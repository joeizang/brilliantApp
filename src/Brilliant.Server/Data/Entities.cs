namespace Brilliant.Server.Data;

public sealed class Device
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the device token. The token itself is never stored.</summary>
    public string TokenHash { get; set; } = "";

    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class PairingCode
{
    /// <summary>SHA-256 of the normalised code, so a database dump doesn't hand out working codes.</summary>
    public string Hash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>
/// One progress event as the household's single learner recorded it on any device. <see cref="Seq"/> is the order the server
/// accepted them in, and is what devices pull by. <see cref="Id"/> is the event's own identity, which makes a push idempotent.
/// </summary>
public sealed class EventRecord
{
    public long Seq { get; set; }
    public string Id { get; set; } = "";

    /// <summary>The device that recorded the event (the app's own device ID, not a pairing record).</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>UTC ticks, so a round trip through the server returns exactly what was recorded; ordering between events depends on it.</summary>
    public long OccurredAtTicks { get; set; }

    public string Type { get; set; } = "";
    public string LessonId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string? Data { get; set; }

    /// <summary>The paired device whose token pushed it.</summary>
    public Guid PushedBy { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>
/// The household's current code for one step, whichever device wrote it. Unlike events a draft is replaced in place: the newest
/// <see cref="UpdatedAtTicks"/> wins, and <see cref="Seq"/> moves to the head of the line each time it is replaced so devices pulling
/// by cursor see the change. A null <see cref="Code"/> is a reset, kept so other devices learn of it.
/// </summary>
public sealed class DraftRecord
{
    public string StepId { get; set; } = "";
    public long Seq { get; set; }
    public string? Code { get; set; }

    /// <summary>UTC ticks, as the device recorded them.</summary>
    public long UpdatedAtTicks { get; set; }

    public Guid PushedBy { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>
/// One published version of a Content Pack, kept whole. Versions are never changed or removed: a mistake is fixed by publishing a
/// higher version. The numbers are the parsed version, so "newest" is a plain sort.
/// </summary>
public sealed class PackRecord
{
    public string PackId { get; set; } = "";
    public int Major { get; set; }
    public int Minor { get; set; }
    public int Patch { get; set; }
    public int FormatVersion { get; set; }

    /// <summary>Lower-case hex SHA-256 of <see cref="Content"/>.</summary>
    public string Sha256 { get; set; } = "";

    public byte[] Content { get; set; } = [];

    /// <summary>Length of <see cref="Content"/> in bytes, kept so a listing needn't read the pack.</summary>
    public long Size { get; set; }

    public Guid PublishedBy { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
}
