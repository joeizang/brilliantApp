using System.Text.Json;

namespace Brilliant.Core.Sync;

/// <summary>
/// What this device remembers between syncs, tied to the pairing it was recorded against: how far through its own log it has pushed,
/// how far through the server's log it has pulled, and when that last completed. Cursors are only valid for one server.
/// </summary>
/// <param name="PairingId">The server's ID for this device's pairing. A different pairing starts over (safe: both directions are idempotent).</param>
/// <param name="PushedThrough">The local log's sequence number up to which events have been sent.</param>
/// <param name="PulledThrough">The server's cursor up to which events have been received.</param>
/// <param name="DraftsPulledThrough">The server's draft cursor up to which drafts have been received.</param>
/// <param name="Rejected">IDs of this device's events the server cannot accept (over the size limits). They stay in the local log, are not sent again, and are shown to the learner.</param>
public sealed record SyncState(string? PairingId, long PushedThrough, long PulledThrough, DateTimeOffset? LastSyncedAt, IReadOnlyList<string>? Rejected = null, long DraftsPulledThrough = 0)
{
    public static SyncState None { get; } = new(null, 0, 0, null);

    public IReadOnlyList<string> RejectedIds => Rejected ?? [];

    /// <summary>The state to continue from for <paramref name="pairingId"/>; a state recorded for another pairing is discarded.</summary>
    public SyncState For(string pairingId) => PairingId == pairingId ? this : new SyncState(pairingId, 0, 0, null);
}

public interface ISyncStateStore
{
    SyncState Load();
    void Save(SyncState state);
}

/// <summary>Keeps the <see cref="SyncState"/> in a small JSON file. A missing or unreadable file means "never synced", which only costs a repeat of idempotent work.</summary>
public sealed class FileSyncStateStore(string path) : ISyncStateStore
{
    public SyncState Load()
    {
        try { return JsonSerializer.Deserialize<SyncState>(File.ReadAllText(path)) ?? SyncState.None; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return SyncState.None; }
    }

    public void Save(SyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, path, overwrite: true);
    }
}
