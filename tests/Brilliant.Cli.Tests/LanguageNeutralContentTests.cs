using Brilliant.Core.Code;
using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

public sealed class LanguageNeutralContentTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-languages-" + Guid.NewGuid());
    private string Root => Path.Combine(_repository, "content", "sample");

    private void WriteLesson(string steps, string runtimes = "python, csharp")
    {
        var manifest = Path.Combine(_repository, "apps", "sample", "manifest.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, $$"""
            appId: example.sample
            displayName: Sample
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [{{runtimes}}]
            stepTypes: [explain, write-code, trace, predict-output, fill-blank, parsons]
            """);
        Directory.CreateDirectory(Path.Combine(Root, "tracks", "t", "l"));
        File.WriteAllText(Path.Combine(Root, "pack.yaml"), "id: pack.sample\nversion: 1.0.0\n");
        File.WriteAllText(Path.Combine(Root, "tracks", "t", "track.yaml"), "id: track.sample\ntitle: Sample\n");
        File.WriteAllText(Path.Combine(Root, "tracks", "t", "l", "lesson.yaml"),
            "id: lesson.sample\ntitle: Sample\nsteps:\n" + steps + "\n");
    }

    private const string WriteCode = """
          - id: step.sample.write
            type: write-code
            title: Double
            prompt: Double it
            language: csharp
            entrypoint: Double
            starter: 'static int Double(int n) => 0;'
            solution: 'static int Double(int n) => n * 2;'
            tests:
              - input: '4'
                expected: '8'
        """;

    private const string Trace = """
          - id: step.sample.trace
            type: trace
            title: Trace
            body: Watch nums
            language: csharp
            code: 'int[] nums = [1, 2];'
            visualise:
              - variable: nums
                as: array
        """;

    private const string Walkthrough = "\n    walkthrough:\n      language: csharp\n      call: Double(4);\n      explanation: Doubles the argument.\n";

    [Fact]
    public void Csharp_tests_keep_their_source_and_use_only_a_matching_checker()
    {
        var code = WriteCode.Replace("'4'", "'new[] { 1, 2 }'").Replace("'8'", "'new[] { 2, 4 }'");
        WriteLesson(code);
        var runner = new Runner("csharp");
        var report = ContentValidator.Load(Root, out var content, runner);
        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<WriteCodeStep>(content!.Lessons[0].Steps[0]);
        Assert.Equal("csharp", step.Language);
        Assert.Equal("new[] { 1, 2 }", Assert.Single(step.Tests).Input);
        Assert.Equal("new[] { 2, 4 }", step.Tests[0].Expected);
        Assert.Equal(step.Tests, runner.Tests);
        Assert.Equal("static int Double(int n) => n * 2;", runner.Code);
    }

    [Fact]
    public void Python_linked_list_comparison_is_not_reused_for_csharp_tests()
    {
        WriteLesson(WriteCode + "\n    compare: linked_list\n");
        var runner = new Runner("csharp");
        var report = ContentValidator.Validate(Root, runner);
        Assert.Contains(report.Errors, e => e.Message.Contains("'compare' is a Python harness option"));
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Non_python_code_never_reaches_a_python_checker(bool trace)
    {
        WriteLesson(trace ? Trace : WriteCode + Walkthrough);
        var runner = new Runner("python");
        var output = Path.Combine(_repository, "pack.zip");
        var report = ContentPacker.Pack(Root, output, runner);
        Assert.Contains(report.Errors, e => e.Message.Contains("no reference/trace checker is available for 'csharp'"));
        Assert.Equal(0, runner.Calls);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("python")]
    [InlineData("csharp")]
    public void Trace_and_walkthrough_language_survives_pack_round_trip(string language)
    {
        WriteLesson((WriteCode + Walkthrough + Trace).Replace("csharp", language));
        var output = Path.Combine(_repository, "pack.zip");
        var report = ContentPacker.Pack(Root, output, new Runner(language));
        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var graph = ContentPackFormat.Load(output);
        Assert.Equal(language, graph.Get<Step>("step.sample.write").Walkthrough!.Language);
        Assert.Equal(language, Assert.IsType<TraceStep>(graph.Get<Step>("step.sample.trace")).Language);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manifest_rejects_disabled_executable_languages_before_running(bool trace)
    {
        WriteLesson(trace ? Trace : WriteCode + Walkthrough, "python");
        var runner = new Runner("csharp");
        var report = ContentValidator.Validate(Root, runner);
        Assert.Contains(report.Errors, e => e.Message.Contains("runtime 'csharp' is not enabled by app 'example.sample'"));
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("predict-output", "    code: 'Double(4);'\n    accepted: ['8']\n")]
    [InlineData("fill-blank", "    template: 'var n = {{value}};'\n    blanks:\n      - id: value\n        accepted: ['8']\n")]
    [InlineData("parsons", "    solution: |\n      var n = 4;\n      n *= 2;\n")]
    public void Static_code_needs_no_runtime_until_a_walkthrough_is_added(string type, string body)
    {
        var step = $"  - id: step.sample.static\n    type: {type}\n    title: Static\n    prompt: P\n    language: csharp\n" + body;
        WriteLesson(step, "");
        Assert.True(ContentValidator.Validate(Root).IsValid);

        WriteLesson(step + Walkthrough, "");
        var runner = new Runner("csharp");
        var report = ContentValidator.Validate(Root, runner);
        Assert.Contains(report.Errors, e => e.Message.Contains("walkthrough: runtime 'csharp' is not enabled"));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void A_walkthrough_cannot_change_its_steps_language()
    {
        WriteLesson(WriteCode + Walkthrough.Replace("language: csharp", "language: python"));
        var runner = new Runner("csharp");
        var report = ContentValidator.Validate(Root, runner);
        Assert.Contains(report.Errors, e => e.Message.Contains("must match the step's language 'csharp'"));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Legacy_python_trace_defaults_to_python_and_stays_loadable()
    {
        WriteLesson(Trace.Replace("    language: csharp\n", ""), "python");
        var report = ContentValidator.Load(Root, out var content, new Runner("python"));
        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        Assert.Null(Assert.IsType<TraceStep>(content!.Lessons[0].Steps[0]).Language);
    }

    [Fact]
    public void Csharp_Parsons_braces_do_not_use_python_colon_indentation_rules()
    {
        WriteLesson("""
              - id: step.sample.parsons
                type: parsons
                title: Order
                prompt: Order the lines
                language: csharp
                solution: |
                  if (ready)
                  {
                      Run();
                  }
            """);
        var report = ContentValidator.Load(Root, out var content);
        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<ParsonsStep>(content!.Lessons[0].Steps[0]);
        Assert.Equal("csharp", step.Language);
        Assert.Equal([0, 0, 1, 0], step.Lines.Select(l => l.Level));
    }

    private sealed class Runner(string language) : IReferenceSolutionRunner
    {
        public string Language => language;
        public int Calls { get; private set; }
        public string? Code { get; private set; }
        public IReadOnlyList<CodeTest>? Tests { get; private set; }
        public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests)
        {
            Calls++;
            Code = code;
            Tests = tests;
            return new(TestRunStatus.Passed, "", null, null, null, []);
        }
        public TraceResult Trace(string code, IReadOnlyList<Visual> visuals)
        {
            Calls++;
            return new(TraceStatus.Ok, [new("line", 1, "<module>", [],
                new Dictionary<string, TrackedArray> { ["nums"] = new("int[]", ["1", "2"], 0, [Visual.Array]) })], "", null, null, null);
        }
    }

    public void Dispose() => Directory.Delete(_repository, recursive: true);
}
