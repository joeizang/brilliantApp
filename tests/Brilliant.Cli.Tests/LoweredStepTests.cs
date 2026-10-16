using Brilliant.Core.Apps;
using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

/// <summary>The <c>lowered</c> step: what the compiler made of a C# snippet, built when the pack is built (#154).</summary>
public sealed class LoweredStepTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-lowered-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_repository, "content", "sample");

    private const string Snippet = "      static class Demo\n      {\n          public static int Total(List<int> items)\n          {\n              var sum = 0;\n              foreach (var item in items) sum += item;\n              return sum;\n          }\n      }";

    private void WritePack(string stepTypes = "[explain, choice, lowered]", string step = "", string packExtras = "targetFramework: net10.0\nlangVersion: '14'\n")
    {
        Write("apps/sample/manifest.yaml", $$"""
            appId: example.lowered
            displayName: Lowered
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [csharp]
            stepTypes: {{stepTypes}}
            """);
        Write("content/sample/pack.yaml", "id: pack.lowered\nversion: 1.0.0\n" + packExtras);
        Write("content/sample/tracks/one/track.yaml", "id: track.lowered\ntitle: Lowered\n");
        Write("content/sample/tracks/one/foreach/lesson.yaml", "id: lesson.lowered\ntitle: foreach\nsteps:\n" + (step.Length > 0 ? step : ChoiceFormStep()));
    }

    private static string ChoiceFormStep(string extra = "") => $$"""
          - id: step.lowered.foreach
            type: lowered
            title: What foreach becomes
            prompt: Which call ends the loop?
            code: |
        {{Snippet}}
            options:
              - text: MoveNext
                correct: true
                feedback: The loop runs while MoveNext returns true.
              - text: Dispose
        {{extra}}
        """ + "\n";

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() { try { Directory.Delete(_repository, true); } catch (IOException) { } }

    private ContentGraph PackAndLoad()
    {
        var bytes = ContentPacker.PackToBytes(Root, out var report);
        Assert.True(report.IsValid, string.Join('\n', report.Errors));
        return ContentPackFormat.Load(new MemoryStream(bytes!));
    }

    [Fact]
    public void A_lowered_step_carries_the_lowered_csharp_and_il_in_the_pack()
    {
        WritePack();

        var step = Assert.IsType<LoweredStep>(PackAndLoad().Lessons.Single().Steps.Single());

        Assert.Contains("MoveNext", step.Lowered!.CSharp);
        Assert.Contains("try", step.Lowered.CSharp);
        Assert.Contains("callvirt", step.Lowered.Il, StringComparison.OrdinalIgnoreCase);
        Assert.False(step.IsTyped);
    }

    [Fact]
    public void Packing_the_same_lowered_content_twice_gives_the_same_bytes()
    {
        WritePack();

        var first = ContentPacker.PackToBytes(Root, out _);
        var second = ContentPacker.PackToBytes(Root, out _);

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_dsa_style_manifest_that_does_not_list_lowered_rejects_the_step()
    {
        WritePack("[explain, choice, predict-output, write-code, fill-blank, parsons, trace]");

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "dsa.zip"));

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Message.Contains("step type 'lowered' is not enabled by app 'example.lowered'"));
    }

    [Fact]
    public void The_real_dsa_manifest_does_not_enable_lowered()
    {
        var repository = FindRepositoryRoot();
        var manifest = AppManifest.Parse(File.ReadAllText(Path.Combine(repository, "apps", "dsa", "manifest.yaml")));

        Assert.False(manifest.SupportsStepType("lowered"));
    }

    [Fact]
    public void The_csharp_manifest_enables_lowered()
    {
        var manifest = AppManifest.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "apps", "csharp", "manifest.yaml")));

        Assert.True(manifest.SupportsStepType("lowered"));
    }

    [Fact]
    public void A_snippet_that_does_not_compile_blocks_the_pack_with_its_line()
    {
        WritePack(step: """
              - id: step.lowered.bad
                type: lowered
                title: Broken
                prompt: Anything?
                code: |
                  int a = "two";
                options:
                  - text: A
                    correct: true
                  - text: B
            """ + "\n");

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "bad.zip"));

        var error = Assert.Single(report.Errors);
        Assert.Contains("step.lowered.bad", error.Message);
        Assert.Contains("does not compile (line 1: CS0029", error.Message);
    }

    [Fact]
    public void A_lowered_step_needs_an_answer_and_a_snippet()
    {
        WritePack(step: """
              - id: step.lowered.empty
                type: lowered
                title: Empty
                prompt: Anything?
            """ + "\n");

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "empty.zip"));

        Assert.Contains(report.Errors, e => e.Message.Contains("'code' is required"));
        Assert.Contains(report.Errors, e => e.Message.Contains("either 'accepted' answers (typed) or 'options'"));
    }

    [Fact]
    public void A_lowered_step_in_another_language_is_rejected()
    {
        WritePack(step: ChoiceFormStep().Replace("    code: |", "    language: python\n    code: |"));

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "py.zip"));

        Assert.Contains(report.Errors, e => e.Message.Contains("'language' must be csharp"));
    }

    [Fact]
    public void A_typed_lowered_step_is_answered_like_a_prediction()
    {
        WritePack(step: """
              - id: step.lowered.typed
                type: lowered
                title: Typed
                prompt: Name the call the loop makes each pass.
                code: |
                  var total = 0;
                  foreach (var n in new List<int> { 1 }) total += n;
                accepted: [MoveNext]
                mistakes:
                  - answers: [Next]
                    feedback: C# calls it MoveNext.
            """ + "\n");

        var step = Assert.IsType<LoweredStep>(PackAndLoad().Lessons.Single().Steps.Single());

        Assert.True(step.IsTyped);
        Assert.True(AnswerEvaluator.Evaluate(step, "MoveNext").IsCorrect);
        Assert.Equal("C# calls it MoveNext.", AnswerEvaluator.Evaluate(step, "Next").Feedback);
        Assert.NotNull(step.Lowered);
    }

    [Fact]
    public void A_lowered_step_requires_the_pinned_compiler_in_pack_yaml()
    {
        WritePack(packExtras: "");

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "pins.zip"));

        Assert.Contains(report.Errors, e => e.Message.Contains("lowered steps require targetFramework"));
    }

    [Fact]
    public void A_lowered_step_can_be_answered_by_choice()
    {
        WritePack();
        var step = Assert.IsType<LoweredStep>(PackAndLoad().Lessons.Single().Steps.Single());

        Assert.True(AnswerEvaluator.Evaluate(step, 0).IsCorrect);
        Assert.False(AnswerEvaluator.Evaluate(step, 1).IsCorrect);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "apps", "dsa", "manifest.yaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
