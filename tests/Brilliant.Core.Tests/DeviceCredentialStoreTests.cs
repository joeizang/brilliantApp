using Brilliant.Core.Sync;

namespace Brilliant.Core.Tests;

public class DeviceCredentialStoreTests : IDisposable
{
    private static readonly DeviceCredentials Creds = new(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "brilliant-creds-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "nested", "device.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class MemoryStore : IDeviceCredentialStore
    {
        public DeviceCredentials? Saved;
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) { Saved = credentials; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct = default) { Saved = null; return Task.CompletedTask; }
    }

    /// <summary>A key store that refuses everything, as an unsigned Mac build's keychain does (MissingEntitlement).</summary>
    private sealed class Refusing : IDeviceCredentialStore
    {
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => throw new InvalidOperationException("MissingEntitlement");
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) => throw new InvalidOperationException("MissingEntitlement");
        public Task ClearAsync(CancellationToken ct = default) => throw new InvalidOperationException("MissingEntitlement");
    }

    [Fact]
    public async Task The_file_store_round_trips_and_creates_its_folder()
    {
        var store = new FileDeviceCredentialStore(FilePath);
        Assert.Null(await store.LoadAsync());

        await store.SaveAsync(Creds);

        Assert.Equal(Creds, await new FileDeviceCredentialStore(FilePath).LoadAsync());
    }

    [Fact]
    public async Task Saving_again_replaces_the_old_credentials_completely()
    {
        var store = new FileDeviceCredentialStore(FilePath);
        await store.SaveAsync(Creds with { Token = "a-much-longer-token-than-the-next-one-0123456789" });
        await store.SaveAsync(Creds with { Token = "short" });
        Assert.Equal("short", (await store.LoadAsync())!.Token);
    }

    [Fact]
    public async Task Clearing_removes_the_file_and_is_fine_when_there_is_none()
    {
        var store = new FileDeviceCredentialStore(FilePath);
        await store.ClearAsync();
        await store.SaveAsync(Creds);
        await store.ClearAsync();
        Assert.Null(await store.LoadAsync());
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task A_corrupt_file_reads_as_not_paired()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, "{ not json");
        Assert.Null(await new FileDeviceCredentialStore(FilePath).LoadAsync());
    }

    [Fact]
    public async Task The_file_is_readable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;
        await new FileDeviceCredentialStore(FilePath).SaveAsync(Creds);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public async Task The_secure_store_is_used_when_it_works_and_the_fallback_is_left_empty()
    {
        MemoryStore secure = new(), fallback = new() { Saved = Creds with { Token = "old" } };
        var store = new FallbackDeviceCredentialStore(secure, fallback);

        await store.SaveAsync(Creds);

        Assert.Equal(Creds, secure.Saved);
        Assert.Null(fallback.Saved);
        Assert.Equal(Creds, await store.LoadAsync());
    }

    [Fact]
    public async Task When_the_secure_store_refuses_the_fallback_keeps_the_credentials()
    {
        var fallback = new MemoryStore();
        var store = new FallbackDeviceCredentialStore(new Refusing(), fallback);

        await store.SaveAsync(Creds);

        Assert.Equal(Creds, fallback.Saved);
        Assert.Equal(Creds, await store.LoadAsync());
    }

    [Fact]
    public async Task Forgetting_clears_both_and_does_not_fail_just_because_the_secure_store_refuses()
    {
        var fallback = new MemoryStore { Saved = Creds };
        var store = new FallbackDeviceCredentialStore(new Refusing(), fallback);

        await store.ClearAsync();

        Assert.Null(fallback.Saved);
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Forgetting_still_clears_the_fallback_when_the_secure_store_clears_normally()
    {
        MemoryStore secure = new() { Saved = Creds }, fallback = new() { Saved = Creds };
        await new FallbackDeviceCredentialStore(secure, fallback).ClearAsync();
        Assert.Null(secure.Saved);
        Assert.Null(fallback.Saved);
    }
}
