# Testing and release acceptance

## Automated suite

From the repository root with .NET SDK 10 installed:

```bash
git submodule update --init --recursive
dotnet restore BTCPayServer.Plugins.Coinsnap.slnx
dotnet test BTCPayServer.Plugins.Coinsnap.slnx --configuration Release
```

The tests use scripted HTTP responses and an in-memory durable-state double. They cover strict
Lightning Address and connection-string parsing, HTTPS/host/redirect/private-IP policy, response
limits, metadata and amount bounds, explicit expiry, BOLT11 matching, every LUD-21 status mapping,
cryptographic preimage proof, retry behavior, restart recovery, address changes, store isolation,
listener isolation, metadata mirroring, and receive-only operations.

## Package verification

```bash
./pack.sh
cd artifacts/BTCPayServer.Plugins.Coinsnap/0.1.0
shasum -a 256 BTCPayServer.Plugins.Coinsnap.btcpay
cat SHA256SUMS
unzip -l BTCPayServer.Plugins.Coinsnap.btcpay
```

The archive hash must match the `.btcpay` line in `SHA256SUMS`. The plugin packer must complete
without a dependency or plugin-loading error.

## Local BTCPay smoke test

1. Register the Debug assembly:

   ```bash
   ./plugin-register.sh
   ```

2. Start BTCPay's development dependencies:

   ```bash
   cd submodules/btcpayserver/BTCPayServer.Tests
   docker compose up -d dev
   ```

3. Launch the `BTCPayServer: Bitcoin-HTTPS` profile from the solution/IDE.
4. Confirm startup has no Coinsnap plugin exception.
5. Open a BTC store's Lightning setup. Confirm the Coinsnap tab shows only a Lightning Address,
   examples, the receive-only notice, and **Test connection**.
6. Confirm malformed addresses and non-`coinsnap.app` domains are rejected.
7. Save a valid staging address and reload the page. Confirm the value remains selected and no
   internal connection string is shown in the Coinsnap tab.

## Staging restart test

Use a disposable BTCPay mainnet staging instance and a low-value invoice. Do not use a seed phrase,
API key, or production customer data.

1. Install the package from **Server Settings → Plugins → Upload Plugin**, restart, and configure a
   dedicated store with the intended `@coinsnap.app` address.
2. Create a low-value fixed invoice within the minimum/maximum returned by Coinsnap. Record the
   BTCPay invoice ID, Lightning payment hash, destination address, amount, and BOLT11 expiry.
3. Do not pay yet. Restart BTCPay Server while the BOLT11 is still valid.
4. After restart, confirm the invoice is still payable and has the same BOLT11/payment hash.
5. Pay it from an independent Lightning wallet.
6. Confirm BTCPay changes the invoice to paid/settled and Coinsnap credits the exact destination
   account and satoshi amount.
7. Restart BTCPay again and confirm the paid state remains recorded without a duplicate payment.
8. Repeat with two stores (they may use the same Coinsnap address) and confirm one store never sees
   or receives the other's tracked invoice event.
9. Create an unpaid invoice, change the store to another Coinsnap address, then pay the old BOLT11.
   Confirm the old invoice still settles while a newly created invoice uses the new address.

## Real-payment release gate

Before describing the plugin as production-ready, complete all of the following on the exact package
candidate:

- install and restart successfully on a supported BTCPay release;
- pass **Test connection** for both the intended BTC and/or `.usd` address form;
- create a BOLT11 whose payment hash and amount match BTCPay's payment details;
- complete one small external mainnet payment;
- observe the same invoice become paid only after a valid LUD-21 preimage is returned;
- confirm the amount arrived in the correct Coinsnap account;
- pass the pre-payment and post-payment restart checks;
- retain server logs and redacted test evidence for the release record.

If any item fails, keep the release marked experimental. Never paste wallet seeds or unrelated
credentials into logs, issues, or test reports.

## Failure drills

In a controlled environment, simulate or firewall the endpoint and verify that timeouts, HTTP 429,
HTTP 5xx, invalid JSON, a mismatched returned BOLT11, and an invalid preimage do not mark an invoice
paid or delete pending state. Restore connectivity and confirm polling recovers with bounded
backoff. An explicit LUD-21 `Not found` should remain unknown to a direct lookup and be pruned by the
background poller only after repeated responses.
