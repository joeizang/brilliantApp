using Brilliant.Core.Drafts;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Sync;

/// <summary>The outcome of one sync attempt. <see cref="At"/> is when it finished. <see cref="Rejected"/> counts events found this time that the server cannot accept.</summary>
public sealed record SyncResult(int Pushed, int Pulled, SyncFailure? Failure, string? Detail, DateTimeOffset At, int Rejected = 0, int DraftsPushed = 0, int DraftsPulled = 0)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Keeps this device's progress log and the server's in step. Pushes this device's own events the server hasn't seen, then pulls
/// everything after its cursor; both directions are idempotent by event ID, so an interrupted or repeated sync is harmless.
/// Code drafts (when a draft store is given) go the same way, but are mutable: the newer copy of a step's draft wins on both sides.
/// Nothing here throws at the learner: when the server can't be reached the app carries on from its local log.
/// </summary>
public sealed class SyncService(
    IProgressEventLog log, string deviceId, IDeviceCredentialStore credentials, SyncClient client, ISyncStateStore stateStore, TimeProvider? clock = null, IDraftSyncSource? drafts = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SyncResult? _last;

    /// <summary>The most recent attempt this run, successful or not; null before the first.</summary>
    public SyncResult? Last => _last;

    public bool IsSyncing { get; private set; }

    /// <summary>When a sync last completed for the pairing <paramref name="pairingId"/>, across runs of the app; null if it never has.</summary>
    public DateTimeOffset? LastSyncedAt(string pairingId) => stateStore.Load().For(pairingId).LastSyncedAt;

    /// <summary>How many of this device's events the server cannot accept, for the pairing <paramref name="pairingId"/>. They stay on this device.</summary>
    public int RejectedCount(string pairingId) => stateStore.Load().For(pairingId).RejectedIds.Count;

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
        int pushed = 0, pulled = 0, draftsPushed = 0, draftsPulled = 0;
        try
        {
            var creds = await credentials.LoadAsync(ct) ?? throw new SyncException(SyncFailure.NotPaired);
            var state = stateStore.Load().For(creds.DeviceId);

            // Push: this device's own events after the cursor. Events pulled from elsewhere are not ours to send back.
            // An event the server could never accept is not sent, and one it refuses by name is set aside; either way it stays in the
            // local log and the rest of the backlog goes through, so one oversized event can't hold up every other.
            var local = log.ReadSince(state.PushedThrough);
            var own = local.Events.Where(e => e.DeviceId == deviceId).ToList();
            var refused = new List<string>();
            var sendable = new List<ProgressEvent>();
            foreach (var e in own)
            {
                if (EventLimits.Problem(e) is null) sendable.Add(e);
                else refused.Add(e.Id);
            }
            foreach (var batch in Batches(sendable))
            {
                var response = await client.PushAsync(creds, batch, ct);
                pushed += response.Accepted;
                refused.AddRange((response.Rejected ?? []).Select(r => r.Id));
            }
            var rejected = state.RejectedIds.Union(refused).ToList();
            state = state with { PushedThrough = local.Cursor, Rejected = rejected.Count == 0 ? null : rejected };
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

            // Drafts: what changed here goes up, then what changed elsewhere comes down. A draft is only replaced by a newer one.
            if (drafts is not null)
            {
                foreach (var batch in DraftBatches(drafts.ReadUnsynced().Where(d => DraftLimits.Problem(d) is null)))
                {
                    var response = await client.PushDraftsAsync(creds, batch, ct);
                    draftsPushed += response.Accepted;
                    drafts.MarkSynced(batch);
                }
                while (true)
                {
                    var page = await client.PullDraftsAsync(creds, state.DraftsPulledThrough, ct);
                    draftsPulled += drafts.Merge(page.Drafts);
                    if (page.Cursor <= state.DraftsPulledThrough) break;
                    state = state with { DraftsPulledThrough = page.Cursor };
                    stateStore.Save(state);
                    if (!page.HasMore) break;
                }
            }

            var now = _clock.GetUtcNow();
            stateStore.Save(state with { LastSyncedAt = now });
            if (pulled > 0) EventsPulled?.Invoke();
            return new SyncResult(pushed, pulled, null, null, now, refused.Count, draftsPushed, draftsPulled);
        }
        catch (SyncException e)
        {
            if (pulled > 0) EventsPulled?.Invoke();
            return new SyncResult(pushed, pulled, e.Failure, e.Detail, _clock.GetUtcNow(), DraftsPushed: draftsPushed, DraftsPulled: draftsPulled);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (pulled > 0) EventsPulled?.Invoke();
            return new SyncResult(pushed, pulled, SyncFailure.LocalError, e.GetBaseException().Message, _clock.GetUtcNow(), DraftsPushed: draftsPushed, DraftsPulled: draftsPulled);
        }
    }

    /// <summary>Batches of at most <see cref="DraftLimits.MaxBatch"/> drafts and <see cref="DraftLimits.MaxBatchData"/> characters of code, in order.</summary>
    private static IEnumerable<List<CodeDraft>> DraftBatches(IEnumerable<CodeDraft> drafts)
    {
        var batch = new List<CodeDraft>();
        var size = 0;
        foreach (var d in drafts)
        {
            var length = d.Code?.Length ?? 0;
            if (batch.Count > 0 && (batch.Count == DraftLimits.MaxBatch || size + length > DraftLimits.MaxBatchData))
            {
                yield return batch;
                (batch, size) = ([], 0);
            }
            batch.Add(d);
            size += length;
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>Batches of at most <see cref="EventLimits.MaxBatch"/> events and <see cref="EventLimits.MaxBatchData"/> characters of data, in order. An event too big to share a batch goes alone.</summary>
    private static IEnumerable<List<ProgressEvent>> Batches(List<ProgressEvent> events)
    {
        var batch = new List<ProgressEvent>();
        var size = 0;
        foreach (var e in events)
        {
            var length = e.Data?.Length ?? 0;
            if (batch.Count > 0 && (batch.Count == EventLimits.MaxBatch || size + length > EventLimits.MaxBatchData))
            {
                yield return batch;
                (batch, size) = ([], 0);
            }
            batch.Add(e);
            size += length;
        }
        if (batch.Count > 0) yield return batch;
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
