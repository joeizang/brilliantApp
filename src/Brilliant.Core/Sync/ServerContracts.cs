namespace Brilliant.Core.Sync;

// The JSON bodies shared by the server and the apps, so both sides compile against one definition.

/// <summary>Exchanges a one-time pairing code for a device token.</summary>
public sealed record PairRequest(string Code, string DeviceName);

/// <summary>The token is shown once, here, and never again: the server keeps only a hash of it.</summary>
public sealed record PairResponse(string DeviceId, string Token);

public sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt);

public sealed record DeviceInfo(string Id, string Name, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt, bool Revoked);
