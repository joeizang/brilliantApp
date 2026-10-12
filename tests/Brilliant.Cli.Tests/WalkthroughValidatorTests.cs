using Brilliant.Cli;
using Brilliant.Core.Content;
using Brilliant.Core.Python;

namespace Brilliant.Cli.Tests;

/// <summary>The full-solution walkthrough (the hint ladder's last rung): how it is authored, what code it runs, and what the validator refuses.</summary>
public sealed class WalkthroughValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brilliant-walkthrough-tests-" + Guid.NewGuid().ToString("N"));

    public WalkthroughValidatorTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Explanation = "    walkthrough:\n      call: result = double(4)\n      explanation: |\n        It **doubles**.\n";

    private const string WriteCode = """
          - id: step.one.w
            type: write-code
            title: Write it
            prompt: Double it.
            language: python
            entrypoint: double
            starter: |
              def double(n):
                  pass
            solution: |
              def double(n):
                  return n * 2
            tests:
              - input: "4"
                expected: "8"

        """;

    private const string Parsons = """
          - id: step.one.p
            type: parsons
            title: Order it
            prompt: Put the lines in order.
            solution: |
              total = 0
              for i in range(3):
                  total += i
        """ + "\n";

    private const string FillBlank = """
          - id: step.one.f
            type: fill-blank
            title: Fill it
            prompt: Fill the blank.
            template: |
              total = 0
              for i in range({{count}}):
                  total += i
            blanks:
              - id: count
                accepted: ["3", "three"]
        """ + "\n";

    private const string Predict = """
          - id: step.one.o
            type: predict-output
            title: Predict it
            prompt: What does it print?
            code: |
              print(1 + 1)
            accepted: ["2"]
        """ + "\n";

    private void WriteLesson(string steps)
    {
        File.WriteAllText(Path.Combine(_root, "pack.yaml"), "id: pack.t\nversion: 1.0.0\n");
        Directory.CreateDirectory(Path.Combine(_root, "tracks", "t", "l1"));
        File.WriteAllText(Path.Combine(_root, "tracks", "t", "track.yaml"), "id: track.t\ntitle: T\n");
        File.WriteAllText(Path.Combine(_root, "tracks", "t", "l1", "lesson.yaml"), "id: lesson.one\ntitle: One\nsteps:\n" + steps);
    }

    private sealed class Runner(Func<string, TraceResult>? trace = null) : IReferenceSolutionRunner
    {
        public List<(string Code, IReadOnlyList<Visual> Visuals)> Traced { get; } = [];

        public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests) =>
            new(TestRunStatus.Passed, "", null, null, null, tests.Select(t => new TestOutcome(t.Input, t.Expected, t.Expected, true, "", null, null)).ToList());

        public TraceResult Trace(string code, IReadOnlyList<Visual> visuals)
        {
            Traced.Add((code, visuals));
            return trace?.Invoke(code) ?? Ok;
        }

        private static readonly TraceResult Ok = new(TraceStatus.Ok,
            [new TraceFrame("line", 1, "<module>", [], new Dictionary<string, TrackedArray>()), new TraceFrame("return", null, "<module>", [], new Dictionary<string, TrackedArray>())],
            "", null, null, null);
    }

    private (List<string> Errors, Runner Runner) Check(string steps, Runner? runner = null)
    {
        WriteLesson(steps);
        runner ??= new Runner();
        return (ContentValidator.Validate(_root, runner).Errors.Select(e => e.Message).ToList(), runner);
    }

    private Step Packed(string steps)
    {
        WriteLesson(steps);
        var outPath = Path.Combine(_root, "out", "p.zip");
        var report = ContentPacker.Pack(_root, outPath, new Runner());
        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        return ContentPackFormat.Load(outPath).Get<Lesson>("lesson.one").Steps[0];
    }

    [Fact]
    public void A_write_code_walkthrough_runs_the_solution_followed_by_the_call_and_survives_packing()
    {
        var step = Packed(WriteCode + Explanation);

        var walkthrough = Assert.IsType<Walkthrough>(step.Walkthrough);
        Assert.Equal("def double(n):\n    return n * 2\n\nresult = double(4)\n", walkthrough.Code);
        Assert.Contains("**doubles**", walkthrough.Explanation);
        Assert.Empty(walkthrough.Visuals);
    }

    [Fact]
    public void A_step_with_no_walkthrough_has_none()
    {
        Assert.Null(Packed(WriteCode).Walkthrough);
    }

    [Fact]
    public void A_Parsons_walkthrough_runs_the_lines_in_their_solution_order_with_their_indentation()
    {
        var step = Packed(Parsons + "    walkthrough:\n      explanation: Adds up 0, 1 and 2.\n");

        Assert.Equal("total = 0\nfor i in range(3):\n    total += i\n", step.Walkthrough!.Code);
    }

    [Fact]
    public void A_fill_blank_walkthrough_runs_the_template_with_the_first_accepted_answers()
    {
        var step = Packed(FillBlank + "    walkthrough:\n      explanation: Counts to three.\n");

        Assert.Equal("total = 0\nfor i in range(3):\n    total += i\n", step.Walkthrough!.Code);
    }

    [Fact]
    public void A_predict_output_walkthrough_runs_the_steps_own_code()
    {
        var step = Packed(Predict + "    walkthrough:\n      explanation: One plus one.\n");

        Assert.Equal("print(1 + 1)\n", step.Walkthrough!.Code);
    }

    [Fact]
    public void The_walkthrough_is_run_once_so_it_cannot_ship_broken_and_draws_its_visuals()
    {
        var (errors, runner) = Check(WriteCode + Explanation + "      visualise:\n        - variable: nums\n          as: array\n");

        var traced = Assert.Single(runner.Traced);
        Assert.Contains("result = double(4)", traced.Code);
        Assert.Equal("nums", Assert.Single(traced.Visuals).Variable);
        Assert.Contains(errors, e => e.Contains("walkthrough") && e.Contains("'nums' is never a list, tuple or deque"));
    }

    [Fact]
    public void A_walkthrough_whose_code_raises_is_reported_with_the_step()
    {
        var failing = new Runner(_ => new TraceResult(TraceStatus.Error, [], "", "ZeroDivisionError: division by zero", "Traceback…", 2));

        var (errors, _) = Check(WriteCode + Explanation, failing);

        Assert.Contains(errors, e => e.Contains("step.one.w") && e.Contains("walkthrough") && e.Contains("could not be traced") && e.Contains("line 2"));
    }

    [Fact]
    public void The_explanation_is_required()
    {
        var (errors, _) = Check(WriteCode + "    walkthrough:\n      call: result = double(4)\n");

        Assert.Contains(errors, e => e.Contains("walkthrough") && e.Contains("'explanation' is required"));
    }

    [Fact]
    public void A_write_code_walkthrough_needs_a_call_or_there_is_nothing_to_watch()
    {
        var (errors, _) = Check(WriteCode + "    walkthrough:\n      explanation: It doubles.\n");

        Assert.Contains(errors, e => e.Contains("walkthrough") && e.Contains("needs a 'call'") && e.Contains("double("));
    }

    [Fact]
    public void The_walkthrough_is_the_fourth_rung_so_a_step_with_one_has_at_most_three_hints()
    {
        var hints = "    hints:\n      - one\n      - two\n      - three\n      - four\n";

        var (errors, _) = Check(WriteCode + hints + Explanation);
        var (fine, _) = Check(WriteCode + hints.Replace("      - four\n", "") + Explanation);

        Assert.Contains(errors, e => e.Contains("at most 3 hints") && e.Contains("walkthrough"));
        Assert.Empty(fine);
    }

    [Fact]
    public void Without_a_walkthrough_four_hints_are_still_fine()
    {
        var (errors, _) = Check(WriteCode + "    hints:\n      - one\n      - two\n      - three\n      - four\n");

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("explain")]
    [InlineData("trace")]
    public void Steps_with_no_solution_to_run_refuse_a_walkthrough(string type)
    {
        var step = type switch
        {
            "choice" => "  - id: step.one.c\n    type: choice\n    title: Pick\n    prompt: Which?\n    options:\n      - text: A\n        correct: true\n      - text: B\n",
            "explain" => "  - id: step.one.e\n    type: explain\n    title: Read\n    body: Text.\n",
            _ => "  - id: step.one.t\n    type: trace\n    title: Walk\n    body: Watch.\n    code: |\n      nums = [1]\n    visualise:\n      - variable: nums\n        as: array\n",
        };

        var (errors, _) = Check(step + Explanation);

        Assert.Contains(errors, e => e.Contains("'walkthrough' only applies to"));
    }

    [Fact]
    public void A_walkthroughs_visuals_are_checked_like_a_trace_steps()
    {
        var (errors, _) = Check(WriteCode + Explanation + "      visualise:\n        - variable: nums\n          as: tree\n");

        Assert.Contains(errors, e => e.Contains("walkthrough: visualise[0]") && e.Contains("unsupported 'as' value 'tree'"));
    }
}
