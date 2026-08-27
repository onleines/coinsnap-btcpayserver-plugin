# Coinsnap Wallet for BTCPay Server

**Accept Lightning payments with BTCPay Server without running your own Lightning node.**

Coinsnap Wallet for BTCPay Server is a receive-only Lightning integration that lets a BTCPay merchant send Lightning payments directly to a Coinsnap Wallet.

The merchant only needs a Coinsnap Lightning Address such as:

`yourname@coinsnap.app`

No Coinsnap Merchant account, API key, Lightning node, channel management, wallet seed, or spending credentials are required.

## How it works

The merchant continues to use BTCPay Server for:

- invoices
- orders
- payment status
- transaction records
- store management

Coinsnap Wallet is only the receiving wallet.

The payment flow is:

**Customer → BTCPay Server → Coinsnap Plugin → Coinsnap Lightning Address → Coinsnap Wallet**

BTCPay requests a Lightning invoice through the configured Coinsnap Lightning Address and automatically detects settlement through LUD-21 payment verification.

BTCPay never receives access to the Coinsnap Wallet seed or spending keys.

## Current status

Version **0.1.0** is an early testing release.

Current target:

- BTCPay Server 2.4.3 or later
- Bitcoin mainnet
- Coinsnap Lightning Addresses on `coinsnap.app`
- receive-only Lightning payments

The plugin has automated test coverage, but the current version should be considered **beta/test software until real mainnet BTCPay payments have been successfully tested by multiple environments**.

## Merchant requirements

You need:

- BTCPay Server 2.4.3+
- permission to install and restart BTCPay plugins
- a Coinsnap Wallet
- a Coinsnap Lightning Address ending in `@coinsnap.app`

Example:

`yourname@coinsnap.app`

No Coinsnap Merchant account is required.

No API key is required.

## Wallet accounts

The plugin treats the Lightning Address as the destination account.

Example:

`yourname@coinsnap.app`

and, where supported by Coinsnap Wallet:

`yourname.usd@coinsnap.app`

The BTCPay plugin itself does not contain BTC/USD or USDB conversion logic. Account routing is handled entirely by Coinsnap Wallet.

## Configure BTCPay Server

Open:

**Store Settings → Payment Methods → Lightning**

Select:

**Coinsnap Wallet**

Enter your Coinsnap Lightning Address:

`yourname@coinsnap.app`

Then use:

**Test connection**

The plugin validates the Lightning Address and retrieves its LNURL-pay metadata.

After saving the configuration, BTCPay can create Lightning invoices through the Coinsnap Wallet integration.

The internal BTCPay connection string is generated automatically.

Merchants do not need to enter or edit:

`type=coinsnap;ln-address=...;`

## Payment flow

For every payment the plugin:

1. resolves the Coinsnap Lightning Address through LNURL-pay;
2. reads the server-provided payment limits;
3. requests a BOLT11 Lightning invoice for the exact amount;
4. validates the returned invoice;
5. stores the explicit LUD-21 verification URL;
6. returns the Lightning invoice to BTCPay;
7. polls the verification endpoint;
8. reports the payment as paid only after settlement is confirmed.

For settled payments the plugin verifies:

`SHA256(preimage) == payment_hash`

A temporary server error or `settled=false` response is never interpreted as payment success.

## Receive-only security model

The plugin is intentionally receive-only.

BTCPay Server cannot:

- spend Coinsnap Wallet funds
- pay Lightning invoices
- query wallet balances
- open or close channels
- access wallet seeds or private keys
- automatically refund from the Coinsnap Wallet

Refunds or outgoing payments must be performed separately from the merchant's wallet.

## Privacy

The plugin does not send Coinsnap:

- customer names
- customer email addresses
- shipping information
- cart contents
- BTCPay API credentials
- wallet spending credentials

Only the information required to create and verify a Lightning payment is transmitted.

## Build and test

Requirements:

- .NET SDK 10
- Git submodules
- Docker for local BTCPay development

```bash
git submodule update --init --recursive
dotnet restore BTCPayServer.Plugins.Coinsnap.slnx
dotnet test BTCPayServer.Plugins.Coinsnap.slnx
./pack.sh
```

The package script creates the BTCPay plugin package and checksum under:

`artifacts/BTCPayServer.Plugins.Coinsnap/<version>/`

For detailed testing instructions see:

`TESTING.md`

## Current limitations

Version 0.1.0 currently supports:

- Bitcoin mainnet only
- fixed positive Lightning amounts
- whole-satoshi amounts only
- Coinsnap Lightning Addresses on `coinsnap.app`
- receive-only operation

Automatic refunds, outgoing Lightning payments, wallet balance access, channel management and arbitrary Lightning Address providers are outside the scope of version 0.1.0.

## Documentation

See:

- `SPEC.md` — product and technical specification
- `BACKEND_CONFIRMED.md` — confirmed Coinsnap LNURL/LUD-21 behavior
- `IMPLEMENTATION_PLAN.md` — architecture and implementation decisions
- `BACKEND_REQUIREMENTS.md` — open backend questions
- `TESTING.md` — manual and automated testing
- `REFERENCES.md` — upstream references
- `THIRD_PARTY_NOTICES.md` — third-party attribution

## License

MIT License.

This project uses architectural patterns inspired by BTCPay Server and the MIT-licensed Blink BTCPay plugin.

See `THIRD_PARTY_NOTICES.md`.

No upstream project or contributor endorses this plugin.
