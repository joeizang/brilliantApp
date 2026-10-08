using Brilliant.Core.Content;

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

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        return builder.Build();
    }
}
