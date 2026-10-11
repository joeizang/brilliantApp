using System.Text.Json;

namespace Brilliant.Core.Sync;

/// <summary>
/// Keeps the credentials in a file only the current user can read. This is the fallback for builds that can't use the platform's
/// secure storage (an unsigned Mac build has no keychain entitlement); in the app it lives inside the sandbox container.
/// </summary>
public sealed class FileDeviceCredentialStore(string path) : IDeviceCredentialStore
{
    private sealed record Stored(string Server, string DeviceId, string Token);

    public async Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(await File.ReadAllTextAsync(path, ct));
            return stored is not null && Uri.TryCreate(stored.Server, UriKind.Absolute, out var server)
                ? new DeviceCredentials(server, stored.DeviceId, stored.Token)
                : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Create the file private from the start, so the token is never readable by others, not even briefly.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await JsonSerializer.SerializeAsync(stream, new Stored(credentials.Server.ToString(), credentials.DeviceId, credentials.Token), cancellationToken: ct);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { }   // nothing was ever saved
        return Task.CompletedTask;
    }
}

/// <summary>Uses <paramref name="primary"/> (the platform's secure storage) when it works and <paramref name="fallback"/> when it doesn't.</summary>
public sealed class FallbackDeviceCredentialStore(IDeviceCredentialStore primary, IDeviceCredentialStore fallback) : IDeviceCredentialStore
{
    public async Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (await primary.LoadAsync(ct) is { } found) return found;
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        return await fallback.LoadAsync(ct);
    }

    public async Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            await primary.SaveAsync(credentials, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await fallback.SaveAsync(credentials, ct);
            return;
        }
        // Stored securely now; don't leave an older copy lying around in the fallback.
        try { await fallback.ClearAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        // Clear both, even if the first fails, so "forget" never leaves a token behind.
        Exception? failure = null;
        try { await primary.ClearAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { failure = e; }
        await fallback.ClearAsync(ct);
        if (failure is not null && await Peek(primary, ct) is not null) throw failure;
    }

    private static async Task<DeviceCredentials?> Peek(IDeviceCredentialStore store, CancellationToken ct)
    {
        try { return await store.LoadAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }
}
