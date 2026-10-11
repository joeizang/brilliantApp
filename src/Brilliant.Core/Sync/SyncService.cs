using Brilliant.Core.Progress;

namespace Brilliant.Core.Sync;

/// <summary>The outcome of one sync attempt. <see cref="At"/> is when it finished.</summary>
public sealed record SyncResult(int Pushed, int Pulled, SyncFailure? Failure, string? Detail, DateTimeOffset At)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Keeps this device's progress log and the server's in step. Pushes this device's own events the server hasn't seen, then pulls
/// everything after its cursor; both directions are idempotent by event ID, so an interrupted or repeated sync is harmless.
/// Nothing here throws at the learner: when the server can't be reached the app carries on from its local log.
/// </summary>
public sealed class SyncService(
    IProgressEventLog log, string deviceId, IDeviceCredentialStore credentials, SyncClient client, ISyncStateStore stateStore, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SyncResult? _last;

    /// <summary>The most recent attempt this run, successful or not; null before the first.</summary>
    public SyncResult? Last => _last;

    public bool IsSyncing { get; private set; }

    /// <summary>When a sync last completed for the pairing <paramref name="pairingId"/>, across runs of the app; null if it never has.</summary>
    public DateTimeOffset? LastSyncedAt(string pairingId) => stateStore.Load().For(pairingId).LastSyncedAt;

    /// <summary>Raised when a sync starts or finishes, so a screen showing the status can redraw.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised after a sync that brought in events from other devices, so screens showing progress can redraw.</summary>
    public event Action? EventsPulled;

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        // One sync at a time: a tap on "Sync now" during the periodic one waits for it, then checks again.
        await _gate.WaitAsync(ct);
        try
        {
            IsSyncing = true;
            StatusChanged?.Invoke();
            var result = await RunAsync(ct);
            _last = result;
            return result;
        }
        finally
        {
            IsSyncing = false;
            _gate.Release();
            StatusChanged?.Invoke();
        }
    }

    private async Task<SyncResult> RunAsync(CancellationToken ct)
    {
        int pushed = 0, pulled = 0;
        try
        {
            var creds = await credentials.LoadAsync(ct) ?? throw new SyncException(SyncFailure.NotPaired);
            var state = stateStore.Load().For(creds.DeviceId);

            // Push: this device's own events after the cursor. Events pulled from elsewhere are not ours to send back.
            var local = log.ReadSince(state.PushedThrough);
            var own = local.Events.Where(e => e.DeviceId == deviceId).ToList();
            for (var i = 0; i < own.Count; i += EventLimits.MaxBatch)
                pushed += (await client.PushAsync(creds, own.Skip(i).Take(EventLimits.MaxBatch).ToList(), ct)).Accepted;
            state = state with { PushedThrough = local.Cursor };
            stateStore.Save(state);

            // Pull: page through the server's log from the cursor, saving the cursor as each page lands.
            while (true)
            {
                var page = await client.PullAsync(creds, state.PulledThrough, ct);
                pulled += page.Events.Count(log.Append);
                if (page.Cursor <= state.PulledThrough) break;   // a server that makes no progress must not trap us here
                state = state with { PulledThrough = page.Cursor };
                stateStore.Save(state);
                if (!page.HasMore) break;
            }

            var now = _clock.GetUtcNow();
            stateStore.Save(state with { LastSyncedAt = now });
            if (pulled > 0) EventsPulled?.Invoke();
            return new SyncResult(pushed, pulled, null, null, now);
        }
        catch (SyncException e)
        {
            if (pulled > 0) EventsPulled?.Invoke();
            return new SyncResult(pushed, pulled, e.Failure, e.Detail, _clock.GetUtcNow());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (pulled > 0) EventsPulled?.Invoke();
            return new SyncResult(pushed, pulled, SyncFailure.LocalError, e.GetBaseException().Message, _clock.GetUtcNow());
        }
    }
}

/// <summary>Runs <see cref="SyncService"/> when the app starts and then at an interval, quietly, for as long as the app does.</summary>
public sealed class SyncScheduler(SyncService sync, TimeSpan interval) : IDisposable
{
    private CancellationTokenSource? _stop;

    public void Start()
    {
        if (_stop is not null) return;
        _stop = new CancellationTokenSource();
        _ = RunAsync(_stop.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                try { await sync.SyncAsync(ct); }
                catch (Exception e) when (e is not OperationCanceledException) { /* a sync that goes wrong must never take the app down with it */ }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => _stop?.Cancel();
}
