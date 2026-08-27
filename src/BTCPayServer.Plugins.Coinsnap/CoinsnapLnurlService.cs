using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap;

internal enum CoinsnapVerificationOutcome
{
    Pending,
    Paid,
    Expired,
    Unknown,
    Retry
}

internal sealed record CoinsnapVerificationResult(
    CoinsnapVerificationOutcome Outcome,
    LightningInvoice? Invoice,
    TimeSpan? RetryAfter = null,
    string? Detail = null);

internal sealed class CoinsnapLnurlService
{
    private readonly CoinsnapHttpClient _http;
    private readonly ICoinsnapBolt11Parser _bolt11Parser;
    private readonly ICoinsnapInvoiceStateRepository _states;
    private readonly ICoinsnapClock _clock;
    private readonly ILogger<CoinsnapLnurlService> _logger;

    public CoinsnapLnurlService(
        CoinsnapHttpClient http,
        ICoinsnapBolt11Parser bolt11Parser,
        ICoinsnapInvoiceStateRepository states,
        ICoinsnapClock clock,
        ILogger<CoinsnapLnurlService> logger)
    {
        _http = http;
        _bolt11Parser = bolt11Parser;
        _states = states;
        _clock = clock;
        _logger = logger;
    }

    public async Task<CoinsnapLnurlPayResponse> GetMetadataAsync(
        CoinsnapLightningAddress address,
        CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync(address.MetadataUri, "LNURL metadata URL", cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Coinsnap Lightning Address metadata returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);

        var metadata = Deserialize<CoinsnapLnurlPayResponse>(response.Body, "LNURL metadata");
        ThrowIfProtocolError(metadata.Status, metadata.Reason, "LNURL metadata");
        if (!string.Equals(metadata.Tag, "payRequest", StringComparison.Ordinal))
            throw new FormatException("The Coinsnap Lightning Address did not return tag=payRequest.");
        _ = CoinsnapUrlPolicy.ParseAndValidate(metadata.Callback, "LNURL callback URL");
        ValidateBounds(metadata.MinSendable, metadata.MaxSendable);
        if (string.IsNullOrWhiteSpace(metadata.Metadata))
            throw new FormatException("Coinsnap LNURL metadata is missing its metadata value.");
        return metadata;
    }

    public async Task<LightningInvoice> CreateInvoiceAsync(
        CoinsnapLightningAddress address,
        string connectionIdentity,
        string? storeId,
        Network network,
        LightMoney amount,
        TimeSpan expiry,
        string? expectedDescriptionHash,
        CancellationToken cancellationToken)
    {
        if (amount is null || amount.MilliSatoshi <= 0)
            throw new NotSupportedException("Coinsnap requires a positive fixed invoice amount.");

        var amountMsat = amount.MilliSatoshi;
        ValidateWholeSatoshis(amountMsat);
        var metadata = await GetMetadataAsync(address, cancellationToken);
        ValidateAmountBounds(amountMsat, metadata.MinSendable!.Value, metadata.MaxSendable!.Value);

        var expirySeconds = ToExpirySeconds(expiry);
        var callback = CoinsnapUrlPolicy.ParseAndValidate(metadata.Callback, "LNURL callback URL");
        var callbackUri = AddQuery(callback,
            ("amount", amountMsat.ToString(CultureInfo.InvariantCulture)),
            ("expiry", expirySeconds.ToString(CultureInfo.InvariantCulture)));

        var response = await _http.GetAsync(callbackUri, "LNURL callback URL", cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Coinsnap invoice callback returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);

        var callbackResult = Deserialize<CoinsnapInvoiceCallbackResponse>(response.Body, "invoice callback");
        ThrowIfProtocolError(callbackResult.Status, callbackResult.Reason, "invoice callback");
        if (string.IsNullOrWhiteSpace(callbackResult.PaymentRequest))
            throw new FormatException("Coinsnap invoice callback did not return a BOLT11 payment request.");
        var verifyUri = CoinsnapUrlPolicy.ParseAndValidate(callbackResult.Verify, "LUD-21 verify URL");

        var parsed = _bolt11Parser.Parse(callbackResult.PaymentRequest, network);
        if (parsed.Amount != amount)
            throw new FormatException(
                $"Coinsnap returned {parsed.Amount.MilliSatoshi} msat, but BTCPay requested {amountMsat} msat.");
        if (parsed.ExpiresAt <= _clock.UtcNow)
            throw new FormatException("Coinsnap returned an already-expired BOLT11 payment request.");
        if (!string.IsNullOrWhiteSpace(expectedDescriptionHash) &&
            !string.Equals(parsed.DescriptionHash, expectedDescriptionHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Coinsnap returned a BOLT11 with a mismatched or missing description hash.");
        }

        var state = new CoinsnapInvoiceState
        {
            PaymentHash = parsed.PaymentHash,
            Bolt11 = parsed.Raw,
            VerifyUrl = verifyUri.ToString(),
            LightningAddress = address.Value,
            ConnectionIdentity = connectionIdentity,
            StoreId = storeId,
            CreatedAt = _clock.UtcNow,
            ExpiresAt = parsed.ExpiresAt
        };
        await _states.AddAsync(state, cancellationToken);

        return BuildInvoice(state, parsed, LightningInvoiceStatus.Unpaid);
    }

    public async Task<CoinsnapVerificationResult> VerifyAsync(
        CoinsnapInvoiceState state,
        Network network,
        CancellationToken cancellationToken)
    {
        CoinsnapBolt11 original;
        try
        {
            original = _bolt11Parser.Parse(state.Bolt11, network);
            if (!original.PaymentHash.Equals(state.PaymentHash, StringComparison.OrdinalIgnoreCase) ||
                original.ExpiresAt != state.ExpiresAt)
            {
                return Retry(state, null, "Persisted BOLT11 does not match its Coinsnap invoice state.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not validate persisted Coinsnap BOLT11 {PaymentHash}", state.PaymentHash);
            return Retry(state, null, "Persisted BOLT11 is invalid.");
        }

        if (state.Settled)
        {
            if (IsValidPreimage(state.Preimage, state.PaymentHash))
                return new CoinsnapVerificationResult(
                    CoinsnapVerificationOutcome.Paid,
                    BuildInvoice(state, original, LightningInvoiceStatus.Paid, state.Preimage, state.PaidAt));
            return Retry(state, null, "Persisted settled state has an invalid preimage.");
        }

        CoinsnapHttpResponse response;
        try
        {
            var verifyUri = CoinsnapUrlPolicy.ParseAndValidate(state.VerifyUrl, "LUD-21 verify URL");
            response = await _http.GetAsync(verifyUri, "LUD-21 verify URL", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Coinsnap verification request failed for {PaymentHash}", state.PaymentHash);
            return Retry(state, null, "Verification transport failure.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return Retry(
                state,
                response.StatusCode == HttpStatusCode.TooManyRequests ? response.RetryAfter : null,
                $"Verification returned HTTP {(int)response.StatusCode}.");
        }

        CoinsnapVerifyResponse result;
        try
        {
            result = Deserialize<CoinsnapVerifyResponse>(response.Body, "LUD-21 verification");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Coinsnap returned invalid verification JSON for {PaymentHash}", state.PaymentHash);
            return Retry(state, null, "Verification JSON was invalid.");
        }

        if (string.Equals(result.Status, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(result.Reason?.Trim(), "Not found", StringComparison.OrdinalIgnoreCase))
                return new CoinsnapVerificationResult(CoinsnapVerificationOutcome.Unknown, null);
            return Retry(state, null, result.Reason ?? "Coinsnap verification returned an error.");
        }
        if (!string.Equals(result.Status, "OK", StringComparison.OrdinalIgnoreCase) || result.Settled is null ||
            string.IsNullOrWhiteSpace(result.PaymentRequest))
        {
            return Retry(state, null, "Coinsnap verification response was incomplete.");
        }

        CoinsnapBolt11 returned;
        try
        {
            returned = _bolt11Parser.Parse(result.PaymentRequest, network);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Coinsnap verify returned an invalid BOLT11 for {PaymentHash}", state.PaymentHash);
            return Retry(state, null, "Verification BOLT11 was invalid.");
        }
        if (!returned.PaymentHash.Equals(state.PaymentHash, StringComparison.OrdinalIgnoreCase) ||
            returned.Amount != original.Amount)
        {
            _logger.LogWarning("Coinsnap verify returned a mismatched BOLT11 for {PaymentHash}", state.PaymentHash);
            return Retry(state, null, "Verification BOLT11 did not match the original invoice.");
        }

        if (result.Settled is false)
        {
            var status = original.ExpiresAt <= _clock.UtcNow
                ? LightningInvoiceStatus.Expired
                : LightningInvoiceStatus.Unpaid;
            return new CoinsnapVerificationResult(
                status == LightningInvoiceStatus.Expired
                    ? CoinsnapVerificationOutcome.Expired
                    : CoinsnapVerificationOutcome.Pending,
                BuildInvoice(state, original, status));
        }

        if (!IsValidPreimage(result.Preimage, state.PaymentHash))
        {
            _logger.LogWarning(
                "Coinsnap reported {PaymentHash} settled without a valid preimage; keeping it pending",
                state.PaymentHash);
            return Retry(state, null, "Settlement proof was invalid.");
        }

        var paidAt = _clock.UtcNow;
        var paid = BuildInvoice(state, original, LightningInvoiceStatus.Paid, result.Preimage, paidAt);
        return new CoinsnapVerificationResult(CoinsnapVerificationOutcome.Paid, paid);
    }

    public LightningInvoice BuildFromState(CoinsnapInvoiceState state, Network network)
    {
        var parsed = _bolt11Parser.Parse(state.Bolt11, network);
        if (!parsed.PaymentHash.Equals(state.PaymentHash, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Persisted Coinsnap BOLT11 payment hash mismatch.");
        if (state.Settled && IsValidPreimage(state.Preimage, state.PaymentHash))
            return BuildInvoice(state, parsed, LightningInvoiceStatus.Paid, state.Preimage, state.PaidAt);
        return BuildInvoice(
            state,
            parsed,
            parsed.ExpiresAt <= _clock.UtcNow ? LightningInvoiceStatus.Expired : LightningInvoiceStatus.Unpaid);
    }

    internal static void ValidateWholeSatoshis(long amountMsat)
    {
        if (amountMsat % 1000 != 0)
            throw new FormatException("Coinsnap requires whole satoshi amounts; millisatoshis must be divisible by 1,000.");
    }

    internal static void ValidateBounds(long? minSendable, long? maxSendable)
    {
        if (minSendable is null || maxSendable is null)
            throw new FormatException("Coinsnap LNURL metadata must include minSendable and maxSendable.");
        if (minSendable <= 0 || maxSendable <= 0 || minSendable > maxSendable)
            throw new FormatException("Coinsnap LNURL metadata contains invalid sendable bounds.");
    }

    internal static void ValidateAmountBounds(long amountMsat, long minSendable, long maxSendable)
    {
        if (amountMsat < minSendable)
            throw new FormatException($"Amount {amountMsat} msat is below the Coinsnap minimum of {minSendable} msat.");
        if (amountMsat > maxSendable)
            throw new FormatException($"Amount {amountMsat} msat is above the Coinsnap maximum of {maxSendable} msat.");
    }

    internal static long ToExpirySeconds(TimeSpan expiry)
    {
        if (expiry <= TimeSpan.Zero)
            throw new FormatException("Coinsnap invoice expiry must be positive.");
        var seconds = Math.Ceiling(expiry.TotalSeconds);
        if (seconds > int.MaxValue)
            throw new FormatException("Coinsnap invoice expiry is too large.");
        return (long)seconds;
    }

    internal static bool IsValidPreimage(string? preimage, string paymentHash)
    {
        var normalized = preimage?.Trim();
        if (normalized is not { Length: 64 } || paymentHash.Trim().Length != 64)
            return false;
        try
        {
            var bytes = Convert.FromHexString(normalized);
            var computed = Convert.ToHexString(SHA256.HashData(bytes));
            return computed.Equals(paymentHash.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static Uri AddQuery(Uri uri, params (string Key, string Value)[] values)
    {
        var builder = new UriBuilder(uri);
        var query = new StringBuilder(builder.Query.TrimStart('?'));
        foreach (var (key, value) in values)
        {
            if (query.Length > 0)
                query.Append('&');
            query.Append(Uri.EscapeDataString(key));
            query.Append('=');
            query.Append(Uri.EscapeDataString(value));
        }
        builder.Query = query.ToString();
        return builder.Uri;
    }

    private CoinsnapVerificationResult Retry(CoinsnapInvoiceState state, TimeSpan? retryAfter, string detail) =>
        new(
            CoinsnapVerificationOutcome.Retry,
            new LightningInvoice
            {
                Id = state.PaymentHash,
                PaymentHash = state.PaymentHash,
                BOLT11 = state.Bolt11,
                Status = LightningInvoiceStatus.Unpaid,
                ExpiresAt = state.ExpiresAt
            },
            retryAfter,
            detail);

    private static LightningInvoice BuildInvoice(
        CoinsnapInvoiceState state,
        CoinsnapBolt11 parsed,
        LightningInvoiceStatus status,
        string? preimage = null,
        DateTimeOffset? paidAt = null) => new()
    {
        Id = state.PaymentHash,
        PaymentHash = state.PaymentHash,
        BOLT11 = state.Bolt11,
        Amount = parsed.Amount,
        AmountReceived = status == LightningInvoiceStatus.Paid ? parsed.Amount : null,
        Status = status,
        Preimage = status == LightningInvoiceStatus.Paid ? preimage : null,
        PaidAt = status == LightningInvoiceStatus.Paid ? paidAt : null,
        ExpiresAt = parsed.ExpiresAt
    };

    private static T Deserialize<T>(string body, string operation) where T : class
    {
        try
        {
            return JsonConvert.DeserializeObject<T>(body)
                   ?? throw new FormatException($"Coinsnap {operation} returned an empty JSON value.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Coinsnap {operation} returned invalid JSON.", ex);
        }
    }

    private static void ThrowIfProtocolError(string? status, string? reason, string operation)
    {
        if (string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase))
            throw new FormatException(reason ?? $"Coinsnap {operation} returned an error.");
    }
}
