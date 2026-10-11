using Brilliant.Core.Progress;

namespace Brilliant.Core.Sync;

// The JSON bodies shared by the server and the apps, so both sides compile against one definition.

/// <summary>Exchanges a one-time pairing code for a device token.</summary>
public sealed record PairRequest(string Code, string DeviceName);

/// <summary>The token is shown once, here, and never again: the server keeps only a hash of it.</summary>
public sealed record PairResponse(string DeviceId, string Token);

public sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt);

public sealed record DeviceInfo(string Id, string Name, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt, bool Revoked);

/// <summary>Events the device wants the server to keep. Safe to send again: the server ignores IDs it already has.</summary>
public sealed record PushEventsRequest(IReadOnlyList<ProgressEvent> Events);

/// <summary><paramref name="Accepted"/> were new; <paramref name="Duplicates"/> the server already had.</summary>
public sealed record PushEventsResponse(int Accepted, int Duplicates);

/// <summary>
/// A page of the household's merged log, in the order the server received it. <paramref name="Cursor"/> is what to ask for next
/// (<c>GET /events?after=&lt;cursor&gt;</c>); <paramref name="HasMore"/> says another page is waiting.
/// </summary>
public sealed record PullEventsResponse(IReadOnlyList<ProgressEvent> Events, long Cursor, bool HasMore);

/// <summary>The limits the server enforces on pushed events, shared so the client can batch to fit.</summary>
public static class EventLimits
{
    public const int MaxBatch = 500;
    public const int MaxPullPage = 1000;
    public const int MaxIdLength = 100;
    public const int MaxDataLength = 100_000;
}
