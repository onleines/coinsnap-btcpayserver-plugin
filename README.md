# Coinsnap Wallet for BTCPay Server

Coinsnap Wallet is a receive-only Lightning integration for BTCPay Server. A merchant enters one
Coinsnap Lightning Address; BTCPay creates and owns the invoice and payment state, while the payment
is delivered directly to that Coinsnap Wallet account.

Version 0.1.0 targets **BTCPay Server 2.4.3 or later**, Bitcoin mainnet, and the public
`coinsnap.app` LNURL-pay/LUD-21 endpoints.

> This first version has comprehensive automated coverage but has not been proven by a real
> mainnet payment in this repository. Treat it as a staging build until the manual acceptance test
> in [TESTING.md](TESTING.md) succeeds in your environment.

## What the merchant needs

- A BTCPay Server 2.4.3+ administrator who can install and restart plugins.
- A Coinsnap Wallet Lightning Address ending exactly in `@coinsnap.app`.
- No Coinsnap merchant account, API key, wallet ID, node credentials, or seed phrase.

Both account forms are preserved exactly:

- `yourname@coinsnap.app` routes to the Bitcoin account.
- `yourname.usd@coinsnap.app` routes to the Dollars account.

## Install a release package

1. Obtain `BTCPayServer.Plugins.Coinsnap.btcpay` from a trusted release and verify its published
   SHA-256 checksum.
2. In BTCPay Server, open **Server Settings → Plugins**.
3. Expand **Upload Plugin**, select the `.btcpay` file, and upload it.
4. Restart BTCPay Server when prompted. Confirm that **Coinsnap Wallet for BTCPay Server** is listed
   and that its dependency check reports no error.

Plugins execute inside the BTCPay Server process. Install only a package whose source and checksum
you trust.

## Configure a store

1. Open the store's **Settings → Payment methods → Lightning** setup.
2. Select **Coinsnap Wallet**.
3. Enter the destination Lightning Address, for example `jens@coinsnap.app` or
   `jens.usd@coinsnap.app`.
4. Choose **Test connection**. This validates the address and retrieves its LNURL-pay metadata.
5. Save and enable the store's Lightning payment method.
6. If the store exposes its own LNURL-pay or Lightning Address, also enable BTCPay's LNURL payment
   method. The plugin then mirrors Coinsnap's metadata so strict payer wallets see the description
   hash committed to by the returned BOLT11.

The UI creates the internal value `type=coinsnap;ln-address=…;store-id=…;` automatically. Merchants
do not need to view or edit it.

## Payment flow and trust boundary

For each fixed, whole-satoshi invoice the plugin:

1. retrieves `https://coinsnap.app/lnurlp/{username}`;
2. validates the advertised minimum/maximum and calls the returned callback with the exact millisat
   amount and requested expiry;
3. validates the returned BOLT11 amount, mainnet encoding, expiry, payment hash and, when present,
   description hash;
4. persists the BOLT11 and explicit LUD-21 `verify` URL before returning it to BTCPay;
5. reports paid only after the verify response returns the same invoice and a preimage for which
   `SHA256(preimage) == payment_hash`.

Only HTTPS URLs on the exact `coinsnap.app` host and default port are accepted. Redirects are
limited and revalidated; DNS answers resolving to private or reserved addresses are rejected.
Response sizes, HTTP timeouts, polling concurrency, backoff, jitter, and `Retry-After` are bounded.

No customer, order, cart, email, shipping, merchant secret, or spending credential is sent to
Coinsnap. The integration cannot pay invoices, refund, inspect balances, obtain deposit addresses,
or manage a node or channels.

## Build, test, and package

Requirements: .NET SDK 10, Git submodules, and (for local BTCPay debugging) Docker.

```bash
git submodule update --init --recursive
dotnet restore BTCPayServer.Plugins.Coinsnap.slnx
dotnet test BTCPayServer.Plugins.Coinsnap.slnx
./pack.sh
```

The pack script uses BTCPay Server's official plugin packer and writes the `.btcpay` archive,
metadata JSON, and `SHA256SUMS` below `artifacts/BTCPayServer.Plugins.Coinsnap/<version>/`.
After an explicit restore, `NO_RESTORE=1 ./pack.sh` provides an offline packaging pass.

For a local BTCPay debug session, run `./plugin-register.sh`, start the dependencies described in
[TESTING.md](TESTING.md), and launch BTCPay Server's `Bitcoin-HTTPS` profile.

## Operational behavior and limitations

- Fixed positive amounts only; millisatoshis must be divisible by 1,000.
- Bitcoin mainnet only.
- Receive-only; BTCPay refunds/payouts need a separately configured spending wallet.
- One process-wide poller checks settlement. There is no Coinsnap webhook or websocket dependency.
- Active invoice state survives process restarts. Changing the configured address affects new
  invoices only; old invoices retain their original verify URL and destination association.
- Explicit `Not found` is treated as unknown. Other protocol/HTTP failures keep state unchanged and
  retry. Invalid settlement proof is never considered paid.
- The security-hardened HTTP client bypasses system HTTP proxies. Deployments that require an
  outbound proxy cannot reach Coinsnap with this version.
- State is stored through BTCPay's settings repository. Do not edit the internal connection string
  or persisted state by hand.

The authoritative product and confirmed backend contract are documented in [SPEC.md](SPEC.md) and
[BACKEND_CONFIRMED.md](BACKEND_CONFIRMED.md). Upstream sources are listed in
[REFERENCES.md](REFERENCES.md), open operational questions in
[BACKEND_REQUIREMENTS.md](BACKEND_REQUIREMENTS.md), and architecture decisions in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).

## License and attribution

This project is MIT licensed. It adapts extension patterns from BTCPay Server and LNURL/LUD-21
concepts studied in the MIT-licensed BTCPay Blink plugin. See
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). No upstream project or contributor endorses this
plugin.
