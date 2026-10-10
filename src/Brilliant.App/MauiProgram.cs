using Brilliant.Core.Content;
using Brilliant.Core.Drafts;
using Brilliant.Core.Progress;
using Brilliant.Core.Python;
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
        builder.Services.AddSingleton<IProgressEventLog>(_ =>
            new SqliteProgressEventLog($"Data Source={Path.Combine(dataDir, "brilliant.db")}"));
        builder.Services.AddSingleton(sp =>
            new ProgressRecorder(sp.GetRequiredService<IProgressEventLog>(), DeviceId.GetOrCreate(dataDir)));

        // Code drafts are mutable (last write wins), so they live apart from the append-only event log.
        builder.Services.AddSingleton<ICodeDraftStore>(_ =>
            new SqliteCodeDraftStore($"Data Source={Path.Combine(dataDir, "drafts.db")}"));

        // Shortcuts and menu commands reach whichever step is on screen through one bus, shared by the webview's key listener and the native menu.
        builder.Services.AddSingleton<StepShortcuts>();
        builder.Services.AddSingleton(TimeProvider.System);   // the Trace Player's playback timer

        // The Python runtime talks to the webview it lives in, so it is scoped to the BlazorWebView.
        builder.Services.AddScoped<IPythonRuntime, PyodideRuntime>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        return builder.Build();
    }
}
