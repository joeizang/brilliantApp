using Brilliant.Core.Progress;
using Brilliant.Core.Apps;

namespace Brilliant.Core.Sync;

// The JSON bodies shared by the server and the apps, so both sides compile against one definition.

/// <summary>Exchanges a one-time pairing code for a device token.</summary>
public sealed record PairRequest(string Code, string DeviceName, string AppId = AppStoragePaths.DsaAppId);

/// <summary>The token is shown once, here, and never again: the server keeps only a hash of it.</summary>
public sealed record PairResponse(string DeviceId, string Token, string AppId = AppStoragePaths.DsaAppId);

public sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt);

public sealed record DeviceInfo(string Id, string Name, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt, bool Revoked, string AppId = AppStoragePaths.DsaAppId);

/// <summary>Events the device wants the server to keep. Safe to send again: the server ignores IDs it already has.</summary>
public sealed record PushEventsRequest(IReadOnlyList<ProgressEvent> Events);

/// <summary>An event the server refused, and why. The rest of the batch is unaffected.</summary>
public sealed record RejectedEvent(string Id, string Reason);

/// <summary>
/// <paramref name="Accepted"/> were new; <paramref name="Duplicates"/> the server already had; <paramref name="Rejected"/> broke
/// <see cref="EventLimits"/> and were not stored (the valid events in the same batch were).
/// </summary>
public sealed record PushEventsResponse(int Accepted, int Duplicates, IReadOnlyList<RejectedEvent>? Rejected = null);

/// <summary>
/// A page of the household's merged log, in the order the server received it. <paramref name="Cursor"/> is what to ask for next
/// (<c>GET /events?after=&lt;cursor&gt;</c>); <paramref name="HasMore"/> says another page is waiting.
/// </summary>
public sealed record PullEventsResponse(IReadOnlyList<ProgressEvent> Events, long Cursor, bool HasMore);

/// <summary>Drafts the device wants the server to hold. Each is kept only if it is newer than the server's copy (<see cref="Drafts.CodeDraft.Supersedes"/>), so sending again is harmless.</summary>
public sealed record PushDraftsRequest(IReadOnlyList<Drafts.CodeDraft> Drafts);

/// <summary><paramref name="Accepted"/> replaced (or created) the server's copy; <paramref name="Ignored"/> were not newer than it; <paramref name="Rejected"/> broke <see cref="DraftLimits"/>.</summary>
public sealed record PushDraftsResponse(int Accepted, int Ignored, IReadOnlyList<RejectedEvent>? Rejected = null);

/// <summary>Drafts changed after the cursor, oldest change first. Tombstones (null code) are included: they are how a reset reaches other devices.</summary>
public sealed record PullDraftsResponse(IReadOnlyList<Drafts.CodeDraft> Drafts, long Cursor, bool HasMore);

/// <summary>One published Content Pack. <paramref name="Sha256"/> (lower-case hex) lets a device check what it downloaded.</summary>
public sealed record PackInfo(string PackId, string Version, int FormatVersion, string Sha256, long Size, DateTimeOffset PublishedAt, string AppId = AppStoragePaths.DsaAppId);

/// <summary>The newest published version of each pack.</summary>
public sealed record PackListResponse(IReadOnlyList<PackInfo> Packs);

/// <summary>
/// <paramref name="Created"/> is false when this exact pack was already published (publishing is safe to repeat).
/// <paramref name="RemovedIds"/> are IDs the previous version had and this one lacks: progress recorded on them is kept but no longer shown.
/// </summary>
public sealed record PublishPackResponse(PackInfo Pack, bool Created, IReadOnlyList<string> RemovedIds);

public static class PackLimits
{
    /// <summary>The biggest pack the server takes, in bytes. The bundled pack is well under 1 MB.</summary>
    public const int MaxBytes = 20 * 1024 * 1024;
}

/// <summary>Limits on drafts, shared like <see cref="EventLimits"/>.</summary>
public static class DraftLimits
{
    public const int MaxBatch = 200;
    public const int MaxPullPage = 500;

    /// <summary>Per push, counting code only.</summary>
    public const int MaxBatchData = EventLimits.MaxBatchData;

    public static string? Problem(Drafts.CodeDraft d)
    {
        if (string.IsNullOrWhiteSpace(d.StepId) || d.StepId.Length > EventLimits.MaxIdLength) return $"The step ID must be 1 to {EventLimits.MaxIdLength} characters.";
        // PostgreSQL text cannot hold U+0000; one such draft would otherwise fail the whole push and be retried for ever.
        if (d.StepId.Contains('\0') || (d.Code?.Contains('\0') ?? false)) return "The draft contains a NUL character, which the server cannot store.";
        if ((d.Code?.Length ?? 0) > EventLimits.MaxDataLength) return $"The draft is {d.Code!.Length:N0} characters; the limit is {EventLimits.MaxDataLength:N0}.";
        if (d.UpdatedAt <= DateTimeOffset.MinValue.AddYears(1) || d.UpdatedAt >= DateTimeOffset.MaxValue.AddYears(-1)) return "The draft's time is out of range.";
        return null;
    }
}

/// <summary>The limits the server enforces on pushed events, shared so the client can batch to fit and can tell which events could never be accepted.</summary>
public static class EventLimits
{
    public const int MaxBatch = 500;
    public const int MaxPullPage = 1000;
    public const int MaxIdLength = 100;

    /// <summary>Per event. Generous on purpose: a submitted solution is recorded in full, and a long file with every character escaped is still well under this.</summary>
    public const int MaxDataLength = 1_000_000;

    /// <summary>Per push, counting <see cref="ProgressEvent.Data"/> only, so a batch of large events stays well within the 30 MB request body Kestrel will read (2 million characters, all escaped, is about 12 MB).</summary>
    public const int MaxBatchData = 2_000_000;

    /// <summary>Why the server would refuse <paramref name="e"/>, or null if it is acceptable. The client asks the same question before sending.</summary>
    public static string? Problem(ProgressEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.Id) || e.Id.Length > MaxIdLength) return $"The event ID must be 1 to {MaxIdLength} characters.";
        if (string.IsNullOrWhiteSpace(e.DeviceId) || e.DeviceId.Length > MaxIdLength) return $"The device ID must be 1 to {MaxIdLength} characters.";
        if (string.IsNullOrWhiteSpace(e.Type) || e.Type.Length > MaxIdLength) return $"The event type must be 1 to {MaxIdLength} characters.";
        if ((e.LessonId ?? "").Length > MaxIdLength || (e.StepId ?? "").Length > MaxIdLength) return $"Lesson and step IDs are limited to {MaxIdLength} characters.";
        if ((e.Data?.Length ?? 0) > MaxDataLength) return $"The event's data is {e.Data!.Length:N0} characters; the limit is {MaxDataLength:N0}.";
        if (e.OccurredAt <= DateTimeOffset.MinValue.AddYears(1) || e.OccurredAt >= DateTimeOffset.MaxValue.AddYears(-1)) return "The event's time is out of range.";
        return null;
    }
}
