using System.Collections.Concurrent;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap;

internal sealed class CoinsnapSettlementPoller : IHostedService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NotFoundDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettledGrace = TimeSpan.FromHours(1);
    private static readonly TimeSpan ExpiredGrace = TimeSpan.FromHours(1);
    private const int MaxConcurrency = 8;
    private const int NotFoundRemovalThreshold = 3;

    private readonly ICoinsnapInvoiceStateRepository _states;
    private readonly CoinsnapLnurlService _lnurl;
    private readonly ICoinsnapClock _clock;
    private readonly ILogger<CoinsnapSettlementPoller> _logger;
    private readonly ConcurrentDictionary<string, PollState> _pollStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    private sealed class PollState
    {
        public int Errors;
        public int NotFounds;
        public DateTimeOffset NextAttempt;
        public bool ConfirmedExpired;
    }

    public CoinsnapSettlementPoller(
        ICoinsnapInvoiceStateRepository states,
        IHttpClientFactory httpClientFactory,
        ICoinsnapBolt11Parser bolt11Parser,
        ICoinsnapClock clock,
        ILoggerFactory loggerFactory)
    {
        _states = states;
        _clock = clock;
        _logger = loggerFactory.CreateLogger<CoinsnapSettlementPoller>();
        _lnurl = new CoinsnapLnurlService(
            new CoinsnapHttpClient(httpClientFactory.CreateClient(CoinsnapConstants.HttpClientName)),
            bolt11Parser,
            states,
            clock,
            loggerFactory.CreateLogger<CoinsnapLnurlService>());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _states.EnsureLoadedAsync(cancellationToken);
        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        if (_loop is null)
            return;
        try
        {
            await _loop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = _states.Snapshot();
                var live = snapshot.Select(s => s.PaymentHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var hash in _pollStates.Keys)
                {
                    if (!live.Contains(hash))
                        _pollStates.TryRemove(hash, out _);
                }

                var now = _clock.UtcNow;
                var due = snapshot.Where(state => IsDue(state, now));
                await Parallel.ForEachAsync(
                    due,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxConcurrency,
                        CancellationToken = cancellationToken
                    },
                    async (state, token) => await PollOneAsync(state, token));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Coinsnap settlement polling cycle failed");
            }

            try
            {
                await Task.Delay(ScanInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOneAsync(CoinsnapInvoiceState invoice, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (invoice.Settled)
        {
            if (now > invoice.ExpiresAt + SettledGrace)
                await _states.RemoveAsync(invoice.PaymentHash, cancellationToken);
            return;
        }
        var pollState = _pollStates.GetOrAdd(invoice.PaymentHash, _ => new PollState());
        if (pollState.ConfirmedExpired)
        {
            if (now > invoice.ExpiresAt + ExpiredGrace)
            {
                _pollStates.TryRemove(invoice.PaymentHash, out _);
                await _states.RemoveAsync(invoice.PaymentHash, cancellationToken);
            }
            return;
        }
        if (now < pollState.NextAttempt)
            return;

        CoinsnapVerificationResult result;
        try
        {
            result = await _lnurl.VerifyAsync(invoice, Network.Main, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unexpected Coinsnap verification failure for {PaymentHash}", invoice.PaymentHash);
            ScheduleFailure(pollState, null);
            return;
        }

        switch (result.Outcome)
        {
            case CoinsnapVerificationOutcome.Paid when result.Invoice?.Preimage is { } preimage:
                _pollStates.TryRemove(invoice.PaymentHash, out _);
                await _states.MarkSettledAsync(
                    invoice.PaymentHash,
                    preimage,
                    result.Invoice.PaidAt ?? now,
                    cancellationToken);
                break;

            case CoinsnapVerificationOutcome.Expired:
                pollState.Errors = 0;
                pollState.NotFounds = 0;
                pollState.ConfirmedExpired = true;
                pollState.NextAttempt = DateTimeOffset.MaxValue;
                break;

            case CoinsnapVerificationOutcome.Unknown:
                pollState.Errors = 0;
                pollState.NotFounds++;
                if (pollState.NotFounds >= NotFoundRemovalThreshold)
                {
                    _pollStates.TryRemove(invoice.PaymentHash, out _);
                    await _states.RemoveAsync(invoice.PaymentHash, cancellationToken);
                }
                else
                {
                    pollState.NextAttempt = now + AddJitter(NotFoundDelay);
                }
                break;

            case CoinsnapVerificationOutcome.Retry:
                pollState.NotFounds = 0;
                ScheduleFailure(pollState, result.RetryAfter);
                break;

            case CoinsnapVerificationOutcome.Pending:
                pollState.Errors = 0;
                pollState.NotFounds = 0;
                pollState.NextAttempt = now + AddJitter(IntervalForAge(now - invoice.CreatedAt));
                break;
        }
    }

    private void ScheduleFailure(PollState state, TimeSpan? requestedDelay)
    {
        state.Errors++;
        var exponentialMs = ScanInterval.TotalMilliseconds * Math.Pow(2, Math.Min(state.Errors, 16));
        var delay = TimeSpan.FromMilliseconds(Math.Min(exponentialMs, MaxBackoff.TotalMilliseconds));
        if (requestedDelay is { } serverDelay && serverDelay > delay)
            delay = serverDelay > MaxBackoff ? MaxBackoff : serverDelay;
        var scheduled = AddJitter(delay);
        if (scheduled > MaxBackoff)
            scheduled = MaxBackoff;
        if (requestedDelay is { } minimumRequested)
        {
            var minimum = minimumRequested > MaxBackoff ? MaxBackoff : minimumRequested;
            if (scheduled < minimum)
                scheduled = minimum;
        }
        state.NextAttempt = _clock.UtcNow + scheduled;
    }

    private bool IsDue(CoinsnapInvoiceState invoice, DateTimeOffset now)
    {
        if (invoice.Settled)
            return now > invoice.ExpiresAt + SettledGrace;
        if (!_pollStates.TryGetValue(invoice.PaymentHash, out var state))
            return true;
        if (state.ConfirmedExpired)
            return now > invoice.ExpiresAt + ExpiredGrace;
        return now >= state.NextAttempt;
    }

    internal static TimeSpan IntervalForAge(TimeSpan age) =>
        age < TimeSpan.FromMinutes(2) ? TimeSpan.FromSeconds(3) :
        age < TimeSpan.FromMinutes(10) ? TimeSpan.FromSeconds(10) :
        TimeSpan.FromSeconds(30);

    internal static TimeSpan AddJitter(TimeSpan value)
    {
        var multiplier = 0.8 + Random.Shared.NextDouble() * 0.4;
        return TimeSpan.FromMilliseconds(Math.Max(1, value.TotalMilliseconds * multiplier));
    }
}
