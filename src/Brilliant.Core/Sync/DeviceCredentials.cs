namespace Brilliant.Core.Sync;

/// <summary>What a paired device keeps: where the server is and the token that proves this device is paired with it.</summary>
public sealed record DeviceCredentials(Uri Server, string DeviceId, string Token);

/// <summary>Holds <see cref="DeviceCredentials"/> in the platform's secure storage (the keychain on a Mac, the keystore on Android).</summary>
public interface IDeviceCredentialStore
{
    Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
