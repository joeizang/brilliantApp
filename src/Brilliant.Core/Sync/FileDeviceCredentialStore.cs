using Brilliant.Core.Apps;
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
    private sealed record Stored(string? Server, string? DeviceId, string? Token, bool Forgotten = false, string AppId = AppStoragePaths.DsaAppId);

    public async Task<FileState> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new FileState.Empty();
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(await File.ReadAllTextAsync(path, ct));
            if (stored is null) return new FileState.Empty();
            if (stored.Forgotten) return new FileState.Forgotten();
            return stored is { Server: { } server, DeviceId: { } id, Token: { } token } && Uri.TryCreate(server, UriKind.Absolute, out var uri)
                ? new FileState.Held(new DeviceCredentials(uri, id, token, stored.AppId))
                : new FileState.Empty();
        }
        catch (JsonException) { return new FileState.Empty(); }
    }

    public async Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => (await ReadAsync(ct)) is FileState.Held held ? held.Credentials : null;

    public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) =>
        WriteAsync(new Stored(credentials.Server.ToString(), credentials.DeviceId, credentials.Token, AppId: credentials.AppId), ct);

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
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Write beside the real file and move it into place, so a write that fails or is cancelled part-way leaves the last
        // authoritative contents untouched. It is created private from the start, so the token is never readable by others.
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            await using (var stream = new FileStream(temp, options))
                await JsonSerializer.SerializeAsync(stream, stored, cancellationToken: ct);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}

/// <summary>
/// Uses <paramref name="primary"/> (the platform's secure storage) when it works and the <paramref name="fallback"/> file when it doesn't.
/// The file is authoritative whenever it says anything, and it is only ever replaced by something that has been committed:
/// a pairing the secure store accepted empties it, one it refused is written into it, and a failed or cancelled save leaves it as it was.
/// Forgetting always leaves a "forgotten" marker, because the platform can report a failed removal as a plain false (or nothing at all)
/// rather than throwing, so a copy the secure store kept can never come back to override a deliberate forget or a newer pairing.
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
        ct.ThrowIfCancellationRequested();

        // Whatever the file says stays until one of the two places has accepted the replacement.
        try
        {
            await primary.SaveAsync(credentials, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await fallback.SaveAsync(credentials, ct);
            return;
        }

        // The secure store holds the new pairing; the file must now stop speaking for an older one.
        await fallback.ClearAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Ask the secure store to let go, but don't trust the answer; the marker is what makes the forget durable.
        // If the marker can't be written the failure reaches the screen rather than a false success.
        try { await primary.ClearAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { }
        await fallback.MarkForgottenAsync(ct);
    }
}
