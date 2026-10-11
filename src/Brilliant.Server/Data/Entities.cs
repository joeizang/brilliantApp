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
