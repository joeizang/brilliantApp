using System.Reflection;
using System.Text.Json.Serialization;
using Brilliant.Core.Apps;
using Brilliant.Core.Content;

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
        Assert.Equal("content/dsa", app.ContentRoot);
        Assert.Equal(["python"], app.Runtimes);
        var knownSteps = typeof(Step).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(type => Assert.IsType<string>(type.TypeDiscriminator));
        // Every step type but the C#-only ones: DSA doesn't show compiler output (#154), async timelines (#159), BenchmarkDotNet results (#155), allocation questions (#156) or interleaving puzzles (#162).
        string[] csharpOnly = ["lowered", "async-timeline", "benchmark-predict", "allocations", "interleave", "review-code", "design-tradeoff"];
        Assert.All(csharpOnly, type => Assert.False(app.SupportsStepType(type)));
        Assert.True(app.StepTypes.SetEquals(knownSteps.Where(type => !csharpOnly.Contains(type))));
        Assert.True(app.StepTypes.SetEquals(StepTypes.All.Where(type => !csharpOnly.Contains(type))));
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

    [Theory]
    [InlineData("content/dsa")]
    [InlineData("content/example")]
    public void Content_source_is_an_optional_repository_relative_path(string root)
    {
        Assert.Null(AppManifest.Parse(Dsa).ContentRoot);
        Assert.Equal(root, AppManifest.Parse(Dsa + $"\ncontentRoot: {root}\n").ContentRoot);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("/absolute")]
    [InlineData("content/../other")]
    [InlineData("content//other")]
    public void Content_sources_cannot_escape_the_repository(string root)
    {
        Assert.Contains("contentRoot", Assert.Throws<AppManifestException>(() => AppManifest.Parse(Dsa + $"\ncontentRoot: {root}\n")).Message);
    }

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

    [Theory]
    [InlineData("trace", "traces", "stepTypes")]
    [InlineData("predict-output", "predict-outputs", "stepTypes")]
    [InlineData("python", "pyhton", "runtimes")]
    public void Unknown_capabilities_list_the_valid_keys(string valid, string typo, string field)
    {
        var ex = Assert.Throws<AppManifestException>(() => AppManifest.Parse(Dsa.Replace(valid, typo)));

        Assert.Contains(field, ex.Message);
        Assert.Contains(typo, ex.Message);
        Assert.Contains(valid, ex.Message);
    }

    [Theory]
    [InlineData("brilliant")]
    [InlineData("com.my-app")]
    [InlineData("com.123app")]
    [InlineData("com.my_app")]
    [InlineData("com..app")]
    [InlineData("com.app.")]
    public void App_ids_must_work_on_both_Android_and_Apple(string id)
    {
        var ex = Assert.Throws<AppManifestException>(() => AppManifest.Parse(Dsa.Replace("com.joeizang.brilliantapp", id)));

        Assert.Contains("appId", ex.Message);
    }

    [Fact]
    public void The_grading_model_is_optional_and_the_dsa_app_names_none()
    {
        Assert.Null(AppManifest.Parse(Dsa).GraderModel);
    }

    [Theory]
    [InlineData("gemma3:12b")]
    [InlineData("qwen3:32b")]
    [InlineData("hf.co/org/model-GGUF:Q4_K_M")]
    [InlineData("llama3")]
    public void A_manifest_can_name_the_Ollama_model_that_gives_feedback(string model)
    {
        Assert.Equal(model, AppManifest.Parse(Dsa + $"\ngraderModel: {model}\n").GraderModel);
    }

    [Theory]
    [InlineData("''")]
    [InlineData("'   '")]
    [InlineData("'two words'")]
    [InlineData("'-leading-dash'")]
    [InlineData("'semi;colon'")]
    [InlineData("'new\\nline'")]
    public void A_grading_model_name_must_be_one_Ollama_tag(string model)
    {
        var ex = Assert.Throws<AppManifestException>(() => AppManifest.Parse(Dsa + $"\ngraderModel: {model}\n"));

        Assert.Contains("graderModel", ex.Message);
    }

    [Fact]
    public void The_shipped_csharp_manifest_names_its_grading_model()
    {
        var app = AppManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "csharp-manifest.yaml")));

        Assert.False(string.IsNullOrWhiteSpace(app.GraderModel));
        Assert.True(app.SupportsStepType("design-tradeoff"));
    }

}
