using Brilliant.Core.Content;

namespace Brilliant.Core.Sync;

public enum ContentUpdateOutcome
{
    /// <summary>The server has nothing newer than this device already has.</summary>
    UpToDate,

    /// <summary>A newer pack was downloaded and stored; it is used the next time the app starts.</summary>
    Downloaded,

    /// <summary>The device isn't paired, so there is no server to ask.</summary>
    NotPaired,

    /// <summary>The server couldn't be reached, or answered badly, or sent a pack that was unusable.</summary>
    Failed,
}

/// <param name="Version">The version downloaded, or the newest one waiting for a restart.</param>
public sealed record ContentUpdateResult(ContentUpdateOutcome Outcome, DateTimeOffset At, string? Version = null, SyncFailure? Failure = null, string? Detail = null);

/// <summary>
/// Looks on the household server for a newer version of the pack the app is running, downloads it and keeps it in the
/// <see cref="PackCache"/>. The running content is never swapped under the learner: the new version is used at the next start,
/// when <see cref="PackStartup.Choose"/> finds it. Progress is recorded against stable IDs, so it carries over (docs/content-distribution.md).
/// Nothing here throws at the learner; the server being away just means the pack stays as it is.
/// </summary>
public sealed class ContentUpdateService(ContentGraph running, PackCache cache, PackClient client, IDeviceCredentialStore credentials, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _unusable = [];   // checksums of packs this device downloaded and could not use: not fetched again every few minutes
    private readonly PackVersion _runningVersion = PackVersion.TryParse(running.Manifest.Version, out var v) ? v : default;

    public ContentUpdateResult? Last { get; private set; }

    /// <summary>The newest version downloaded that the running app isn't using yet, or null.</summary>
    public string? PendingVersion { get; private set; }

    public string RunningVersion => _runningVersion.ToString();

    /// <summary>Raised when a check finishes, so a screen showing the status can redraw.</summary>
    public event Action? Changed;

    public async Task<ContentUpdateResult> CheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var result = await RunAsync(ct);
            Last = result;
            return result;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    private async Task<ContentUpdateResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var creds = await credentials.LoadAsync(ct);
            if (creds is null) return new ContentUpdateResult(ContentUpdateOutcome.NotPaired, _clock.GetUtcNow());

            var held = cache.Newest(running.Manifest.PackId);
            var have = held is { } h && h > _runningVersion ? h : _runningVersion;

            // The newest version of our pack that this app can read. A pack in a newer format waits for a newer app.
            var offer = (await client.ListAsync(creds, ct))
                .Where(p => p.PackId == running.Manifest.PackId && p.FormatVersion == ContentPackFormat.CurrentFormatVersion && PackVersion.TryParse(p.Version, out _))
                .OrderByDescending(p => PackVersion.Parse(p.Version))
                .FirstOrDefault();
            if (offer is null || PackVersion.Parse(offer.Version) <= have || _unusable.Contains(offer.Sha256))
                return new ContentUpdateResult(ContentUpdateOutcome.UpToDate, _clock.GetUtcNow(), PendingVersion);

            var bytes = await client.DownloadAsync(creds, offer, ct);
            var version = PackVersion.Parse(offer.Version);
            try { cache.Store(offer.PackId, version, bytes); }
            catch (ContentPackException) { _unusable.Add(offer.Sha256); throw; }
            PendingVersion = version.ToString();
            return new ContentUpdateResult(ContentUpdateOutcome.Downloaded, _clock.GetUtcNow(), PendingVersion);
        }
        catch (SyncException e)
        {
            return new ContentUpdateResult(ContentUpdateOutcome.Failed, _clock.GetUtcNow(), PendingVersion, e.Failure, e.Detail);
        }
        catch (ContentPackException e)
        {
            // The server's pack passed its own checks but this device can't use it (or it was damaged in transit). Try again next time.
            return new ContentUpdateResult(ContentUpdateOutcome.Failed, _clock.GetUtcNow(), PendingVersion, SyncFailure.ServerError, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ContentUpdateResult(ContentUpdateOutcome.Failed, _clock.GetUtcNow(), PendingVersion, SyncFailure.LocalError, e.GetBaseException().Message);
        }
    }
}
