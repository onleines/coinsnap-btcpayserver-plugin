# Technical References

This document lists the main upstream projects and specifications that should be studied when implementing Coinsnap Wallet for BTCPay Server.

---

# 1. Blink BTCPay Server Plugin

Repository:

https://github.com/Kukks/BTCPayServerPlugins

Relevant plugin:

https://github.com/Kukks/BTCPayServerPlugins/tree/master/Plugins/BTCPayServer.Plugins.Blink

The Blink plugin is the primary architectural reference for the Coinsnap integration because it already implements a receive-only Lightning Address model suitable for wallets that do not expose Lightning node credentials to BTCPay Server.

## Important files to inspect

### BlinkPlugin.cs

Study how the plugin registers its BTCPay services and Lightning integration.

### BlinkLightningConnectionStringHandler.cs

Study how Blink integrates a custom Lightning connection type into BTCPay Server.

For Coinsnap, the connection handler should be significantly simpler because Coinsnap version 1 only supports the receive-only Lightning Address model.

### BlinkLnAddressLightningClient.cs

This is the most important reference.

Study:

* Lightning Address resolution
* LNURL-pay invoice creation
* BOLT11 parsing
* payment hash handling
* settlement polling
* invoice status mapping
* error handling
* retry behavior
* implementation of the BTCPay Lightning client interface

Only the non-custodial Lightning Address path is relevant.

### BlinkLnurlRequestFilter.cs

Study how Blink handles LNURL metadata and BOLT11 description hash compatibility.

Coinsnap will likely require the same architectural concept.

---

# 2. Blink Functionality That Must NOT Be Copied

Coinsnap version 1 does not need:

* Blink GraphQL
* Blink API authentication
* API keys
* custodial accounts
* wallet IDs
* balance functionality
* Lightning sending
* channel functionality
* Blink-specific currency handling
* `currency=USD`

Do not fork the complete Blink plugin and perform a Blink-to-Coinsnap search-and-replace.

Use the proven non-custodial Lightning Address patterns only.

---

# 3. BTCPay Server Plugin Template

Official template:

https://github.com/btcpayserver/btcpayserver-plugin-template

The new Coinsnap plugin should be created using the current official BTCPay plugin architecture.

Prefer the current BTCPay plugin APIs over older APIs found in third-party plugins.

---

# 4. BTCPay Server

Repository:

https://github.com/btcpayserver/btcpayserver

Inspect the current BTCPay implementation when necessary for:

* Lightning payment methods
* plugin registration
* store-specific Lightning configuration
* LNURL-pay integration
* payment method configuration
* persistence
* UI extension points

---

# 5. BTCPay Lightning Abstractions

Repository/project to inspect:

https://github.com/btcpayserver/BTCPayServer.Lightning

The implementation must use the current Lightning client interfaces expected by the target BTCPay Server version.

In particular, inspect the current definition of:

`ILightningClient`

Do not assume that the interface used by the Blink plugin is still identical to the current BTCPay version.

---

# 6. Lightning Address

Coinsnap Lightning Address example:

`jens@coinsnap.app`

Current endpoint:

`https://coinsnap.app/lnurlp/jens`

The plugin should use Lightning Address as the merchant-facing wallet identifier.

---

# 7. LNURL-pay

The plugin uses the standard LNURL-pay flow.

Relevant specification family:

LUD-06 / LNURL-pay

The plugin must use server-provided:

* callback
* `minSendable`
* `maxSendable`
* metadata

The Coinsnap invoice callback accepts:

`amount=<msat>`

and supports:

`expiry=<seconds>`

---

# 8. LUD-21 Payment Verification

The Coinsnap invoice callback returns an explicit:

`verify`

URL.

Example:

`https://coinsnap.app/verify/<payment_hash>`

This endpoint is used to detect settlement.

Important behavior:

* `settled=true` means paid
* `settled=false` means not yet confirmed
* expiry must be determined from BOLT11
* temporary server errors must be retried
* payment preimage should be verified against the payment hash

---

# 9. Licensing

The Kukks BTCPayServerPlugins repository uses the MIT license.

Before copying or adapting substantial portions of the Blink implementation:

* inspect the upstream license
* preserve required copyright notices
* preserve the MIT permission notice where required
* document copied/adapted code in `THIRD_PARTY_NOTICES.md`

Do not imply that Blink developers endorse the Coinsnap plugin.

---

# 10. Implementation Priority

Use references in this order:

1. Current official BTCPay Server APIs
2. Current official BTCPay plugin template
3. Current BTCPay Lightning abstractions
4. Blink non-custodial Lightning Address implementation
5. Coinsnap confirmed backend behavior in `BACKEND_CONFIRMED.md`

If Blink conflicts with the current BTCPay architecture:

follow the current BTCPay architecture.

If a generic implementation assumption conflicts with:

`BACKEND_CONFIRMED.md`

follow:

`BACKEND_CONFIRMED.md`
