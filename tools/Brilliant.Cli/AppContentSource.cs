using Brilliant.Core.Apps;

namespace Brilliant.Cli;

/// <summary>Resolves an app's authoring directory and capabilities from its repository manifest.</summary>
public sealed record AppContentSource(string Root, AppManifest App)
{
    public static AppContentSource FromManifest(string manifestPath)
    {
        var file = new FileInfo(manifestPath);
        var repository = file.Directory?.Parent?.Parent
            ?? throw new AppManifestException("Expected apps/<app>/manifest.yaml.");
        var app = AppManifest.Parse(File.ReadAllText(file.FullName));
        return new(ResolveRoot(app, repository.FullName, file.Directory!.Name), app);
    }

    internal static string ResolveRoot(AppManifest app, string repository, string appName) =>
        Path.GetFullPath(Path.Combine(repository, app.ContentRoot ?? Path.Combine("content", appName)));

    internal static AppManifest? Infer(string contentRoot)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(contentRoot));
        if (directory.Parent?.Name != "content" || directory.Parent.Parent is not { } repository) return null;
        var source = FromManifest(Path.Combine(repository.FullName, "apps", directory.Name, "manifest.yaml"));
        if (source.Root.TrimEnd(Path.DirectorySeparatorChar) != directory.FullName.TrimEnd(Path.DirectorySeparatorChar))
            throw new AppManifestException($"App '{source.App.AppId}' selects content directory '{source.Root}', not '{directory.FullName}'.");
        return source.App;
    }
}
