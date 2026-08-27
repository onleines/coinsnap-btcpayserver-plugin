using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.Coinsnap;

public sealed record CoinsnapLightningAddress
{
    private static readonly Regex LocalPartPattern = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,62}[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private CoinsnapLightningAddress(string localPart)
    {
        LocalPart = localPart;
    }

    public string LocalPart { get; }
    public string Domain => CoinsnapConstants.Host;
    public string Value => $"{LocalPart}@{Domain}";
    public Uri MetadataUri => new($"https://{CoinsnapConstants.Host}/lnurlp/{Uri.EscapeDataString(LocalPart)}");

    public static CoinsnapLightningAddress Parse(string value)
    {
        if (!TryParse(value, out var address, out var error))
            throw new FormatException(error);
        return address!;
    }

    public static bool TryParse(string? value, out CoinsnapLightningAddress? address, out string? error)
    {
        address = null;
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "A Coinsnap Lightning Address is required.";
            return false;
        }

        var firstAt = normalized.IndexOf('@');
        if (firstAt <= 0 || firstAt != normalized.LastIndexOf('@') || firstAt == normalized.Length - 1)
        {
            error = "Enter a valid Lightning Address such as yourname@coinsnap.app.";
            return false;
        }

        var localPart = normalized[..firstAt];
        var domain = normalized[(firstAt + 1)..];
        if (!domain.Equals(CoinsnapConstants.Host, StringComparison.OrdinalIgnoreCase))
        {
            error = "Coinsnap Wallet version 1 supports only @coinsnap.app Lightning Addresses.";
            return false;
        }

        if (!LocalPartPattern.IsMatch(localPart) || localPart.Contains("..", StringComparison.Ordinal))
        {
            error = "The Coinsnap Lightning Address username is malformed.";
            return false;
        }

        address = new CoinsnapLightningAddress(localPart);
        error = null;
        return true;
    }

    public override string ToString() => Value;
}
