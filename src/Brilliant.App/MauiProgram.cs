using Brilliant.Core.Apps;
using Brilliant.Core.Content;
using Brilliant.Core.Drafts;
using Brilliant.Core.Progress;
using Brilliant.Core.Code;
using Brilliant.Core.Sync;
#if BRILLIANT_PYTHON
using Brilliant.Runtime.Python;
#endif
#if BRILLIANT_CSHARP
using Brilliant.Runtime.CSharp;
#endif
using Brilliant.Data;
using Brilliant.Lessons.UI;

namespace Brilliant.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        using var manifestStream = FileSystem.OpenAppPackageFileAsync("app-manifest.yaml").GetAwaiter().GetResult();
        using var manifestReader = new StreamReader(manifestStream);
        var manifest = AppManifest.Parse(manifestReader.ReadToEnd());

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton(manifest);

        var storage = new AppStoragePaths(manifest, FileSystem.AppDataDirectory);
        var dataDir = storage.DataDirectory;
        Directory.CreateDirectory(dataDir);

        // Content Packs: the bundled one, or a newer version of it that the app downloaded from the household server (docs/content-distribution.md).
        // The choice is made once, at start, and shared as an immutable graph; a download is used at the next start, never swapped under the learner.
        var packCache = new PackCache(Path.Combine(dataDir, "packs"));
        builder.Services.AddSingleton(packCache);
        builder.Services.AddSingleton<ContentGraph>(_ => PackStartup.Choose(() =>
        {
            using var stream = FileSystem.OpenAppPackageFileAsync(manifest.ContentPack).GetAwaiter().GetResult();
            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            return buffer;
        }, packCache));

        // Progress lives in an append-only event log in on-device SQLite.
        builder.Services.AddSingleton<IProgressEventLog>(sp =>
        {
#if DEBUG
            // BRILLIANT_DEV_START_AT=<lesson id> opens the app at that lesson on a throwaway in-memory log (see DevProgress).
            if (DevProgress.TryCreate(sp.GetRequiredService<ContentGraph>()) is { } scratch) return scratch;
#endif
            return new SqliteProgressEventLog($"Data Source={Path.Combine(dataDir, "brilliant.db")}");
        });
        builder.Services.AddSingleton(sp =>
            new ProgressRecorder(sp.GetRequiredService<IProgressEventLog>(), DeviceId.GetOrCreate(dataDir)));

        // Code drafts are mutable (last write wins), so they live apart from the append-only event log.
        // Sync reads and merges them through IDraftSyncSource, the same store.
        builder.Services.AddSingleton(_ => new SqliteCodeDraftStore($"Data Source={Path.Combine(dataDir, "drafts.db")}"));
        builder.Services.AddSingleton<ICodeDraftStore>(sp => sp.GetRequiredService<SqliteCodeDraftStore>());
        builder.Services.AddSingleton<IDraftSyncSource>(sp => sp.GetRequiredService<SqliteCodeDraftStore>());

        // Shortcuts and menu commands reach whichever step is on screen through one bus, shared by the webview's key listener and the native menu.
        builder.Services.AddSingleton<StepShortcuts>();
        builder.Services.AddSingleton(new ScreenInfo(csharpWriteCodeAvailable: !OperatingSystem.IsAndroid()));
        builder.Services.AddSingleton(TimeProvider.System);   // the Trace Player's playback timer

        // Pairing with the household server over plain HTTP on the LAN; the token it returns goes in the platform's secure storage.
        // An unsigned Mac build has no keychain entitlement (MissingEntitlement), so the token falls back to a private file in the app's sandbox.
        builder.Services.AddSingleton<IDeviceCredentialStore>(_ => new FallbackDeviceCredentialStore(
            new SecureDeviceCredentialStore(storage.CredentialsKey), new FileDeviceCredentialStore(Path.Combine(dataDir, "device-credentials.json"))));
        builder.Services.AddSingleton(_ => new PairingClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, manifest.AppId));
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton(new DeviceLabel(Microsoft.Maui.Devices.DeviceInfo.Current.Name));   // "Pixel 9 Pro XL", not the "localhost" Android reports as its machine name

        // Sync keeps the event log in step with the server: at start, every couple of minutes, and on demand from the Sync screen.
        // The server being away never matters to the learner; the app runs from its local log.
        builder.Services.AddSingleton(_ => new SyncClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, manifest.AppId));
        builder.Services.AddSingleton<ISyncStateStore>(_ => new FileSyncStateStore(Path.Combine(dataDir, "sync-state.json")));
        builder.Services.AddSingleton(sp => new SyncService(
            sp.GetRequiredService<IProgressEventLog>(), DeviceId.GetOrCreate(dataDir),
            sp.GetRequiredService<IDeviceCredentialStore>(), sp.GetRequiredService<SyncClient>(), sp.GetRequiredService<ISyncStateStore>(),
            drafts: sp.GetRequiredService<IDraftSyncSource>()));
        builder.Services.AddSingleton(_ => new PackClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, manifest.AppId));
        builder.Services.AddSingleton(sp => new ContentUpdateService(
            sp.GetRequiredService<ContentGraph>(), sp.GetRequiredService<PackCache>(), sp.GetRequiredService<PackClient>(), sp.GetRequiredService<IDeviceCredentialStore>()));
        builder.Services.AddSingleton(sp =>
        {
            var updates = sp.GetRequiredService<ContentUpdateService>();
            return new SyncScheduler(sp.GetRequiredService<SyncService>(), TimeSpan.FromMinutes(2), alsoEachTime: ct => updates.CheckAsync(ct));   // look for newer content after each sync
        });

        // The Python runtime talks to the webview it lives in, so it is scoped to the BlazorWebView.
#if BRILLIANT_PYTHON
        builder.Services.AddScoped<ICodeRuntime, PyodideRuntime>();
#endif
#if BRILLIANT_CSHARP
        // The native compilation lifetime is process-wide, including reopened Blazor scopes.
        builder.Services.AddSingleton<ICodeRuntime, CSharpRuntime>(_ => new CSharpRuntime());
#endif
        builder.Services.AddScoped(sp => new CodeRuntimeRegistry(sp.GetServices<ICodeRuntime>()));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        var app = builder.Build();
#if DEBUG
        // The throwaway log of BRILLIANT_DEV_START_AT must never be synced to the household server.
        if (Environment.GetEnvironmentVariable("BRILLIANT_DEV_START_AT") is null)
#endif
            app.Services.GetRequiredService<SyncScheduler>().Start();
        return app;
    }
}
