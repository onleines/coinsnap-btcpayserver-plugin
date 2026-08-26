# Coinsnap Backend — Confirmed Interface

This document contains backend behavior confirmed by the Coinsnap Wallet development team.

Treat these statements as the current backend contract for the initial BTCPay Server plugin implementation.

---

# 1. Authentication

No API key is required for:

* creating Lightning invoices using an existing Coinsnap Lightning Address
* looking up the settlement status of an invoice

The BTCPay plugin does not require access to a Coinsnap Merchant account.

---

# 2. Lightning Address Resolution

Example Coinsnap Lightning Address:

`jens@coinsnap.app`

It can be resolved through:

`https://coinsnap.app/lnurlp/jens`

The plugin should use the standard Lightning Address / LNURL-pay flow.

---

# 3. Invoice Creation

When BTCPay requests an invoice through the Coinsnap Lightning Address, the invoice callback returns both:

* the BOLT11 invoice
* a LUD-21 verification URL

Conceptual response:

```json
{
  "pr": "lnbc...",
  "routes": [],
  "verify": "https://coinsnap.app/verify/<payment_hash>"
}
```

The `verify` URL belongs to that specific invoice.

No wallet access is required.

No balance access is required.

No spending access is required.

---

# 4. Settlement Verification

The verification endpoint can be polled by BTCPay.

## Unpaid

```json
{
  "status": "OK",
  "settled": false,
  "preimage": null,
  "pr": "lnbc..."
}
```

## Paid

```json
{
  "status": "OK",
  "settled": true,
  "preimage": "<preimage>",
  "pr": "lnbc..."
}
```

## Unknown payment hash

```json
{
  "status": "ERROR",
  "reason": "Not found"
}
```

## Server-side problem

```json
{
  "status": "ERROR",
  "reason": "Internal server error"
}
```

---

# 5. Settlement Semantics

Important:

`settled: true`

is only returned after the payment notification has reached the LNURL server.

Therefore:

`settled: false`

means:

**settlement has not been confirmed yet**

It does NOT mean:

**the invoice was definitely not paid**

Short settlement notification delays are normal.

BTCPay must continue polling.

It must not fail an invoice simply because the first verification response contains:

`settled: false`

---

# 6. Recommended Payment Status Mapping

Use:

`settled = true`

→ **PAID**

`settled = false` and BOLT11 still valid

→ **PENDING**

`settled = false` and BOLT11 expired

→ **EXPIRED**

`status = ERROR` with reason `Not found`

→ **UNKNOWN**

Other `ERROR` responses

→ retry

→ do not change state

HTTP/network errors

→ retry

→ do not change state

The settlement endpoint itself does not provide an explicit expired status.

BTCPay must determine expiry from the BOLT11 invoice.

---

# 7. Preimage Verification

After settlement, the verify endpoint returns the payment preimage.

BTCPay can cryptographically verify:

`SHA256(preimage) == payment_hash`

The BTCPay plugin should perform this validation before treating settlement as authoritative.

---

# 8. Callback and Verification Hosts

For:

`jens@coinsnap.app`

the expected callback is conceptually:

`https://coinsnap.app/lnurlp/jens/invoice`

and the verification endpoint is:

`https://coinsnap.app/verify/<payment_hash>`

The backend derives the host from the Lightning Address domain.

Callback and verification normally use the same host.

A verification URL pointing to a different domain would be abnormal.

The BTCPay plugin should therefore use an explicit allowlist of Coinsnap-operated domains.

Initial confirmed production domain:

`coinsnap.app`

Cross-domain redirects should be rejected.

If additional Coinsnap-operated domains are needed later, they must be explicitly added to the allowlist.

---

# 9. Invoice Expiry

The current default expiry for invoices created through this mechanism is:

**30 days**

This is too long for a normal BTCPay payment flow.

The invoice callback accepts an expiry parameter in seconds.

Example:

`/lnurlp/jens/invoice?amount=<msat>&expiry=900`

The BTCPay plugin should actively request an appropriate expiry.

However:

the authoritative expiry is always the expiry encoded inside the returned BOLT11 invoice.

The plugin must parse and use the BOLT11 expiry.

---

# 10. Verification After Expiry

The verification endpoint continues to work after an invoice expires.

There is currently no cleanup routine deleting old verification records.

Old payments therefore remain queryable in practice.

However:

**there is currently no formally documented guaranteed retention period.**

The implementation should not depend on indefinite retention.

---

# 11. Amount Requirements

The requested amount must be a whole number of satoshis.

Therefore:

`amount_msat % 1000 == 0`

must be true.

Amounts that are not exact multiples of 1000 millisatoshis are rejected by the backend.

The plugin must not silently round amounts.

---

# 12. Minimum and Maximum Amounts

Minimum and maximum sendable amounts are provided by the LNURL-pay response.

The BTCPay plugin must read:

`minSendable`

and:

`maxSendable`

from the server.

Do not assume or hardcode payment limits.

---

# 13. Comments

LNURL comments are limited to:

**255 characters**

Comments are not required for version 1 of the BTCPay plugin.

The plugin should not send customer or order information as comments by default.

---

# 14. Wallet Account Routing

The BTCPay plugin should only forward payments to the configured Lightning Address.

Examples:

`jens@coinsnap.app`

and:

`jens.usd@coinsnap.app`

should use exactly the same plugin code path.

The Coinsnap backend is responsible for determining which Coinsnap Wallet account receives the payment.

The BTCPay plugin must not implement:

* BTC account routing
* USD account routing
* USDB handling
* Spark Stable Balance logic
* currency selection

The Lightning Address itself identifies the destination account.

---

# 15. Main Design Principle

For the wallet integration, all that matters is that the payment is forwarded to the configured Coinsnap Lightning Address.

BTCPay Server remains responsible for payment management.

Coinsnap Wallet remains responsible for receiving the funds.

The integration must not require access to wallet spending functionality.
