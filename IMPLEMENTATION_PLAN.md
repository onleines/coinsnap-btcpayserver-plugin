# Coinsnap Wallet for BTCPay Server: implementation plan

## Scope and target

The first release targets **BTCPay Server 2.4.3** and .NET 10. The plugin dependency is declared as `BTCPayServer >= 2.4.3`, and the development project references the official BTCPay Server source as a Git submodule, following the current plugin template.

The integration is deliberately receive-only. BTCPay creates and owns invoices, orders, payment records, and payment status. Coinsnap Wallet is only the destination that creates the BOLT11 through LNURL-pay and exposes settlement through LUD-21.

The supplied local checkout initially contained no working-tree documentation and the upstream repository could not be read anonymously. After authenticated access became available, `SPEC.md`, `BACKEND_CONFIRMED.md`, and `REFERENCES.md` were read completely and the finished implementation was revalidated against them. Those repository documents and the supplied product brief form the implementation contract. The public BTCPay template, BTCPay Server, BTCPayServer.Lightning, and Blink plugin sources were also inspected directly.

## Current BTCPay APIs

The plugin uses these BTCPay 2.4.3 extension points:

- `BaseBTCPayServerPlugin.Execute(IServiceCollection)` for registration.
- `ILightningConnectionStringHandler.Create(string, Network, out string?)` for `type=coinsnap`.
- `IExtendedLightningClient` for configuration validation and display metadata.
- `IPluginHookFilter` / `PluginHookFilter<LNURLPayRequest>` at `modify-lnurlp-request` to align BTCPay's served LNURL metadata with the metadata committed to by the Coinsnap BOLT11.
- `ISettingsRepository` for database-backed plugin state without a provider-specific plugin schema.
- `IHostedService` for one process-wide settlement poller.
- `ln-payment-method-setup-tabhead` and `ln-payment-method-setup-tab` for the merchant-facing setup form.

The current `ILightningClient` contract includes invoice creation/lookup/listening, payment/send methods, node information and balance, channel methods, deposit address, connection, cancellation, and invoice/payment listing. Coinsnap implements invoice receipt and settlement lookup. Spending, balance, channel, deposit, and node-management methods throw `NotSupportedException`; no synthetic balances or node data are returned.

## Reference concepts

Concepts retained from Blink's Lightning Address work:

- resolve LNURL-pay metadata, then call its callback to obtain a BOLT11;
- use LUD-21 `verify` for settlement;
- require the BOLT11 amount and payment hash to match the request;
- verify `SHA256(preimage) == payment_hash` before reporting paid;
- share polling rather than creating a loop per listener;
- return an unpaid result on temporary verification failure so BTCPay retries;
- mirror provider LNURL metadata for description-hash compatibility;
- make all spending and node operations explicitly unsupported.

Blink functionality excluded:

- GraphQL and API authentication;
- API keys, wallet IDs, custodial accounts, and balances;
- BTC/USD currency flags and USDB routing;
- sending, refunds, and wallet/node/channel management;
- arbitrary Lightning Address domains.

The implementation is clean Coinsnap-specific code rather than a renamed Blink fork. Licensing attribution is documented in `THIRD_PARTY_NOTICES.md`.

## Proposed files and responsibilities

- `CoinsnapPlugin.cs`: registrations, named HTTP client, UI extensions, hook, and poller.
- `CoinsnapLightningAddress.cs`: strict parsing and the `@coinsnap.app` restriction.
- `CoinsnapUrlPolicy.cs`: HTTPS/host/port/user-info/redirect validation.
- `CoinsnapLightningConnectionStringHandler.cs`: parse `type=coinsnap`, validate `ln-address`, construct a client.
- `CoinsnapLnAddressLightningClient.cs`: receive-only `IExtendedLightningClient` implementation.
- `CoinsnapLnurlService.cs`: metadata lookup, invoice creation, BOLT11 validation, and LUD-21 verification.
- `CoinsnapDtos.cs`: strongly typed LNURL and LUD-21 payloads.
- `CoinsnapInvoiceState.cs`: restart-safe state for one BOLT11.
- `CoinsnapInvoiceStateRepository.cs`: thread-safe persistence through BTCPay settings.
- `CoinsnapSettlementPoller.cs`: one bounded, jittered, backoff-aware poller.
- `CoinsnapInvoiceListener.cs`: cheap event subscriber, with no polling loop.
- `CoinsnapLnurlRequestFilter.cs`: metadata and min/max alignment.
- Razor partials: Coinsnap-specific address UI which builds the internal connection string in the browser.

## LNURL-pay and invoice flow

1. Parse and normalize `local-part@coinsnap.app` without interpreting `.usd`.
2. GET `https://coinsnap.app/lnurlp/{escaped-local-part}`.
3. Require `tag=payRequest`, a valid allowlisted HTTPS callback, and valid positive `minSendable`/`maxSendable` bounds.
4. Require a positive fixed amount divisible by 1,000 msat and within the advertised bounds.
5. Call the callback with `amount=<exact-msat>` and `expiry=<positive-seconds>` while preserving any existing callback query.
6. Require a successful response containing `pr` and an explicit allowlisted `verify` URL.
7. Parse BOLT11 against the BTCPay network and require exact amount, payment hash, non-expired encoded expiry, and (when BTCPay supplied a description hash) the same description hash.
8. Persist the original address, BOLT11, payment hash, verify URL, store identity, creation time, and encoded expiry before returning the invoice.

No customer, order, cart, email, shipping, API-key, or store-secret data is sent.

## LUD-21 and status mapping

Every response is validated against the original persisted state:

- `settled=true` is `Paid` only if the returned `pr` has the original payment hash and the returned 32-byte preimage hashes to that payment hash.
- `settled=false` is `Unpaid` while the encoded BOLT11 is valid and `Expired` after its encoded expiry.
- `status=ERROR, reason=Not found` is unknown (`GetInvoice` returns `null`).
- other `ERROR`, HTTP failures (including 429), timeouts, cancellation unrelated to shutdown, malformed JSON, missing fields, BOLT11 mismatch, and invalid preimages are temporary failures. They never produce `Paid` and never delete/change a payment record.

If a response says settled but its proof is invalid, the invoice remains pending even after nominal expiry. This deliberately prefers a retry over a false paid or false expired result.

## Expiry strategy

The callback always receives `expiry=<seconds>`. `CreateInvoiceParams.Expiry` is rounded up to a positive whole second and capped to an integer query value. The simple overload uses the expiry supplied by BTCPay. A 15-minute default is used only by internal test/config probing where BTCPay supplied no invoice expiry.

The authoritative expiry is `BOLT11PaymentRequest.ExpiryDate`; the callback query is only a request. An already-expired returned invoice is rejected.

## Polling strategy

One hosted poller owns all verification traffic. Listeners only subscribe to its settlement event.

- global concurrency is capped at 8;
- a payment hash has at most one in-flight poll;
- fresh invoices poll around 3 seconds, invoices older than 2 minutes around 10 seconds, and invoices older than 10 minutes around 30 seconds;
- temporary failures use capped exponential backoff up to 2 minutes plus ±20% jitter;
- HTTP 429 `Retry-After` is honored within the same cap;
- all waits and HTTP operations accept cancellation;
- settled and expired invoices stop network polling and remain queryable for a one-hour grace period;
- explicit unknown invoices are removed only after repeated `Not found` responses in the background poller, avoiding the creation/visibility race.

## Persistence, restart, address change, and isolation

The repository stores a single versioned snapshot through BTCPay's `ISettingsRepository`. Writes are serialized and invoice creation is persisted before the BOLT11 is returned. The handler and hosted poller both ensure the snapshot is loaded, so startup ordering cannot cause a tracked invoice to be lost.

Each state includes a store identity. The merchant UI automatically adds `store-id` to the hidden internal connection string. Listener/list operations filter by store identity, and payment hashes remain globally unique. A fallback connection identity based on the Lightning Address exists for non-UI/API configurations that omit `store-id`.

The verify URL and original address live on the invoice state, not on current store configuration. Changing a store address therefore affects only new invoices. Old invoices continue using their persisted original BOLT11 and verify URL.

Settled state is persisted before notification. This lets a restart between Coinsnap settlement and BTCPay payment recording recover safely. Terminal state is pruned after a grace period.

## LNURL metadata strategy

For a store whose enabled BTC Lightning connection is `type=coinsnap`, the request filter fetches the configured Coinsnap address's LNURL metadata. It replaces BTCPay's metadata with the exact Coinsnap `metadata` string and narrows BTCPay's sendable range to the intersection with Coinsnap's range. This makes strict payer wallets see the same description hash that Coinsnap committed to in the BOLT11. The displayed identity may therefore be the Coinsnap Lightning Address rather than the BTCPay store description.

Filter failure does not crash checkout; invoice creation still performs strict description-hash validation and will fail safely if the values cannot match.

## HTTP security strategy

- Only `https://coinsnap.app` is allowlisted in version 1.
- User info, fragments, non-default ports, IP literals, localhost/private host names, HTTP, and other schemes are rejected.
- Automatic redirects are disabled. At most two redirects are followed manually, and every target is revalidated against the same HTTPS allowlist; cross-domain redirects are rejected.
- `IHttpClientFactory` supplies clients with normal platform TLS validation and a 20-second timeout.
- The connection callback resolves `coinsnap.app`, rejects the complete response if any address is
  private/reserved, and connects only to the validated address set (DNS-rebinding protection).
- System HTTP proxies are bypassed so a proxy cannot redirect allowlisted traffic to an unvalidated
  network destination; proxy-only deployments are a documented version-1 limitation.
- Response bodies are bounded before JSON parsing.
- Logging uses structured payment-hash/address fields and contains no wallet secrets (none exist).

## Test strategy

Unit and component-style tests use scripted `HttpMessageHandler` responses and an in-memory state store. They cover address/URL policy, metadata, amounts, expiry query, BOLT11 amount/network/expiry/hash checks, all settlement mappings and proof checks, retry behavior, persistence/restart, address changes, multi-store filtering, metadata mirroring, connection parsing, and unsupported operations.

A real mainnet payment is explicitly outside automated tests and must be performed only after installing the built plugin in a disposable/staging BTCPay Server. The plugin must not be called production-ready until that succeeds.

## Licensing implications

The Blink repository and official BTCPay sources/templates are MIT licensed. The implementation follows their public extension patterns and adapts the non-custodial LNURL/LUD-21 concepts. The required MIT notices and non-endorsement statement are in `THIRD_PARTY_NOTICES.md`.
