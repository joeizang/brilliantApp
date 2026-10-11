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

    /// <summary>A keystore that can go away and come back, as the platform's does. While it is away it refuses everything, as an unsigned Mac build's keychain does (MissingEntitlement).</summary>
    private sealed class Keystore : IDeviceCredentialStore
    {
        public DeviceCredentials? Held;
        public bool Available = true;

        private void Check()
        {
            if (!Available) throw new InvalidOperationException("MissingEntitlement");
        }

        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) { Check(); return Task.FromResult(Held); }
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) { Check(); Held = credentials; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct = default) { Check(); Held = null; return Task.CompletedTask; }
    }

    // A new instance over the same file and keystore is what an app restart looks like.
    private FallbackDeviceCredentialStore Store(Keystore keystore) => new(keystore, new FileDeviceCredentialStore(FilePath));

    private static DeviceCredentials Device(string n) => Creds with { DeviceId = "dev-" + n, Token = "tok-" + n };

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
    public async Task The_file_can_say_the_server_was_forgotten_on_purpose()
    {
        var file = new FileDeviceCredentialStore(FilePath);
        await file.MarkForgottenAsync();
        Assert.IsType<FileState.Forgotten>(await file.ReadAsync());
        Assert.Null(await file.LoadAsync());
        await file.SaveAsync(Creds);
        Assert.IsType<FileState.Held>(await file.ReadAsync());
    }

    [Fact]
    public async Task A_working_keystore_holds_the_credentials_and_the_file_stays_absent()
    {
        var keystore = new Keystore();

        await Store(keystore).SaveAsync(Creds);

        Assert.Equal(Creds, keystore.Held);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(Creds, await Store(keystore).LoadAsync());
    }

    [Fact]
    public async Task When_the_keystore_refuses_the_file_keeps_the_credentials_across_restarts()
    {
        var keystore = new Keystore { Available = false };

        await Store(keystore).SaveAsync(Creds);

        Assert.Equal(Creds, await Store(keystore).LoadAsync());
    }

    [Fact]
    public async Task A_save_that_works_empties_a_file_left_from_an_earlier_outage()
    {
        var keystore = new Keystore { Available = false };
        await Store(keystore).SaveAsync(Device("old"));
        keystore.Available = true;

        await Store(keystore).SaveAsync(Device("new"));

        Assert.False(File.Exists(FilePath));
        Assert.Equal(Device("new"), await Store(keystore).LoadAsync());
    }

    [Fact]
    public async Task A_new_pairing_made_during_an_outage_survives_the_keystore_coming_back_with_an_older_one()
    {
        var keystore = new Keystore { Held = Device("old"), Available = false };
        Assert.Null(await Store(keystore).LoadAsync());   // looks unpaired, so the learner pairs again

        await Store(keystore).SaveAsync(Device("new"));
        keystore.Available = true;

        Assert.Equal(Device("new"), await Store(keystore).LoadAsync());
        Assert.Equal(Device("new"), await Store(keystore).LoadAsync());   // and after a restart
    }

    [Fact]
    public async Task Forgetting_during_an_outage_stays_forgotten_when_the_keystore_comes_back_with_the_old_pairing()
    {
        var keystore = new Keystore { Held = Device("old"), Available = false };

        await Store(keystore).ClearAsync();   // can't reach the keystore, but must not pretend or throw
        Assert.Null(await Store(keystore).LoadAsync());
        keystore.Available = true;

        Assert.Null(await Store(keystore).LoadAsync());   // the old token is still in the keystore, and must not come back
        Assert.Equal(Device("old"), keystore.Held);
    }

    [Fact]
    public async Task Pairing_again_after_a_forgotten_outage_replaces_what_the_keystore_kept()
    {
        var keystore = new Keystore { Held = Device("old"), Available = false };
        await Store(keystore).ClearAsync();
        keystore.Available = true;

        await Store(keystore).SaveAsync(Device("new"));

        Assert.Equal(Device("new"), keystore.Held);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(Device("new"), await Store(keystore).LoadAsync());
    }

    [Fact]
    public async Task Forgetting_with_a_working_keystore_leaves_nothing_behind()
    {
        var keystore = new Keystore { Held = Creds };
        await Store(keystore).ClearAsync();
        Assert.Null(keystore.Held);
        Assert.False(File.Exists(FilePath));
        Assert.Null(await Store(keystore).LoadAsync());
    }

    [Fact]
    public async Task Forgetting_a_fallback_pairing_clears_the_file()
    {
        var keystore = new Keystore { Available = false };
        await Store(keystore).SaveAsync(Creds);
        keystore.Available = true;

        await Store(keystore).ClearAsync();

        Assert.False(File.Exists(FilePath));
        Assert.Null(await Store(keystore).LoadAsync());
    }

    [Fact]
    public async Task A_forget_that_cannot_be_made_durable_is_reported()
    {
        // The keystore is away and the file can't be written (its "path" is a folder), so nothing can honour the forget.
        Directory.CreateDirectory(FilePath);
        var store = new FallbackDeviceCredentialStore(new Keystore { Available = false }, new FileDeviceCredentialStore(FilePath));
        await Assert.ThrowsAnyAsync<Exception>(() => store.ClearAsync());
    }
}
