using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

/// <summary>The <c>benchmark-predict</c> step as authors write it, and the recording a pack build attaches to it (#155).</summary>
[Collection(BenchmarkCollection.Name)]
public sealed class BenchmarkPredictStepTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-bench-pack-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_repository, "content", "sample");
    private string AppManifest => Path.Combine(_repository, "apps", "sample", "manifest.yaml");

    private const string Candidates = """
        setup: 'int[] data = Enumerable.Range(0, 1000).ToArray();'
        candidates:
          - id: loop
            label: for loop
            code: |
              var total = 0;
              for (var i = 0; i < data.Length; i++) total += data[i];
              return total;
          - id: linq
            label: LINQ chain
            code: return data.Where(x => x % 2 == 0).Select(x => x * 2).ToList().Count;
        """;

    private void WritePack(string step, string stepTypes = "[explain, benchmark-predict]", string packExtras = "targetFramework: net10.0\nlangVersion: '14'\n")
    {
        Write("apps/sample/manifest.yaml", $$"""
            appId: example.bench
            displayName: Bench
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [csharp]
            stepTypes: {{stepTypes}}
            """);
        Write("content/sample/pack.yaml", "id: pack.bench\nversion: 1.0.0\n" + packExtras);
        Write("content/sample/tracks/one/track.yaml", "id: track.bench\ntitle: Bench\n");
        Write("content/sample/tracks/one/sum/lesson.yaml", "id: lesson.bench\ntitle: Sum\nsteps:\n" + step);
    }

    /// <summary>The step's YAML. <paramref name="candidates"/> and <paramref name="extra"/> are written at column 0 here and indented under the step.</summary>
    private static string Step(string measure = "allocations", string candidates = Candidates, string extra = "") =>
        "  - id: step.bench.sum\n    type: benchmark-predict\n    title: Loop or LINQ\n    prompt: Rank them, fewest allocations first.\n" +
        $"    measure: {measure}\n" + Indent(candidates) + (extra.Length > 0 ? Indent(extra) : "");

    private static string Indent(string yaml) => string.Concat(yaml.Split('\n').Select(line => "    " + line + "\n"));

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
    public void A_valid_step_passes_validation_without_running_the_benchmark()
    {
        WritePack(Step());

        Assert.Empty(Errors());
    }

    [Fact]
    public void Validating_does_not_attach_a_recording_and_building_a_pack_does()
    {
        WritePack(Step());

        var report = ContentValidator.Load(Root, out var content, appManifestPath: AppManifest, recordBenchmarks: false);
        Assert.True(report.IsValid, string.Join('\n', report.Errors));
        Assert.Null(Assert.IsType<BenchmarkPredictStep>(content!.Lessons.Single().Steps.Single()).Recorded);

        var bytes = ContentPacker.PackToBytes(Root, out var packReport, appManifestPath: AppManifest);
        Assert.True(packReport.IsValid, string.Join('\n', packReport.Errors));
        var step = Assert.IsType<BenchmarkPredictStep>(ContentPackFormat.Load(new MemoryStream(bytes!)).Lessons.Single().Steps.Single());

        var recorded = Assert.IsType<BenchmarkRecording>(step.Recorded);
        Assert.Equal(["linq", "loop"], recorded.Rows.Select(r => r.CandidateId).Order(StringComparer.Ordinal));
        Assert.Equal(0, recorded.Rows.Single(r => r.CandidateId == "loop").AllocatedBytes);
        Assert.True(recorded.Rows.Single(r => r.CandidateId == "linq").AllocatedBytes > 0);
        Assert.True(recorded.Rows.All(r => r.MeanNs > 0));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Machine));
        Assert.StartsWith(".NET", recorded.Runtime);
        // The recording is what grades the learner: the loop allocates nothing, so it goes first.
        Assert.True(AnswerEvaluator.Evaluate(step, ["loop", "linq"]).IsCorrect);
        Assert.False(AnswerEvaluator.Evaluate(step, ["linq", "loop"]).IsCorrect);
    }

    [Fact]
    public void A_step_needs_two_to_five_candidates()
    {
        WritePack(Step(candidates: "candidates:\n  - {id: only, label: Only, code: 'return 1;'}"));

        Assert.Contains(Errors(), e => e.Contains("2 to 5 candidates"));
    }

    [Fact]
    public void Candidates_need_unique_slug_ids_labels_and_code()
    {
        WritePack(Step(candidates: """
            candidates:
              - {id: a, label: A, code: 'return 1;'}
              - {id: a, label: Again, code: 'return 2;'}
              - {id: 'Bad Id', label: B, code: 'return 3;'}
              - {id: c, code: 'return 4;'}
              - {id: d, label: D}
            """));

        var errors = Errors();
        Assert.Contains(errors, e => e.Contains("repeats the id 'a'"));
        Assert.Contains(errors, e => e.Contains("lowercase letters, digits and hyphens (found 'Bad Id')"));
        Assert.Contains(errors, e => e.Contains("candidates[3] needs a 'label'"));
        Assert.Contains(errors, e => e.Contains("candidates[4] needs 'code'"));
    }

    [Fact]
    public void An_unknown_measure_is_reported()
    {
        WritePack(Step(measure: "speed"));

        Assert.Contains(Errors(), e => e.Contains("'measure' must be time or allocations (found 'speed')"));
    }

    [Fact]
    public void An_authored_answer_is_refused_because_the_benchmark_decides_it()
    {
        WritePack(Step(extra: "accepted: [loop]"));

        Assert.Contains(Errors(), e => e.Contains("remove 'accepted', 'options' and 'mistakes'"));
    }

    [Fact]
    public void Another_language_is_refused()
    {
        WritePack(Step(extra: "language: python"));

        Assert.Contains(Errors(), e => e.Contains("'language' must be csharp"));
    }

    [Fact]
    public void A_candidate_that_does_not_compile_blocks_the_pack_with_its_own_line()
    {
        WritePack(Step(candidates: """
            candidates:
              - id: ok
                label: Fine
                code: return 1;
              - id: broken
                label: Broken
                code: |
                  var x = 1;
                  return "two";
            """));

        var error = Assert.Single(Errors());

        Assert.Contains("step.bench.sum", error);
        Assert.Contains("candidate 'broken', line 2", error);
        Assert.Contains("CS0029", error);
    }

    [Fact]
    public void Hints_are_allowed_and_a_walkthrough_is_not()
    {
        WritePack(Step(extra: "hints: [Count the allocations.]\nwalkthrough:\n  explanation: x\n  call: 'var a = 1;'\n  visualise: []"));

        var errors = Errors();
        Assert.DoesNotContain(errors, e => e.Contains("'hints'"));
        Assert.Contains(errors, e => e.Contains("walkthrough"));
    }

    [Fact]
    public void An_app_that_does_not_list_the_type_refuses_it()
    {
        WritePack(Step(), stepTypes: "[explain]");

        Assert.Contains(Errors(), e => e.Contains("step type 'benchmark-predict' is not enabled by app 'example.bench'"));
    }

    [Fact]
    public void The_pack_must_pin_the_csharp_target()
    {
        WritePack(Step(), packExtras: "");

        Assert.Contains(Errors(), e => e.Contains("require targetFramework"));
    }

    [Fact]
    public void Benchmark_fields_on_another_step_type_are_refused()
    {
        WritePack("  - id: step.bench.note\n    type: explain\n    title: Note\n    body: Hello.\n    measure: time\n");

        Assert.Contains(Errors(), e => e.Contains("'measure', 'setup' and 'candidates' only apply to benchmark-predict steps"));
    }
}
