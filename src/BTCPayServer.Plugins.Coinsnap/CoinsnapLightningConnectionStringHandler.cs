using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap;

public sealed class CoinsnapLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "ln-address", "store-id"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICoinsnapBolt11Parser _bolt11Parser;
    private readonly ICoinsnapInvoiceStateRepository _states;
    private readonly ICoinsnapClock _clock;
    private readonly ILoggerFactory _loggerFactory;

    internal CoinsnapLightningConnectionStringHandler(
        IHttpClientFactory httpClientFactory,
        ICoinsnapBolt11Parser bolt11Parser,
        ICoinsnapInvoiceStateRepository states,
        ICoinsnapClock clock,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _bolt11Parser = bolt11Parser;
        _states = states;
        _clock = clock;
        _loggerFactory = loggerFactory;
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        Dictionary<string, string> values;
        string type;
        try
        {
            values = LightningConnectionStringHelper.ExtractValues(connectionString, out type);
        }
        catch (FormatException)
        {
            throw;
        }

        if (!type.Equals(CoinsnapConstants.ConnectionType, StringComparison.OrdinalIgnoreCase))
        {
            error = null;
            return null;
        }
        if (network != Network.Main)
        {
            error = "Coinsnap Wallet receiving is available only on Bitcoin mainnet.";
            return null;
        }

        var unknown = values.Keys.FirstOrDefault(k => !AllowedKeys.Contains(k));
        if (unknown is not null)
        {
            error = $"The key '{unknown}' is not supported by Coinsnap Wallet connections.";
            return null;
        }
        if (!values.TryGetValue("ln-address", out var rawAddress))
        {
            error = "The key 'ln-address' is required.";
            return null;
        }
        if (!CoinsnapLightningAddress.TryParse(rawAddress, out var address, out error))
        {
            return null;
        }

        values.TryGetValue("store-id", out var storeId);
        if (!string.IsNullOrEmpty(storeId) &&
            (storeId.Length > 128 || storeId.Any(c => char.IsWhiteSpace(c) || c is ';' or '=')))
        {
            error = "The optional store-id is malformed.";
            return null;
        }

        try
        {
            _states.EnsureLoadedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<CoinsnapLightningConnectionStringHandler>()
                .LogWarning(ex, "Could not load persisted Coinsnap invoices");
            error = "Coinsnap invoice tracking state could not be loaded.";
            return null;
        }

        var identity = ConnectionIdentity(address!, storeId);
        var httpClient = _httpClientFactory.CreateClient(CoinsnapConstants.HttpClientName);
        var service = new CoinsnapLnurlService(
            new CoinsnapHttpClient(httpClient),
            _bolt11Parser,
            _states,
            _clock,
            _loggerFactory.CreateLogger<CoinsnapLnurlService>());

        error = null;
        return new CoinsnapLnAddressLightningClient(address!, identity, storeId, network, service, _states);
    }

    internal static string ConnectionIdentity(CoinsnapLightningAddress address, string? storeId) =>
        string.IsNullOrWhiteSpace(storeId) ? $"address:{address.Value}" : $"store:{storeId}";
}
