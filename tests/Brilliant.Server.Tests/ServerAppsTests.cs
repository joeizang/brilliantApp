using Brilliant.Core.Apps;

namespace Brilliant.Server.Tests;

public sealed class ServerAppsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brilliant-server-apps-" + Guid.NewGuid());
    private const string Manifest = """
        appId: example.sample
        displayName: Sample
        accent:
          dark: '#112233'
          light: '#445566'
          onDark: '#ffffff'
          onLight: '#000000'
        contentPack: sample.pack.zip
        runtimes: []
        stepTypes: [explain]
        """;

    private void Write(string name, string yaml)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.yaml"), yaml);
    }

    [Fact]
    public void Known_ids_come_from_validated_manifests_and_match_exactly()
    {
        Write("sample", Manifest);
        Write("other", Manifest.Replace("example.sample", "example.other"));
        var apps = ServerApps.Load(_root);
        Assert.True(apps.Contains("example.sample"));
        Assert.True(apps.Contains("example.other"));
        Assert.False(apps.Contains("Example.Sample"));
        Assert.False(apps.Contains("example.unknown"));
        Assert.False(apps.Contains(null));
    }

    [Fact]
    public void Duplicate_ids_fail_startup_instead_of_selecting_one_manifest()
    {
        Write("sample", Manifest);
        Write("duplicate", Manifest);
        Assert.Contains("Duplicate server app ID", Assert.Throws<AppManifestException>(() => ServerApps.Load(_root)).Message);
    }

    [Fact]
    public void Invalid_manifest_fails_startup_instead_of_enabling_an_unknown_app()
    {
        Write("sample", Manifest.Replace("example.sample", "bad/id"));
        Assert.Throws<AppManifestException>(() => ServerApps.Load(_root));
    }

    [Fact]
    public void Empty_catalog_is_an_error()
    {
        Directory.CreateDirectory(_root);
        Assert.Contains("No app manifests", Assert.Throws<AppManifestException>(() => ServerApps.Load(_root)).Message);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
