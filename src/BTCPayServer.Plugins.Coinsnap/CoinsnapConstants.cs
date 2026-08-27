namespace BTCPayServer.Plugins.Coinsnap;

internal static class CoinsnapConstants
{
    public const string ConnectionType = "coinsnap";
    public const string Host = "coinsnap.app";
    public const string HttpClientName = "BTCPayServer.Plugins.Coinsnap";
    public const string SettingsName = "Coinsnap.TrackedInvoices.v1";
    public const int MaxResponseBytes = 256 * 1024;
    public const int MaxRedirects = 2;
}
