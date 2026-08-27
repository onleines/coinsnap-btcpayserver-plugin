using System.Collections.Concurrent;
using BTCPayServer.Abstractions.Contracts;

namespace BTCPayServer.Plugins.Coinsnap;

internal interface ICoinsnapStateStore
{
    Task<CoinsnapPersistedInvoiceStates?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CoinsnapPersistedInvoiceStates state, CancellationToken cancellationToken = default);
}

internal sealed class BtcpayCoinsnapStateStore : ICoinsnapStateStore
{
    private readonly ISettingsRepository _settings;

    public BtcpayCoinsnapStateStore(ISettingsRepository settings)
    {
        _settings = settings;
    }

    public Task<CoinsnapPersistedInvoiceStates?> LoadAsync(CancellationToken cancellationToken = default) =>
        _settings.GetSettingAsync<CoinsnapPersistedInvoiceStates>(CoinsnapConstants.SettingsName);

    public Task SaveAsync(CoinsnapPersistedInvoiceStates state, CancellationToken cancellationToken = default) =>
        _settings.UpdateSetting(state, CoinsnapConstants.SettingsName);
}

internal interface ICoinsnapInvoiceStateRepository
{
    event Action<CoinsnapInvoiceState>? Settled;
    Task EnsureLoadedAsync(CancellationToken cancellationToken = default);
    Task AddAsync(CoinsnapInvoiceState state, CancellationToken cancellationToken = default);
    Task MarkSettledAsync(string paymentHash, string preimage, DateTimeOffset paidAt, CancellationToken cancellationToken = default);
    Task RemoveAsync(string paymentHash, CancellationToken cancellationToken = default);
    bool TryGet(string paymentHash, string connectionIdentity, out CoinsnapInvoiceState state);
    IReadOnlyCollection<CoinsnapInvoiceState> Snapshot(string? connectionIdentity = null);
}

internal sealed class CoinsnapInvoiceStateRepository : ICoinsnapInvoiceStateRepository
{
    private readonly ICoinsnapStateStore _store;
    private readonly ConcurrentDictionary<string, CoinsnapInvoiceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _loaded;

    public CoinsnapInvoiceStateRepository(ICoinsnapStateStore store)
    {
        _store = store;
    }

    public event Action<CoinsnapInvoiceState>? Settled;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
            return;
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
                return;
            var persisted = await _store.LoadAsync(cancellationToken);
            if (persisted?.Version is 1 && persisted.Invoices is not null)
            {
                foreach (var state in persisted.Invoices.Where(IsStructurallyValid))
                    _states[state.PaymentHash] = state.Clone();
            }
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task AddAsync(CoinsnapInvoiceState state, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!IsStructurallyValid(state))
            throw new ArgumentException("Coinsnap invoice state is incomplete.", nameof(state));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (_states.TryGetValue(state.PaymentHash, out var existing) &&
                !existing.ConnectionIdentity.Equals(state.ConnectionIdentity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A Coinsnap payment hash is already owned by another store connection.");
            }

            _states[state.PaymentHash] = state.Clone();
            await SaveSnapshotAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task MarkSettledAsync(
        string paymentHash,
        string preimage,
        DateTimeOffset paidAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!CoinsnapLnurlService.IsValidPreimage(preimage, paymentHash))
            throw new ArgumentException("The Coinsnap settlement preimage does not match the payment hash.", nameof(preimage));

        CoinsnapInvoiceState? settled = null;
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!_states.TryGetValue(paymentHash, out var current) || current.Settled)
                return;
            settled = current.Clone();
            settled.Settled = true;
            settled.Preimage = preimage;
            settled.PaidAt = paidAt;
            _states[paymentHash] = settled;
            await SaveSnapshotAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
        Settled?.Invoke(settled!.Clone());
    }

    public async Task RemoveAsync(string paymentHash, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (_states.TryRemove(paymentHash, out _))
                await SaveSnapshotAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public bool TryGet(string paymentHash, string connectionIdentity, out CoinsnapInvoiceState state)
    {
        state = default!;
        if (!_loaded || !_states.TryGetValue(paymentHash, out var found) ||
            !found.ConnectionIdentity.Equals(connectionIdentity, StringComparison.Ordinal))
            return false;
        state = found.Clone();
        return true;
    }

    public IReadOnlyCollection<CoinsnapInvoiceState> Snapshot(string? connectionIdentity = null)
    {
        if (!_loaded)
            return [];
        return _states.Values
            .Where(s => connectionIdentity is null || s.ConnectionIdentity.Equals(connectionIdentity, StringComparison.Ordinal))
            .Select(s => s.Clone())
            .ToArray();
    }

    private Task SaveSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = new CoinsnapPersistedInvoiceStates
        {
            Invoices = _states.Values.Select(s => s.Clone()).ToList()
        };
        return _store.SaveAsync(snapshot, cancellationToken);
    }

    private static bool IsStructurallyValid(CoinsnapInvoiceState state) =>
        !string.IsNullOrWhiteSpace(state.PaymentHash) &&
        !string.IsNullOrWhiteSpace(state.Bolt11) &&
        !string.IsNullOrWhiteSpace(state.VerifyUrl) &&
        !string.IsNullOrWhiteSpace(state.LightningAddress) &&
        !string.IsNullOrWhiteSpace(state.ConnectionIdentity) &&
        state.ExpiresAt > state.CreatedAt;
}
