using Brilliant.Core.Content;
using Brilliant.Core.Drafts;
using Brilliant.Core.Progress;
using Brilliant.Core.Python;
using Brilliant.Core.Sync;
using Brilliant.Data;
using Brilliant.Lessons.UI;

namespace Brilliant.App;

public static class MauiProgram
{
    public const string BundledPackName = "track-1.pack.zip";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

        // The bundled Content Pack is loaded once and shared as an immutable graph.
        builder.Services.AddSingleton<ContentGraph>(_ =>
        {
            using var stream = FileSystem.OpenAppPackageFileAsync(BundledPackName).GetAwaiter().GetResult();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            return ContentPackFormat.Load(buffer);
        });

        // Progress lives in an append-only event log in on-device SQLite.
        var dataDir = FileSystem.AppDataDirectory;
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
        builder.Services.AddSingleton<ICodeDraftStore>(_ =>
            new SqliteCodeDraftStore($"Data Source={Path.Combine(dataDir, "drafts.db")}"));

        // Shortcuts and menu commands reach whichever step is on screen through one bus, shared by the webview's key listener and the native menu.
        builder.Services.AddSingleton<StepShortcuts>();
        builder.Services.AddSingleton(TimeProvider.System);   // the Trace Player's playback timer

        // Pairing with the household server over plain HTTP on the LAN; the token it returns goes in the platform's secure storage.
        // An unsigned Mac build has no keychain entitlement (MissingEntitlement), so the token falls back to a private file in the app's sandbox.
        builder.Services.AddSingleton<IDeviceCredentialStore>(_ => new FallbackDeviceCredentialStore(
            new SecureDeviceCredentialStore(), new FileDeviceCredentialStore(Path.Combine(dataDir, "device-credentials.json"))));
        builder.Services.AddSingleton(_ => new PairingClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
        builder.Services.AddSingleton<PairingService>();

        // Sync keeps the event log in step with the server: at start, every couple of minutes, and on demand from the Sync screen.
        // The server being away never matters to the learner; the app runs from its local log.
        builder.Services.AddSingleton(_ => new SyncClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }));
        builder.Services.AddSingleton<ISyncStateStore>(_ => new FileSyncStateStore(Path.Combine(dataDir, "sync-state.json")));
        builder.Services.AddSingleton(sp => new SyncService(
            sp.GetRequiredService<IProgressEventLog>(), DeviceId.GetOrCreate(dataDir),
            sp.GetRequiredService<IDeviceCredentialStore>(), sp.GetRequiredService<SyncClient>(), sp.GetRequiredService<ISyncStateStore>()));
        builder.Services.AddSingleton(sp => new SyncScheduler(sp.GetRequiredService<SyncService>(), TimeSpan.FromMinutes(2)));

        // The Python runtime talks to the webview it lives in, so it is scoped to the BlazorWebView.
        builder.Services.AddScoped<IPythonRuntime, PyodideRuntime>();

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
