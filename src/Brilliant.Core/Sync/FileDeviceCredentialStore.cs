using System.Text.Json;

namespace Brilliant.Core.Sync;

/// <summary>What the file says: nothing, "forgotten on purpose", or credentials.</summary>
public abstract record FileState
{
    public sealed record Empty : FileState;

    /// <summary>The learner forgot the server while the secure store couldn't confirm it had let go of its copy, so that copy must not come back.</summary>
    public sealed record Forgotten : FileState;

    public sealed record Held(DeviceCredentials Credentials) : FileState;
}

/// <summary>
/// Keeps the credentials in a file only the current user can read. This is the fallback for builds that can't use the platform's
/// secure storage (an unsigned Mac build has no keychain entitlement); in the app it lives inside the sandbox container.
/// </summary>
public sealed class FileDeviceCredentialStore(string path) : IDeviceCredentialStore
{
    private sealed record Stored(string? Server, string? DeviceId, string? Token, bool Forgotten = false);

    public async Task<FileState> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new FileState.Empty();
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(await File.ReadAllTextAsync(path, ct));
            if (stored is null) return new FileState.Empty();
            if (stored.Forgotten) return new FileState.Forgotten();
            return stored is { Server: { } server, DeviceId: { } id, Token: { } token } && Uri.TryCreate(server, UriKind.Absolute, out var uri)
                ? new FileState.Held(new DeviceCredentials(uri, id, token))
                : new FileState.Empty();
        }
        catch (JsonException) { return new FileState.Empty(); }
    }

    public async Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => (await ReadAsync(ct)) is FileState.Held held ? held.Credentials : null;

    public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) =>
        WriteAsync(new Stored(credentials.Server.ToString(), credentials.DeviceId, credentials.Token), ct);

    /// <summary>Records that there is deliberately no pairing, whatever the secure store may still hold.</summary>
    public Task MarkForgottenAsync(CancellationToken ct = default) => WriteAsync(new Stored(null, null, null, Forgotten: true), ct);

    public Task ClearAsync(CancellationToken ct = default)
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { }   // nothing was ever written
        return Task.CompletedTask;
    }

    private async Task WriteAsync(Stored stored, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Create the file private from the start, so the token is never readable by others, not even briefly.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await JsonSerializer.SerializeAsync(stream, stored, cancellationToken: ct);
    }
}

/// <summary>
/// Uses <paramref name="primary"/> (the platform's secure storage) when it works and the <paramref name="fallback"/> file when it doesn't.
/// The file is authoritative whenever it says anything: it is only written when the secure store failed, and any later save empties it
/// first, so a copy the secure store kept through an outage can never override a newer pairing or a deliberate forget once it recovers.
/// </summary>
public sealed class FallbackDeviceCredentialStore(IDeviceCredentialStore primary, FileDeviceCredentialStore fallback) : IDeviceCredentialStore
{
    public async Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default)
    {
        switch (await fallback.ReadAsync(ct))
        {
            case FileState.Held held: return held.Credentials;
            case FileState.Forgotten: return null;
        }

        try { return await primary.LoadAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    public async Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default)
    {
        await fallback.ClearAsync(ct);
        try
        {
            await primary.SaveAsync(credentials, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await fallback.SaveAsync(credentials, ct);
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        try
        {
            await primary.ClearAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // We can't tell whether the secure store let go, so make "forgotten" durable instead of claiming it: if this write fails too, the failure reaches the screen.
            await fallback.MarkForgottenAsync(ct);
            return;
        }
        await fallback.ClearAsync(ct);
    }
}
