# Coinsnap Wallet for BTCPay Server

## Product and Technical Specification

## 1. Goal

Build a BTCPay Server plugin called:

**Coinsnap Wallet for BTCPay Server**

The plugin allows merchants using BTCPay Server to accept Lightning payments directly into a Coinsnap Wallet **without operating their own Lightning node**.

The merchant does not need:

* a Coinsnap Merchant account
* a Coinsnap API key
* a Coinsnap Merchant ID
* a Lightning node
* Lightning channels
* inbound liquidity management
* access to a wallet seed from BTCPay Server

The merchant only needs a Coinsnap Wallet Lightning Address.

Examples:

`jens@coinsnap.app`

or:

`jens.usd@coinsnap.app`

The plugin is **receive-only**.

BTCPay Server manages the payment and transaction records.

Coinsnap Wallet receives the funds.

---

# 2. Product Positioning

Core message:

**Accept Lightning with BTCPay Server without running a Lightning node.**

The architecture is:

BTCPay Server
→ Coinsnap BTCPay Plugin
→ Coinsnap Lightning Address
→ Coinsnap Wallet
→ merchant receives funds

Coinsnap is not the merchant payment backend in this integration.

The merchant continues to use BTCPay Server for:

* invoices
* orders
* payment status
* transaction history
* store management
* accounting-related payment information

Coinsnap Wallet only provides the Lightning receiving destination.

---

# 3. Merchant Experience

The merchant should not have to understand BTCPay Lightning connection strings.

The configuration should be as simple as possible.

Example:

## Coinsnap Wallet

**Receive Lightning payments directly to your Coinsnap Wallet without running a Lightning node.**

Lightning Address:

`[ jens@coinsnap.app ]`

Possible helper text:

> Enter the Lightning Address of the Coinsnap Wallet account where you want to receive payments.

Examples:

`yourname@coinsnap.app` — Bitcoin account

`yourname.usd@coinsnap.app` — Dollars account

Additional information:

> Receive only. BTCPay Server cannot access or spend funds from your Coinsnap Wallet.

> No Coinsnap Merchant account is required.

The merchant should never have to manually enter:

`type=coinsnap;ln-address=jens@coinsnap.app;`

If BTCPay requires such a connection string internally, the plugin must generate it automatically.

---

# 4. Internal Connection Type

Internal BTCPay Lightning connection type:

`coinsnap`

Internal connection string:

`type=coinsnap;ln-address=<lightning-address>;`

Example:

`type=coinsnap;ln-address=jens@coinsnap.app;`

This is an implementation detail and should normally remain hidden from the merchant.

---

# 5. Supported Lightning Addresses

Version 1 supports Coinsnap Lightning Addresses only.

Supported domain:

`coinsnap.app`

Valid examples:

`jens@coinsnap.app`

`jens.usd@coinsnap.app`

`shop@coinsnap.app`

`store.one@coinsnap.app`

Invalid examples:

`jens@blink.sv`

`jens@example.com`

`jens`

`@coinsnap.app`

The plugin should:

* trim surrounding whitespace
* validate the Lightning Address syntax
* compare the domain case-insensitively
* reject Lightning Addresses outside the approved Coinsnap domain allowlist

Do not modify the local part of the Lightning Address unless Coinsnap explicitly confirms that this is safe.

---

# 6. BTC and Dollar Accounts

The plugin must treat the Lightning Address as an opaque destination identifier.

For example:

`jens@coinsnap.app`

and:

`jens.usd@coinsnap.app`

must use exactly the same plugin code path.

The BTCPay plugin must not implement:

* `currency=BTC`
* `currency=USD`
* USDB routing
* Spark Stable Balance logic
* BTC/USD account detection

Coinsnap Wallet is responsible for determining which wallet account receives the payment.

This allows Coinsnap to introduce additional wallet account types later without requiring changes to the BTCPay plugin.

---

# 7. BTCPay Invoice Currency

Do not confuse the currency of the BTCPay invoice with the Coinsnap Wallet account type.

Example:

A BTCPay invoice may be denominated in EUR.

BTCPay calculates the required Lightning amount.

The Coinsnap plugin requests a Lightning invoice for that amount.

The destination could still be:

`jens.usd@coinsnap.app`

The customer pays a normal Bitcoin Lightning invoice.

Any conversion into a Dollar/USDB balance happens inside Coinsnap Wallet and is outside the scope of this plugin.

---

# 8. Payment Flow

For a normal fixed-amount BTCPay invoice:

1. BTCPay requests a Lightning invoice from the Coinsnap Lightning client.

2. The plugin resolves the configured Coinsnap Lightning Address.

Example:

`jens@coinsnap.app`

resolves via:

`https://coinsnap.app/lnurlp/jens`

3. The plugin reads the LNURL-pay response.

4. The plugin validates:

* `tag`
* callback URL
* `minSendable`
* `maxSendable`
* metadata
* allowed host

5. The plugin validates that the requested amount is a whole number of satoshis.

Requirement:

`amount_msat % 1000 == 0`

Do not silently round.

6. The plugin checks that the requested amount is inside:

`minSendable`

and:

`maxSendable`

7. The plugin calls the LNURL-pay callback.

Example:

`https://coinsnap.app/lnurlp/jens/invoice?amount=<msat>&expiry=<seconds>`

8. Coinsnap returns:

* BOLT11 invoice in `pr`
* LUD-21 verification URL in `verify`

9. The plugin validates the returned BOLT11.

10. The plugin gives the BOLT11 to BTCPay.

11. BTCPay presents the Lightning invoice to the customer.

12. The customer pays.

13. The plugin polls the returned LUD-21 `verify` URL.

14. Once settlement is cryptographically confirmed, the plugin reports the Lightning invoice as paid to BTCPay.

15. BTCPay updates the BTCPay invoice according to its normal payment processing rules.

---

# 9. LNURL-Pay

The plugin must use standard Lightning Address / LNURL-pay behavior.

Example Lightning Address endpoint:

`https://coinsnap.app/lnurlp/jens`

The plugin must read the server-provided:

* callback
* `minSendable`
* `maxSendable`
* metadata

Do not hardcode minimum or maximum payment amounts.

The LNURL callback may support comments, but comments are not required for version 1.

Coinsnap currently limits comments to 255 characters.

Do not transmit BTCPay order or customer data as an LNURL comment by default.

---

# 10. Invoice Callback

The plugin calls the callback with:

`amount=<amount-in-msat>`

and should explicitly specify an invoice expiry using:

`expiry=<seconds>`

Example:

`/lnurlp/jens/invoice?amount=100000000&expiry=900`

The exact expiry strategy should follow BTCPay requirements where possible.

The plugin must not simply rely on the Coinsnap backend default expiry.

The backend default is currently 30 days, which is unsuitable for most BTCPay payment flows.

The authoritative expiry is always the expiry encoded in the returned BOLT11 invoice.

---

# 11. Callback Response

The Coinsnap callback returns conceptually:

```json
{
  "pr": "lnbc...",
  "routes": [],
  "verify": "https://coinsnap.app/verify/<payment_hash>"
}
```

The plugin must use the explicit `verify` URL returned by the callback.

Do not construct or guess the verification URL when the explicit LUD-21 URL is available.

---

# 12. BOLT11 Validation

Before giving the BOLT11 invoice to BTCPay, validate at minimum:

* BOLT11 syntax
* Bitcoin network
* encoded amount
* payment hash
* expiry
* invoice is not already expired

The amount encoded in the BOLT11 must exactly equal the amount requested by BTCPay.

If the amount differs:

reject the invoice.

Do not silently correct or round the amount.

The BOLT11 payment hash should be used as the canonical Lightning payment identifier where appropriate.

---

# 13. Settlement Detection

Settlement is detected via the LUD-21 verification URL returned with the invoice.

Example:

`https://coinsnap.app/verify/<payment_hash>`

Unpaid response:

```json
{
  "status": "OK",
  "settled": false,
  "preimage": null,
  "pr": "lnbc..."
}
```

Paid response:

```json
{
  "status": "OK",
  "settled": true,
  "preimage": "<preimage>",
  "pr": "lnbc..."
}
```

Unknown payment hash:

```json
{
  "status": "ERROR",
  "reason": "Not found"
}
```

Server-side problem:

```json
{
  "status": "ERROR",
  "reason": "Internal server error"
}
```

---

# 14. Settlement Status Mapping

Use the following logic.

`settled = true`

→ **PAID**

`settled = false` and BOLT11 still valid

→ **PENDING**

`settled = false` and BOLT11 expired

→ **EXPIRED**

`status = ERROR` and reason indicates `Not found`

→ **UNKNOWN**

Other server errors:

→ retry

→ do not change payment state

HTTP failures:

→ retry

→ do not change payment state

Timeout:

→ retry

→ do not change payment state

Invalid JSON:

→ retry/fail safely

→ never report PAID

Important:

`settled=false` means only that payment settlement has not yet reached the LNURL server.

It does not prove that the customer has not paid.

Short settlement propagation delays are normal.

---

# 15. Cryptographic Settlement Verification

For a paid invoice, Coinsnap returns the payment preimage.

The plugin must verify:

`SHA256(preimage) == payment_hash`

If this verification fails:

do not report the invoice as paid.

The plugin should also ensure that any returned `pr` corresponds to the original payment hash.

Payment status handling is security-critical.

Prefer a pending state over a false positive.

---

# 16. Invoice Expiry

Coinsnap's default backend expiry is currently:

**30 days**

The plugin should explicitly request an appropriate expiry through the callback.

Example:

`expiry=900`

for 15 minutes.

Do not hardcode 15 minutes if BTCPay provides an appropriate expiry value for the payment request.

The actual BOLT11 expiry returned by Coinsnap is authoritative.

There is no separate expired status from the Coinsnap verify endpoint.

Therefore the plugin determines expiry from the BOLT11.

---

# 17. Settlement Polling

Settlement detection may use polling.

Requirements:

* avoid excessive requests
* avoid duplicate polling for the same payment hash
* use cancellation tokens
* implement retry logic
* implement bounded backoff
* consider jitter
* handle HTTP 429 appropriately
* stop polling settled invoices
* stop active polling once an invoice has expired where appropriate
* do not treat temporary errors as final status

Use the non-custodial Blink Lightning Address implementation as a technical reference.

---

# 18. Restart Safety

The plugin must remain reliable across BTCPay Server restarts.

Persist enough information to continue tracking previously created invoices.

Relevant information may include:

* store ID
* original Lightning Address
* payment hash
* BOLT11 invoice
* verify URL
* creation timestamp
* expiry timestamp

After restart:

an unpaid Lightning invoice must still be able to become PAID if Coinsnap later reports settlement.

If the merchant changes the configured Lightning Address:

existing invoices must continue using their original verification information.

New invoices use the new address.

---

# 19. Multi-Store Support

Coinsnap Wallet configuration must be store-specific.

Examples:

Store A:

`shop-a@coinsnap.app`

Store B:

`shop-b@coinsnap.app`

Store C:

`owner.usd@coinsnap.app`

Multiple stores may intentionally use the same Coinsnap Lightning Address.

State must never leak between BTCPay stores.

---

# 20. Receive-Only Integration

The Coinsnap Lightning client is receive-only.

BTCPay must not gain access to:

* wallet seed
* private keys
* Spark wallet credentials
* spending credentials

Unsupported features include:

* Lightning sending
* paying invoices
* balance queries
* channel opening
* channel closing
* node management
* automatic refunds from the Coinsnap Wallet

Unsupported operations must fail clearly according to current BTCPay conventions.

Do not return fake balances or fake node information.

---

# 21. LNURL Metadata and Description Hash

Study the Blink implementation:

`BlinkLnurlRequestFilter.cs`

A BOLT11 invoice generated by Coinsnap may commit to the Coinsnap LNURL metadata through the BOLT11 description hash.

BTCPay's own LNURL response may otherwise expose different store metadata.

If these hashes differ, strict payer wallets may reject the invoice.

Implement the equivalent Coinsnap behavior using the current BTCPay plugin interfaces.

The plugin must ensure that the metadata shown through BTCPay is compatible with the metadata committed to by the Coinsnap-generated BOLT11.

It is acceptable in version 1 if the payer wallet displays the Coinsnap Lightning Address rather than the BTCPay store description.

Do not attempt to modify a generated BOLT11.

---

# 22. HTTP Security

Use current .NET and BTCPay HTTP conventions.

Prefer:

`IHttpClientFactory`

Requirements:

* HTTPS only
* certificate validation enabled
* sensible timeouts
* cancellation support
* bounded redirects
* cross-domain redirects rejected
* no localhost
* no private IP ranges
* no file URLs
* no arbitrary merchant-supplied server URL

Callback and verify hosts must be validated against a Coinsnap-operated domain allowlist.

Initial production domain:

`coinsnap.app`

Additional domains may be added later only after confirmation by Coinsnap.

---

# 23. Privacy

Do not send unnecessary BTCPay information to Coinsnap.

Do not transmit:

* customer name
* customer email
* shipping address
* shopping cart contents
* BTCPay API credentials
* store secrets

Only transmit the information required for invoice creation and verification.

---

# 24. Suggested Classes

Suggested structure:

`CoinsnapPlugin.cs`

Registers the plugin and required services.

`CoinsnapLightningConnectionStringHandler.cs`

Handles:

`type=coinsnap;ln-address=...;`

`CoinsnapLnAddressLightningClient.cs`

Implements the current BTCPay Lightning client interface.

`CoinsnapLnurlRequestFilter.cs`

Handles LNURL metadata compatibility.

`CoinsnapLightningAddress.cs`

Optional value object for parsing and validation.

`CoinsnapInvoiceState.cs`

Persisted settlement monitoring state.

`CoinsnapInvoiceStateRepository.cs`

Persistence implementation using an appropriate BTCPay-supported mechanism.

DTOs for:

* LNURL-pay response
* invoice callback response
* LUD-21 verification response

The exact class structure may be adjusted to match current BTCPay architecture.

---

# 25. Reference Implementation

Use the Blink BTCPay plugin as the main architectural reference.

Repository:

https://github.com/Kukks/BTCPayServerPlugins/tree/master/Plugins/BTCPayServer.Plugins.Blink

Relevant files include:

* `BlinkPlugin.cs`
* `BlinkLightningConnectionStringHandler.cs`
* `BlinkLnAddressLightningClient.cs`
* `BlinkLnurlRequestFilter.cs`

Only the non-custodial Lightning Address implementation is relevant.

Do not copy:

* Blink GraphQL
* Blink API authentication
* custodial wallet logic
* Blink wallet IDs
* balance functionality
* sending functionality

Do not perform a simple Blink → Coinsnap search-and-replace.

Create a clean Coinsnap plugin based on the current BTCPay plugin architecture.

---

# 26. BTCPay Reference

Use the current official BTCPay plugin template:

https://github.com/btcpayserver/btcpayserver-plugin-template

Inspect the current BTCPay Server source code and current Lightning client interfaces.

Do not assume that APIs used by the Blink plugin are still current.

Prefer current BTCPay APIs and conventions.

---

# 27. Tests

Automated tests should cover at minimum:

## Address validation

Valid:

`jens@coinsnap.app`

`jens.usd@coinsnap.app`

Invalid:

wrong domain

missing username

missing domain

malformed address

## LNURL metadata

Valid response accepted.

Wrong tag rejected.

Server error handled.

Missing callback rejected.

Invalid callback rejected.

Minimum/maximum amounts enforced.

## Amount handling

Whole satoshis accepted.

Non-whole-satoshi msat value rejected.

No silent rounding.

## Invoice creation

Correct amount sent.

Expiry parameter sent.

Valid BOLT11 parsed.

Wrong amount rejected.

Wrong network rejected.

Expired invoice rejected.

Missing verify URL rejected.

## Settlement

`settled=false` → pending while invoice valid.

`settled=true` → paid after cryptographic verification.

Expired BOLT11 + unsettled → expired.

Not found → unknown.

HTTP error → retry/no state change.

Internal server error → retry/no state change.

Timeout → retry/no state change.

Invalid preimage → never paid.

## Restart

Create unpaid invoice.

Persist state.

Simulate BTCPay restart.

Settle payment.

Confirm settlement is still detected.

## Address change

Create invoice using one Coinsnap address.

Change store configuration.

Existing invoice remains trackable.

New invoices use new address.

## Multiple stores

Verify isolation between stores.

## Receive-only

Sending must be unsupported.

Balance must not be fabricated.

Channel functions must be unsupported.

---

# 28. Documentation

The repository should eventually contain:

* `README.md`
* `SPEC.md`
* `BACKEND_CONFIRMED.md`
* `REFERENCES.md`
* `IMPLEMENTATION_PLAN.md`
* `BACKEND_REQUIREMENTS.md`
* `TESTING.md`
* `THIRD_PARTY_NOTICES.md`

---

# 29. Licensing

The Blink reference repository uses the MIT license.

If substantial Blink source code is copied or adapted:

preserve the copyright and MIT license notices required by the upstream license.

Document reused third-party source in:

`THIRD_PARTY_NOTICES.md`

Do not imply endorsement by the Blink developers.

---

# 30. Out of Scope for Version 1

Do not implement:

* Coinsnap Merchant accounts
* Coinsnap Merchant API
* API authentication
* GraphQL
* wallet balances
* Lightning sending
* automatic refunds
* channel management
* direct Breez SDK integration
* direct Spark SDK integration
* USDB implementation
* arbitrary Lightning Address providers
* OAuth
* wallet creation
* seed handling
* on-chain Coinsnap integration

---

# 31. Acceptance Criteria

Version 1 is successful when:

* the plugin builds against a supported current BTCPay Server version
* a merchant can configure a Coinsnap Lightning Address
* the merchant does not need a Lightning node
* the merchant does not need a Coinsnap Merchant account
* the merchant does not need an API key
* the merchant does not manually enter a connection string
* BTCPay can request a valid Lightning invoice through Coinsnap
* the returned BOLT11 amount is verified
* settlement is detected through LUD-21
* payment preimage is cryptographically verified
* temporary server failures never result in false payment confirmation
* invoice expiry is determined from BOLT11
* invoices remain trackable across BTCPay restart
* configuration works independently for multiple stores
* BTCPay continues to maintain its own payment records
* Coinsnap Wallet receives the funds
* BTCPay cannot spend Coinsnap Wallet funds
* automated tests pass

---

# 32. Product Principle

Always preserve this separation:

**BTCPay Server manages the payment.**

**Coinsnap Wallet receives the funds.**

The merchant experience should be:

**Install plugin → enter Coinsnap Lightning Address → receive Lightning.**
