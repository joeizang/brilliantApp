using Brilliant.Cli;
using Brilliant.Core.Content;
using Brilliant.Core.Python;

namespace Brilliant.Cli.Tests;

public sealed class TraceStepValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brilliant-trace-tests-" + Guid.NewGuid().ToString("N"));

    public TraceStepValidatorTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private const string Header = "id: lesson.one\ntitle: One\n";
    private const string Head = "steps:\n  - id: step.one.t\n    type: trace\n    title: Walk\n";
    private const string Body = "    body: Watch i.\n";
    private const string Code = "    code: |\n      nums = [3, 8, 2]\n      for i in range(3):\n          pass\n";
    private const string Visualise = "    visualise:\n      - variable: nums\n        as: array\n        pointers: [i]\n";

    private void WriteLesson(string yaml)
    {
        Write("pack.yaml", "id: pack.t\nversion: 1.0.0\n");
        Write("tracks/t/track.yaml", "id: track.t\ntitle: T\n");
        Write("tracks/t/l1/lesson.yaml", yaml);
    }

    private List<string> Errors(string stepYaml, IReferenceSolutionRunner? runner = null)
    {
        WriteLesson(Header + stepYaml);
        return ContentValidator.Validate(_root, runner ?? new FakeRunner(Good)).Errors.Select(e => e.Message).ToList();
    }

    // ---- Fake tracer ----

    private static TraceFrame Frame(int? line, params (string Name, string Type, string Repr)[] locals) =>
        new(line is null ? "return" : "line", line, "<module>", locals.Select(l => new TraceLocal(l.Name, l.Type, l.Repr)).ToList(),
            new Dictionary<string, TrackedArray> { ["nums"] = new("list", ["3", "8", "2"], 0) });

    private static TraceResult Good(string code, IReadOnlyList<Visual> visuals) =>
        new(TraceStatus.Ok, [Frame(1), Frame(2, ("i", "int", "0")), Frame(null, ("i", "int", "2"))], "", null, null, null);

    private sealed class FakeRunner(Func<string, IReadOnlyList<Visual>, TraceResult> trace) : IReferenceSolutionRunner
    {
        public List<(string Code, IReadOnlyList<Visual> Visuals)> Traced { get; } = [];
        public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests) => throw new NotSupportedException();
        public TraceResult Trace(string code, IReadOnlyList<Visual> visuals) { Traced.Add((code, visuals)); return trace(code, visuals); }
    }

    // ---- Shape ----

    [Fact]
    public void A_trace_step_loads_into_the_pack_with_its_code_and_visuals()
    {
        WriteLesson(Header + Head + Body + Code + Visualise);
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath, new FakeRunner(Good));

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var step = Assert.IsType<TraceStep>(ContentPackFormat.Load(outPath).Get<Lesson>("lesson.one").Steps[0]);
        Assert.Equal("Watch i.", step.Body.Trim());
        Assert.StartsWith("nums = [3, 8, 2]", step.Code);
        var visual = Assert.Single(step.Visuals);
        Assert.Equal(("nums", "array"), (visual.Variable, visual.As));
        Assert.Equal(["i"], visual.Pointers);
    }

    [Fact]
    public void Pointers_are_optional()
    {
        Assert.Empty(Errors(Head + Body + Code + "    visualise:\n      - variable: nums\n        as: array\n"));
    }

    [Fact]
    public void Body_code_and_visualise_are_required()
    {
        var m = Errors(Head);

        Assert.Contains(m, x => x.Contains("'body' is required"));
        Assert.Contains(m, x => x.Contains("'code' is required"));
        Assert.Contains(m, x => x.Contains("'visualise'") && x.Contains("at least one"));
    }

    [Fact]
    public void An_empty_visualise_list_is_the_same_as_none()
    {
        Assert.Contains(Errors(Head + Body + Code + "    visualise: []\n"), x => x.Contains("at least one"));
    }

    [Fact]
    public void Code_is_required_even_when_the_rest_is_there()
    {
        Assert.Contains(Errors(Head + Body + Visualise), x => x.Contains("'code' is required"));
    }

    [Fact]
    public void A_visual_needs_a_variable_and_a_kind()
    {
        var m = Errors(Head + Body + Code + "    visualise:\n      - pointers: [i]\n");

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains("'variable' is required"));
        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains("'as' is required"));
    }

    [Fact]
    public void Only_arrays_can_be_drawn_for_now()
    {
        var m = Errors(Head + Body + Code + "    visualise:\n      - variable: nums\n        as: tree\n");

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains("unsupported") && x.Contains("'tree'") && x.Contains("array"));
    }

    [Theory]
    [InlineData("variable: 2nums", "variable")]
    [InlineData("variable: nums\n        pointers: [i, 'j k']", "pointer")]
    public void Names_must_be_python_identifiers(string fields, string what)
    {
        var m = Errors(Head + Body + Code + $"    visualise:\n      - as: array\n        {fields}\n");

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains(what) && x.Contains("not a valid Python"));
    }

    [Fact]
    public void A_variable_is_drawn_once_and_a_pointer_named_once()
    {
        var m = Errors(Head + Body + Code
            + "    visualise:\n      - variable: nums\n        as: array\n        pointers: [i, i]\n      - variable: nums\n        as: array\n");

        Assert.Contains(m, x => x.Contains("visualise[0]") && x.Contains("pointer 'i'") && x.Contains("twice"));
        Assert.Contains(m, x => x.Contains("visualise[1]") && x.Contains("'nums'") && x.Contains("already"));
    }

    [Fact]
    public void Trace_steps_take_no_hints()
    {
        var m = Errors(Head + Body + Code + Visualise + "    hints:\n      - a\n");

        Assert.Contains(m, x => x.Contains("'hints' only apply to questions"));
    }

    [Fact]
    public void Unknown_fields_are_rejected()
    {
        Assert.NotEmpty(Errors(Head + Body + Code + Visualise + "    bogus: 1\n"));
    }

    [Fact]
    public void A_trace_step_cannot_back_a_review_item()
    {
        var m = Errors("concepts:\n  - id: concept.loops\n    title: Loops\nreviewItems:\n  - id: review.one.t\n    concept: concept.loops\n    step: step.one.t\n"
            + Head + Body + Code + Visualise);

        Assert.Contains(m, x => x.Contains("review item needs a question") && x.Contains("trace"));
    }

    [Fact]
    public void The_unsupported_type_message_lists_trace()
    {
        var m = Errors("steps:\n  - id: step.one.t\n    type: video\n    title: V\n");

        Assert.Contains(m, x => x.Contains("unsupported step type 'video'") && x.Contains("trace"));
    }

    // ---- The code is really run ----

    [Fact]
    public void The_authored_code_is_traced_with_its_visuals_before_packing()
    {
        var runner = new FakeRunner(Good);

        Assert.Empty(Errors(Head + Body + Code + Visualise, runner));

        var traced = Assert.Single(runner.Traced);
        Assert.StartsWith("nums = [3, 8, 2]", traced.Code);
        Assert.Equal(["nums"], traced.Visuals.Select(v => v.Variable));
    }

    [Fact]
    public void Code_that_raises_is_reported_with_its_traceback()
    {
        var runner = new FakeRunner((_, _) => new(TraceStatus.Error, [Frame(1)], "", "The code raised an error.", "ZeroDivisionError: division by zero", 3));

        var m = Errors(Head + Body + Code + Visualise, runner);

        var message = Assert.Single(m);
        Assert.Contains("step.one.t", message);
        Assert.Contains("line 3", message);
        Assert.Contains("ZeroDivisionError", message);
    }

    [Fact]
    public void Code_that_runs_too_long_is_reported()
    {
        var runner = new FakeRunner((_, _) => new(TraceStatus.Truncated, [Frame(1)], "", "More than 300 steps.", null, null));

        var m = Errors(Head + Body + Code + Visualise, runner);

        Assert.Contains(m, x => x.Contains("too long") && x.Contains("More than 300 steps."));
    }

    [Fact]
    public void A_visualised_variable_that_is_never_a_list_is_reported()
    {
        var untracked = new TraceFrame("line", 1, "<module>", [], new Dictionary<string, TrackedArray>());
        var runner = new FakeRunner((_, _) => new(TraceStatus.Ok, [untracked], "", null, null, null));

        var m = Errors(Head + Body + Code + Visualise, runner);

        Assert.Contains(m, x => x.Contains("'nums'") && x.Contains("never a list"));
    }

    [Fact]
    public void A_pointer_that_is_never_an_int_is_reported()
    {
        var runner = new FakeRunner((_, _) => new(TraceStatus.Ok, [Frame(1, ("i", "str", "'a'")), Frame(null, ("i", "str", "'b'"))], "", null, null, null));

        var m = Errors(Head + Body + Code + Visualise, runner);

        Assert.Contains(m, x => x.Contains("pointer 'i'") && x.Contains("never an int"));
    }

    [Fact]
    public void A_trace_with_no_steps_is_reported()
    {
        var runner = new FakeRunner((_, _) => new(TraceStatus.Ok, [], "", null, null, null));

        Assert.Contains(Errors(Head + Body + Code + Visualise, runner), x => x.Contains("no steps"));
    }

    [Fact]
    public void Nothing_is_traced_when_the_content_is_otherwise_invalid()
    {
        var runner = new FakeRunner(Good);

        Assert.NotEmpty(Errors(Head + Visualise, runner));    // no body, no code
        Assert.Empty(runner.Traced);
    }

    [Fact]
    public void A_missing_python_is_reported_once_even_with_exercises_and_trace_steps_together()
    {
        var exercise = "  - id: step.one.w\n    type: write-code\n    title: W\n    prompt: Write it\n    language: python\n    entrypoint: add\n"
            + "    solution: |\n      def add(a, b):\n          return a + b\n    tests:\n      - input: '1, 2'\n        expected: '3'\n";
        WriteLesson(Header + Head + Body + Code + Visualise + exercise);

        var error = Assert.Single(ContentValidator.Validate(_root, new CPythonRunner(python: "definitely-not-a-python")).Errors);

        Assert.Contains("BRILLIANT_PYTHON", error.Message);
    }

    [Fact]
    public void A_missing_python_is_reported_once_with_how_to_fix_it()
    {
        WriteLesson(Header + Head + Body + Code + Visualise);

        var error = Assert.Single(ContentValidator.Validate(_root, new CPythonRunner(python: "definitely-not-a-python")).Errors);

        Assert.Contains("BRILLIANT_PYTHON", error.Message);
    }

    // ---- Real CPython, with the very tracer.py the app runs ----

    [Fact]
    public void Real_cpython_traces_the_sample()
    {
        Assert.Empty(Errors(Head + Body + Code + Visualise, new CPythonRunner()));
    }

    [Fact]
    public void Real_cpython_reports_code_that_raises()
    {
        var m = Errors(Head + Body + "    code: |\n      nums = [1]\n      nums[5]\n" + Visualise, new CPythonRunner());

        Assert.Contains(m, x => x.Contains("IndexError") && x.Contains("line 2"));
    }

    [Fact]
    public void Real_cpython_stops_code_that_never_ends()
    {
        var m = Errors(Head + Body + "    code: |\n      nums = [1]\n      while True:\n          pass\n" + Visualise, new CPythonRunner());

        Assert.Contains(m, x => x.Contains("too long"));
    }

    [Fact]
    public void Real_cpython_reports_a_visualised_name_the_code_never_makes()
    {
        var m = Errors(Head + Body + "    code: |\n      xs = [1]\n" + Visualise, new CPythonRunner());

        Assert.Contains(m, x => x.Contains("'nums'") && x.Contains("never a list"));
    }
}
