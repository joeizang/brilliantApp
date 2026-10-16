using System.Globalization;
using System.Net;
using System.Xml.Linq;
using Brilliant.Core.Apps;

namespace Brilliant.Cli;

/// <summary>Projects a validated manifest into native build metadata and the WebView's initial HTML.</summary>
public static class AppBuildInputs
{
    public static void Write(string manifestPath, string templatePath, string repositoryRoot, string outputDirectory)
    {
        var app = AppManifest.Parse(File.ReadAllText(manifestPath));
        if (app.DisplayName.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new AppManifestException("App displayName cannot contain path separators or NUL (it names the Mac app bundle).");
        var icon = app.Icon is null ? "" : Path.GetFullPath(Path.Combine(repositoryRoot, app.Icon));
        if (icon.Length > 0 && !File.Exists(icon)) throw new AppManifestException($"App icon does not exist: {icon}");
        var template = File.ReadAllText(templatePath);
        if (!template.Contains("{{APP_TITLE}}", StringComparison.Ordinal) || !template.Contains("{{APP_STYLE}}", StringComparison.Ordinal))
            throw new AppManifestException("App HTML template requires APP_TITLE and APP_STYLE placeholders.");

        var darkDot = app.AppId == AppStoragePaths.DsaAppId ? "rgba(160, 185, 255, .07)" : Dot(app.Accent.Dark, ".07");
        var lightDot = Dot(app.Accent.Light, ".09");
        var css = $$"""
            <style>
            :root { --accent: {{app.Accent.Dark}}; --on-accent: {{app.Accent.OnDark}}; --dot: {{darkDot}}; }
            @media (prefers-color-scheme: light) {
                :root { --accent: {{app.Accent.Light}}; --on-accent: {{app.Accent.OnLight}}; --dot: {{lightDot}}; }
            }
            </style>
            """;
        var html = template.Replace("{{APP_TITLE}}", WebUtility.HtmlEncode(app.DisplayName), StringComparison.Ordinal)
            .Replace("{{APP_STYLE}}", css, StringComparison.Ordinal);
        var appName = new FileInfo(manifestPath).Directory!.Name;
        var contentRoot = AppContentSource.ResolveRoot(app, repositoryRoot, appName);
        var xml = new XDocument(new XElement("App",
            new XElement("ApplicationId", app.AppId), new XElement("ApplicationTitle", app.DisplayName),
            new XElement("Icon", icon), new XElement("ContentPack", app.ContentPack),
            new XElement("ContentRoot", contentRoot)));
        var android = XDocument.Load(Path.Combine(repositoryRoot, "src", "Brilliant.App", "Platforms", "Android", "AndroidManifest.xml"));
        if (icon.Length > 0)
        {
            XNamespace ns = "http://schemas.android.com/apk/res/android";
            var application = android.Root?.Element("application")
                ?? throw new AppManifestException("Android manifest template requires an application element.");
            application.SetAttributeValue(ns + "icon", "@mipmap/" + Path.GetFileNameWithoutExtension(icon));
        }
        Directory.CreateDirectory(outputDirectory);
        WriteIfChanged(Path.Combine(outputDirectory, "app.xml"), xml.ToString());
        WriteIfChanged(Path.Combine(outputDirectory, "index.html"), html);
        WriteIfChanged(Path.Combine(outputDirectory, "AndroidManifest.xml"), android.ToString());
    }

    public static void WriteRuntimeSelection(string manifestPath, string outputPath)
    {
        var app = AppManifest.Parse(File.ReadAllText(manifestPath));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        WriteIfChanged(outputPath, new XElement("Runtimes", new XElement("PythonEnabled", app.SupportsRuntime("python"))).ToString());
    }

    private static string Dot(string hex, string alpha)
    {
        int Channel(int at) => int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return $"rgba({Channel(1)}, {Channel(3)}, {Channel(5)}, {alpha})";
    }

    private static void WriteIfChanged(string path, string value)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != value) File.WriteAllText(path, value);
    }
}
