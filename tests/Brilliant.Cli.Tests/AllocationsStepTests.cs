using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

/// <summary>The <c>allocations</c> step as authors write it (#156): predict a snippet's allocation, or rewrite it to meet a byte budget.</summary>
public sealed class AllocationsStepTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-allocations-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_repository, "content", "sample");
    private string AppManifest => Path.Combine(_repository, "apps", "sample", "manifest.yaml");

    private const string BoxCode = "static int Boxed(int n)\n{\n    object o = n;\n    return (int)o + 1;\n}";
    private const string BoxOptions = "- bytes: 0\n- bytes: 24\n- bytes: 48\n  feedback: That would be two boxes.";

    /// <summary>A predict step; <paramref name="options"/>, <paramref name="code"/> and <paramref name="extra"/> are written at column 0 here and indented under the step.</summary>
    private static string Predict(string variant = "variant: predict", string options = BoxOptions, string code = BoxCode, string extra = "") =>
        "  - id: step.alloc.box\n    type: allocations\n" + (variant.Length > 0 ? "    " + variant + "\n" : "") +
        "    title: Boxing\n    prompt: How much does this allocate?\n    entrypoint: Boxed\n    input: '5'\n" + (extra.Length > 0 ? Indent(extra) : "") +
        "    code: |\n" + Indent(code, "      ") + "    options:\n" + Indent(options, "      ");

    private static string Indent(string yaml, string by = "    ") => string.Concat(yaml.Split('\n').Select(line => by + line + "\n"));

    private static readonly string PredictStep = Predict();

    private const string RewriteStep = """
          - id: step.alloc.sum
            type: allocations
            variant: rewrite
            title: Sum without a list
            prompt: Rewrite SumTo so it allocates nothing.
            entrypoint: SumTo
            largeScreenPreferred: true
            starter: |
              static int SumTo(int n) => Enumerable.Range(1, n).ToList().Sum();
            solution: |
              static int SumTo(int n)
              {
                  var total = 0;
                  for (var i = 1; i <= n; i++) total += i;
                  return total;
              }
            tests:
              - input: '4'
                expected: '10'
                maxAllocatedBytes: 0
              - input: '0'
                expected: '0'
            phoneAlternative:
              id: step.alloc.sum.fill
              type: fill-blank
              title: Complete the loop
              prompt: Finish the loop.
              language: csharp
              template: 'for (var i = 1; i <= n; i{{step}}) total += i;'
              blanks:
                - id: step
                  accepted: ['++']
        """;

    private void WritePack(string steps, string stepTypes = "[explain, allocations, write-code, fill-blank]")
    {
        Write("apps/sample/manifest.yaml", $$"""
            appId: example.alloc
            displayName: Alloc
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [csharp]
            stepTypes: {{stepTypes}}
            """);
        Write("content/sample/pack.yaml", "id: pack.alloc\nversion: 1.0.0\ntargetFramework: net10.0\nlangVersion: '14'\n");
        Write("content/sample/tracks/one/track.yaml", "id: track.alloc\ntitle: Alloc\n");
        Write("content/sample/tracks/one/box/lesson.yaml", "id: lesson.alloc\ntitle: Boxing\nsteps:\n" + steps + "\n");
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() { try { Directory.Delete(_repository, true); } catch (IOException) { } }

    private IReadOnlyList<string> Errors() =>
        ContentValidator.Validate(Root, appManifestPath: AppManifest).Errors.Select(e => e.Message).ToList();

    [Fact]
    public void A_predict_step_is_packed_as_a_choice_whose_right_option_is_the_measured_one()
    {
        WritePack(PredictStep);

        var bytes = ContentPacker.PackToBytes(Root, out var report, appManifestPath: AppManifest);
        Assert.True(report.IsValid, string.Join('\n', report.Errors));
        var step = Assert.IsType<PredictOutputStep>(ContentPackFormat.Load(new MemoryStream(bytes!)).Lessons.Single().Steps.Single());

        Assert.Equal(["0 bytes", "24 bytes", "48 bytes"], step.Options.Select(o => o.Text));
        // A boxed int is an object header, a method-table pointer and the value: 24 bytes on CoreCLR.
        Assert.Equal("24 bytes", Assert.Single(step.Options, o => o.Correct).Text);
        Assert.Equal("csharp", step.Code.Language);
        Assert.Contains("static int Boxed", step.Code.Code);
        Assert.EndsWith("Measured call: Boxed(5)", step.Prompt);
    }

    [Fact]
    public void Each_option_says_why_it_is_wrong_and_the_right_one_shows_the_measurement()
    {
        WritePack(PredictStep);

        ContentValidator.Load(Root, out var content, appManifestPath: AppManifest);
        var options = Assert.IsType<PredictOutputStep>(content!.Lessons.Single().Steps.Single()).Options;

        Assert.Equal("Not quite: one call allocates 24 bytes, not 0 bytes.", options[0].Feedback);
        Assert.StartsWith("Measured: one call allocates 24 bytes on .NET (CoreCLR).", options[1].Feedback);
        Assert.Equal("That would be two boxes.", options[2].Feedback);
    }

    [Fact]
    public void Zero_is_a_measurable_answer()
    {
        WritePack(Predict(code: "static int Boxed(int n) => n + 1;"));

        ContentValidator.Load(Root, out var content, appManifestPath: AppManifest);
        var options = Assert.IsType<PredictOutputStep>(content!.Lessons.Single().Steps.Single()).Options;

        Assert.Equal("0 bytes", Assert.Single(options, o => o.Correct).Text);
    }

    [Fact]
    public void A_predict_step_needs_no_phone_alternative_because_nothing_runs_on_the_device()
    {
        WritePack(PredictStep);

        Assert.Empty(Errors());
    }

    [Fact]
    public void No_option_naming_the_measured_bytes_blocks_the_pack_and_says_what_was_measured()
    {
        WritePack(Predict(options: "- bytes: 0\n- bytes: 32\n- bytes: 48"));

        var error = Assert.Single(Errors());

        Assert.Contains("step.alloc.box", error);
        Assert.Contains("allocates 24 bytes", error);
        Assert.Contains("no option says so (options: 0, 32, 48)", error);
    }

    [Fact]
    public void A_snippet_that_does_not_compile_is_reported_with_the_compiler_message()
    {
        WritePack(Predict(code: BoxCode.Replace("(int)o + 1", "(int)o + missing")));

        var error = Assert.Single(Errors());

        Assert.Contains("could not be measured", error);
        Assert.Contains("missing", error);
    }

    [Fact]
    public void Options_need_distinct_byte_counts_in_range_and_cannot_name_the_answer()
    {
        WritePack(Predict(options: "- bytes: 24\n- bytes: 24\n- bytes: -1\n- {bytes: 2000000}\n- {text: lots, correct: true}\n- {feedback: no count}"));

        var errors = Errors();

        Assert.Contains(errors, e => e.Contains("options[1] repeats 24 bytes"));
        Assert.Contains(errors, e => e.Contains("options[2] 'bytes' must be between 0 and 1,000,000"));
        Assert.Contains(errors, e => e.Contains("options[3] 'bytes' must be between 0 and 1,000,000"));
        Assert.Contains(errors, e => e.Contains("options[4] gives a byte count only"));
        Assert.Contains(errors, e => e.Contains("options[5] needs 'bytes'"));
    }

    [Fact]
    public void A_step_needs_two_to_six_options()
    {
        WritePack(Predict(options: "- bytes: 24"));
        Assert.Contains(Errors(), e => e.Contains("needs 2 to 6 options (found 1)"));

        WritePack(Predict(options: "- bytes: 1\n- bytes: 2\n- bytes: 3\n- bytes: 4\n- bytes: 5\n- bytes: 6\n- bytes: 24"));
        Assert.Contains(Errors(), e => e.Contains("needs 2 to 6 options (found 7)"));
    }

    [Fact]
    public void The_variant_must_be_predict_or_rewrite()
    {
        WritePack(Predict(variant: "variant: guess"));
        Assert.Contains(Errors(), e => e.Contains("'variant' must be predict or rewrite (found 'guess')"));

        WritePack(Predict(variant: ""));
        Assert.Contains(Errors(), e => e.Contains("needs a 'variant'"));
    }

    [Fact]
    public void A_predict_step_refuses_the_fields_of_the_other_variant()
    {
        WritePack(Predict(extra: "solution: 'x'\ntests:\n  - {input: '1', expected: '2'}"));

        Assert.Contains(Errors(), e => e.Contains("remove 'accepted', 'mistakes', 'tests', 'starter', 'solution' and 'files'"));
    }

    [Fact]
    public void Another_language_is_refused()
    {
        WritePack(Predict(extra: "language: python"));

        Assert.Contains(Errors(), e => e.Contains("'language' must be csharp"));
    }

    [Fact]
    public void The_variant_and_input_fields_belong_to_allocations_steps_only()
    {
        WritePack("  - id: step.alloc.note\n    type: explain\n    title: Note\n    body: Hello.\n    variant: predict\n");

        Assert.Contains(Errors(), e => e.Contains("'variant' and 'input' only apply to allocations steps"));
    }

    [Fact]
    public void Misplaced_input_and_option_bytes_are_refused_instead_of_ignored()
    {
        WritePack(RewriteStep.Replace("    entrypoint: SumTo\n", "    entrypoint: SumTo\n    input: '4'\n"));
        Assert.Contains(Errors(), e => e.Contains("'input' only applies to variant: predict"));

        WritePack("  - id: step.alloc.pick\n    type: choice\n    title: Pick\n    prompt: Which?\n    options:\n      - {text: A, correct: true}\n      - {text: B, bytes: 8}\n");
        Assert.Contains(Errors(), e => e.Contains("an option's 'bytes' only applies to allocations steps"));
    }

    [Fact]
    public void An_app_that_does_not_list_the_type_refuses_it()
    {
        WritePack(PredictStep, stepTypes: "[explain]");

        Assert.Contains(Errors(), e => e.Contains("step type 'allocations' is not enabled by app 'example.alloc'"));
    }

    [Fact]
    public void A_rewrite_step_is_packed_as_a_write_code_step_with_its_budget_and_phone_alternative()
    {
        WritePack(RewriteStep);

        var bytes = ContentPacker.PackToBytes(Root, out var report, appManifestPath: AppManifest);
        Assert.True(report.IsValid, string.Join('\n', report.Errors));
        var step = Assert.IsType<WriteCodeStep>(ContentPackFormat.Load(new MemoryStream(bytes!)).Lessons.Single().Steps.Single());

        Assert.Equal("csharp", step.Language);
        Assert.Equal(0, step.Tests[0].MaxAllocatedBytes);
        Assert.Null(step.Tests[1].MaxAllocatedBytes);
        Assert.True(step.LargeScreenPreferred);
        Assert.IsType<FillBlankStep>(step.PhoneAlternative);
    }

    [Fact]
    public void A_rewrite_step_needs_a_test_with_an_allocation_budget()
    {
        WritePack(RewriteStep.Replace("        maxAllocatedBytes: 0\n", ""));

        Assert.Contains(Errors(), e => e.Contains("graded by its allocation budget"));
    }

    [Fact]
    public void A_rewrite_step_needs_a_phone_alternative_because_android_cannot_run_csharp()
    {
        var withoutAlternative = RewriteStep[..RewriteStep.IndexOf("    phoneAlternative:", StringComparison.Ordinal)];
        WritePack(withoutAlternative);

        Assert.Contains(Errors(), e => e.Contains("needs 'largeScreenPreferred: true' and a 'phoneAlternative'"));
    }

    [Fact]
    public void A_rewrite_whose_reference_solution_misses_the_budget_blocks_the_pack_with_the_measured_bytes()
    {
        WritePack(RewriteStep.Replace("for (var i = 1; i <= n; i++) total += i;\n", "for (var i = 1; i <= n; i++) total += new List<int> { i }.Sum();\n"));

        var error = Assert.Single(Errors());

        Assert.Contains("reference solution is wrong", error);
        Assert.Contains("per call; the budget is 0 (CoreCLR)", error);
    }

    [Fact]
    public void A_rewrite_step_refuses_another_language()
    {
        WritePack(RewriteStep.Replace("entrypoint: SumTo", "entrypoint: SumTo\n    language: python"));

        Assert.Contains(Errors(), e => e.Contains("'language' must be csharp"));
    }
}
