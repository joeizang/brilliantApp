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
