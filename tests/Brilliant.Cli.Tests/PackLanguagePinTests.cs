using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

public sealed class PackLanguagePinTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-pack-pins-" + Guid.NewGuid());
    private string Root => Path.Combine(_repository, "content", "sample");

    private void Write(string metadata, string runtimes = "csharp")
    {
        var app = Path.Combine(_repository, "apps", "sample");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "manifest.yaml"), $$"""
            appId: example.sample
            displayName: Sample
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [{{runtimes}}]
            stepTypes: [explain]
            """);
        Directory.CreateDirectory(Path.Combine(Root, "tracks", "one", "one"));
        File.WriteAllText(Path.Combine(Root, "pack.yaml"), "id: pack.sample\nversion: 1.0.0\n" + metadata);
        File.WriteAllText(Path.Combine(Root, "tracks", "one", "track.yaml"), "id: track.one\ntitle: One\n");
        File.WriteAllText(Path.Combine(Root, "tracks", "one", "one", "lesson.yaml"), """
            id: lesson.one
            title: One
            steps:
              - id: step.one
                title: One
                type: explain
                body: Welcome
            """);
    }

    [Fact]
    public void An_explanation_only_Csharp_app_still_requires_and_packs_its_compiler_pins()
    {
        Write("targetFramework: net10.0\nlangVersion: 14\n");
        var output = Path.Combine(_repository, "sample.zip");

        var report = ContentPacker.Pack(Root, output);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var manifest = ContentPackFormat.Load(output).Manifest;
        Assert.Equal("net10.0", manifest.TargetFramework);
        Assert.Equal("14", manifest.LangVersion);
    }

    [Theory]
    [InlineData("", "'targetFramework' is required")]
    [InlineData("targetFramework: net10.0\n", "'langVersion' is required")]
    [InlineData("langVersion: 14\n", "'targetFramework' is required")]
    public void Csharp_apps_cannot_omit_either_pin(string metadata, string diagnostic)
    {
        Write(metadata);
        Assert.Contains(ContentValidator.Validate(Root).Errors, e => e.File == "pack.yaml" && e.Message.Contains(diagnostic));
    }

    [Theory]
    [InlineData("csharp", "targetFramework: net9.0\nlangVersion: 14\n", "unsupported targetFramework")]
    [InlineData("csharp", "targetFramework: net10.0\nlangVersion: preview\n", "unsupported langVersion")]
    [InlineData("python", "targetFramework: net11.0\nlangVersion: 14\n", "unsupported targetFramework")]
    [InlineData("python", "targetFramework: net10.0\nlangVersion: latest\n", "unsupported langVersion")]
    [InlineData("", "targetFramework: ''\nlangVersion: 14\n", "unsupported targetFramework")]
    public void Unsupported_supplied_pins_are_rejected_for_every_app(string runtimes, string metadata, string diagnostic)
    {
        Write(metadata, runtimes);
        Assert.Contains(ContentValidator.Validate(Root).Errors, e => e.Message.Contains(diagnostic));
    }

    [Fact]
    public void Python_apps_keep_their_existing_unpinned_manifest_shape()
    {
        Write("", "python");
        var report = ContentValidator.Load(Root, out var content);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        Assert.Null(content!.Manifest.TargetFramework);
        Assert.Null(content.Manifest.LangVersion);
    }

    public void Dispose() => Directory.Delete(_repository, recursive: true);
}
