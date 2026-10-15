using Brilliant.Core.Apps;

namespace Brilliant.Core.Tests;

public class AppManifestTests
{
    [Fact]
    public void Shipped_dsa_manifest_preserves_the_existing_identity_palette_pack_and_capabilities()
    {
        var app = AppManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "dsa-manifest.yaml")));

        Assert.Equal("com.joeizang.brilliantapp", app.AppId);
        Assert.Equal("Brilliant", app.DisplayName);
        Assert.Null(app.Icon);
        Assert.Equal(new AppAccent("#78a8ff", "#2456d6", "#08101f", "#ffffff"), app.Accent);
        Assert.Equal("track-1.pack.zip", app.ContentPack);
        Assert.Equal(["python"], app.Runtimes);
        Assert.True(app.StepTypes.SetEquals(["explain", "choice", "predict-output", "write-code", "fill-blank", "parsons", "trace"]));
    }

    private const string Dsa = """
        appId: com.joeizang.brilliantapp
        displayName: Brilliant
        icon: null
        accent:
          dark: '#78a8ff'
          light: '#2456d6'
          onDark: '#08101f'
          onLight: '#ffffff'
        contentPack: track-1.pack.zip
        runtimes: [python]
        stepTypes: [explain, choice, predict-output, write-code, fill-blank, parsons, trace]
        """;

    [Fact]
    public void Parses_identity_and_answers_capability_questions()
    {
        var app = AppManifest.Parse(Dsa);

        Assert.Equal("com.joeizang.brilliantapp", app.AppId);
        Assert.Equal("Brilliant", app.DisplayName);
        Assert.Null(app.Icon);
        Assert.Equal("#78a8ff", app.Accent.Dark);
        Assert.Equal("#2456d6", app.Accent.Light);
        Assert.Equal("#08101f", app.Accent.OnDark);
        Assert.Equal("#ffffff", app.Accent.OnLight);
        Assert.Equal("track-1.pack.zip", app.ContentPack);
        Assert.True(app.SupportsRuntime("python"));
        Assert.False(app.SupportsRuntime("csharp"));
        Assert.True(app.SupportsStepType("trace"));
        Assert.False(app.SupportsStepType("lowered"));
    }

    [Theory]
    [InlineData("appId: com.joeizang.brilliantapp", "appId: ''", "appId")]
    [InlineData("displayName: Brilliant", "displayName: ''", "displayName")]
    [InlineData("contentPack: track-1.pack.zip", "contentPack: ../other.pack.zip", "contentPack")]
    [InlineData("dark: '#78a8ff'", "dark: 'red; color: black'", "accent.dark")]
    [InlineData("runtimes: [python]", "runtimes: [python, python]", "runtimes")]
    [InlineData("stepTypes: [explain, choice, predict-output, write-code, fill-blank, parsons, trace]", "stepTypes: []", "stepTypes")]
    public void Invalid_fields_report_the_field(string before, string after, string field)
    {
        var ex = Assert.Throws<AppManifestException>(() => AppManifest.Parse(Dsa.Replace(before, after)));

        Assert.Contains(field, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# empty document")]
    [InlineData("[unterminated")]
    [InlineData("null")]
    [InlineData("42")]
    public void Malformed_or_empty_documents_have_a_manifest_diagnostic(string yaml)
    {
        var ex = Assert.Throws<AppManifestException>(() => AppManifest.Parse(yaml));
        Assert.Contains("manifest", ex.Message);
    }

    [Fact]
    public void Typos_and_duplicate_keys_are_rejected()
    {
        Assert.Throws<AppManifestException>(() => AppManifest.Parse(Dsa + "\ndisplayNmae: Typo"));
        Assert.Throws<AppManifestException>(() => AppManifest.Parse(Dsa + "\nappId: another.app"));
    }

    [Theory]
    [InlineData("appId: com.joeizang.brilliantapp")]
    [InlineData("displayName: Brilliant")]
    [InlineData("contentPack: track-1.pack.zip")]
    [InlineData("runtimes: [python]")]
    [InlineData("stepTypes: [explain, choice, predict-output, write-code, fill-blank, parsons, trace]")]
    [InlineData("  light: '#2456d6'")]
    public void Required_fields_cannot_be_omitted(string field)
    {
        Assert.Throws<AppManifestException>(() => AppManifest.Parse(Dsa.Replace(field, "")));
    }

    [Fact]
    public void An_app_can_disable_all_code_runtimes_and_declare_its_icon()
    {
        var app = AppManifest.Parse(Dsa.Replace("runtimes: [python]", "runtimes: []")
            .Replace("icon: null", "icon: apps/example/icon.svg"));

        Assert.Empty(app.Runtimes);
        Assert.False(app.SupportsRuntime("python"));
        Assert.Equal("apps/example/icon.svg", app.Icon);
    }
}
