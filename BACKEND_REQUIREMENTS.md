# Coinsnap backend requirements and open questions

## Required contract

Version 1 relies only on the confirmed public LNURL/LUD-21 flow:

- `https://coinsnap.app/lnurlp/{username}` returns LNURL-pay metadata.
- Its callback accepts exact `amount` in millisatoshis and `expiry` in seconds.
- The callback returns a BOLT11 in `pr` and an absolute LUD-21 URL in `verify`.
- The verify response returns `status`, `settled`, `preimage`, and `pr` as documented in the product brief.

No private Coinsnap API, merchant account, merchant ID, wallet ID, API key, seed, or spending permission is required or used.

## Open operational questions

These are not blockers for the implementation because the client fails safely:

1. What are the documented rate limits for `/lnurlp`, invoice callbacks, and `/verify`? The poller currently caps process-wide concurrency at 8 and backs off through 120 seconds, honoring HTTP `Retry-After`.
2. What maximum `expiry` value does the callback accept? The plugin passes BTCPay's requested positive whole-second expiry and trusts the returned BOLT11 as authoritative.
3. Are same-host redirects ever intentionally returned? The plugin permits at most two redirects only when every target remains HTTPS on `coinsnap.app`.
4. Is the production certificate/host allowlist expected to expand beyond `coinsnap.app`? Version 1 intentionally rejects every other host.
5. What settlement-notification delay distribution should operators expect? The plugin treats `settled=false` as pending until BOLT11 expiry and never as proof of non-payment.

## Security invariants expected from the backend

- `pr` must be the invoice created by the callback and must retain the same payment hash in later verify responses.
- When `settled=true`, `preimage` must be 32 bytes encoded as 64 hexadecimal characters and hash to the BOLT11 payment hash.
- The metadata string returned by `/lnurlp` must be the string committed to by the BOLT11 description hash.
- BTC and dollar account routing is determined entirely by the unchanged Lightning Address local part (for example, `name` versus `name.usd`).

Any violation leaves the BTCPay payment pending; it is never promoted to paid.
