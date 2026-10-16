using System.Xml.Linq;
using Brilliant.Core.Apps;

namespace Brilliant.Cli.Tests;

public sealed class AppBuildInputsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brilliant-flavor-" + Guid.NewGuid());

    public AppBuildInputsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "Brilliant.App", "Platforms", "Android"));
        File.WriteAllText(Path.Combine(_root, "src", "Brilliant.App", "Platforms", "Android", "AndroidManifest.xml"),
            """<manifest xmlns:android="http://schemas.android.com/apk/res/android"><application android:allowBackup="true"/></manifest>""");
    }

    [Theory]
    [InlineData("runtimes: [python]", "true")]
    [InlineData("runtimes:\n  - python", "true")]
    [InlineData("runtimes: []", "false")]
    public void Runtime_build_selection_uses_the_validated_manifest_parser(string runtimes, string enabled)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "BrilliantApp.sln"))) repo = repo.Parent;
        var yaml = File.ReadAllText(Path.Combine(repo!.FullName, "apps/dsa/manifest.yaml"));
        var path = Path.Combine(_root, "manifest.yaml");
        File.WriteAllText(path, yaml.Replace("runtimes: [python]", runtimes));
        var output = Path.Combine(_root, "selection.xml");

        AppBuildInputs.WriteRuntimeSelection(path, output);

        Assert.Equal(enabled, XDocument.Load(output).Root!.Element("PythonEnabled")!.Value);
    }

    [Fact]
    public void Manifest_generates_native_identity_and_branding_before_Blazor_renders()
    {
        File.WriteAllText(Path.Combine(_root, "manifest.yaml"), """
            appId: example.flavor
            displayName: 'Learn & Test'
            icon: icon.svg
            accent:
              dark: '#aabbcc'
              light: '#112233'
              onDark: '#000000'
              onLight: '#ffffff'
            contentPack: example.pack.zip
            contentRoot: content/another-source
            runtimes: [python]
            stepTypes: [explain]
            """);
        File.WriteAllText(Path.Combine(_root, "icon.svg"), "<svg/>");
        File.WriteAllText(Path.Combine(_root, "index.html"), "<title>{{APP_TITLE}}</title>{{APP_STYLE}}");

        AppBuildInputs.Write(Path.Combine(_root, "manifest.yaml"), Path.Combine(_root, "index.html"), _root, Path.Combine(_root, "generated"));

        var properties = XDocument.Load(Path.Combine(_root, "generated", "app.xml"));
        Assert.Equal("example.flavor", properties.Root!.Element("ApplicationId")!.Value);
        Assert.Equal("Learn & Test", properties.Root.Element("ApplicationTitle")!.Value);
        Assert.Equal("example.pack.zip", properties.Root.Element("ContentPack")!.Value);
        Assert.Equal(Path.Combine(_root, "content", "another-source"), properties.Root.Element("ContentRoot")!.Value);
        Assert.Equal(Path.Combine(_root, "icon.svg"), properties.Root.Element("Icon")!.Value);
        var html = File.ReadAllText(Path.Combine(_root, "generated", "index.html"));
        Assert.Contains("<title>Learn &amp; Test</title>", html);
        Assert.Contains("--accent: #aabbcc", html);
        Assert.Contains("--accent: #112233", html);
        Assert.Contains("--dot: rgba(17, 34, 51, .09)", html);
        Assert.DoesNotContain("{{APP_", html);
        var android = XDocument.Load(Path.Combine(_root, "generated", "AndroidManifest.xml"));
        XNamespace ns = "http://schemas.android.com/apk/res/android";
        Assert.Equal("@mipmap/icon", (string?)android.Root!.Element("application")!.Attribute(ns + "icon"));
        Assert.Equal("true", (string?)android.Root.Element("application")!.Attribute(ns + "allowBackup"));
    }

    [Fact]
    public void A_missing_icon_fails_before_any_generated_files_are_written()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "BrilliantApp.sln"))) repo = repo.Parent;
        var manifest = File.ReadAllText(Path.Combine(repo!.FullName, "apps", "dsa", "manifest.yaml"));
        File.WriteAllText(Path.Combine(_root, "manifest.yaml"), manifest.Replace("icon: null", "icon: missing.svg"));

        var ex = Assert.Throws<AppManifestException>(() => AppBuildInputs.Write(Path.Combine(_root, "manifest.yaml"),
            "not-read.html", _root, Path.Combine(_root, "generated")));

        Assert.Contains("missing.svg", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "generated")));
    }

    [Fact]
    public void Dsa_build_keeps_its_existing_title_and_both_background_dot_colors()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "BrilliantApp.sln"))) repo = repo.Parent;

        AppBuildInputs.Write(Path.Combine(repo!.FullName, "apps", "dsa", "manifest.yaml"),
            Path.Combine(repo.FullName, "src", "Brilliant.App", "wwwroot", "index.html"), repo.FullName, _root);

        var html = File.ReadAllText(Path.Combine(_root, "index.html"));
        Assert.Contains("<title>Brilliant</title>", html);
        Assert.Contains("--dot: rgba(160, 185, 255, .07)", html);
        Assert.Contains("--dot: rgba(36, 86, 214, .09)", html);
    }

    [Fact]
    public void A_display_name_cannot_turn_the_native_bundle_into_a_path()
    {
        File.WriteAllText(Path.Combine(_root, "manifest.yaml"), """
            appId: example.app
            displayName: '../Other'
            accent: {dark: '#aabbcc', light: '#112233', onDark: '#000000', onLight: '#ffffff'}
            contentPack: example.pack.zip
            runtimes: []
            stepTypes: [explain]
            """);

        var ex = Assert.Throws<AppManifestException>(() => AppBuildInputs.Write(Path.Combine(_root, "manifest.yaml"),
            "not-read.html", _root, Path.Combine(_root, "generated")));

        Assert.Contains("displayName", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "generated")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
