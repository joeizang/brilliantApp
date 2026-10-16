using Brilliant.Core.Apps;
using System.Text.Json;
using Brilliant.Core.Sync;

namespace Brilliant.App;

/// <summary>Keeps the server address and device token in the platform's secure storage (the keychain on a Mac, the keystore on Android) as one entry.</summary>
public sealed class SecureDeviceCredentialStore(string key) : IDeviceCredentialStore
{

    private sealed record Stored(string Server, string DeviceId, string Token, string AppId = AppStoragePaths.DsaAppId);

    public async Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default)
    {
        string? json;
        try { json = await SecureStorage.Default.GetAsync(key); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The key store can be unavailable or hold an entry from a different signing identity; treat that as "not paired".
            return null;
        }
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            return stored is not null && Uri.TryCreate(stored.Server, UriKind.Absolute, out var server)
                ? new DeviceCredentials(server, stored.DeviceId, stored.Token, stored.AppId)
                : null;
        }
        catch (JsonException) { return null; }
    }

    public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) =>
        SecureStorage.Default.SetAsync(key, JsonSerializer.Serialize(new Stored(credentials.Server.ToString(), credentials.DeviceId, credentials.Token, AppId: credentials.AppId)));

    public async Task ClearAsync(CancellationToken ct = default)
    {
        // Remove reports a failed keychain query as false rather than throwing, and false also means "nothing there", so look again.
        if (SecureStorage.Default.Remove(key)) return;
        if (!string.IsNullOrEmpty(await SecureStorage.Default.GetAsync(key)))
            throw new InvalidOperationException("The secure store did not let go of the device token.");
    }
}
