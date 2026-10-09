using Brilliant.Core.Content;
using Brilliant.Core.Python;
using Brilliant.Lessons.UI;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

public class WriteCodeStepViewTests : BunitContext
{
    private static readonly WriteCodeStep Step = new("step.w", "Echo", "Write f", "python", "def f(x):\n    pass\n", "f",
        [new CodeTest("7", "7")]);

    private sealed class FakeRuntime(TestRunResult result) : IPythonRuntime
    {
        public Task<TestRunResult> RunTestsAsync(string code, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    // Renders the step with a stubbed editor module and runtime, then clicks "Run tests".
    private IRenderedComponent<WriteCodeStepView> Run(TestRunResult result)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/code-editor.js");
        module.Setup<int>("create", _ => true).SetResult(1);
        module.Setup<string>("getCode", _ => true).SetResult("def f(x):\n    return x\n");
        Services.AddSingleton<IPythonRuntime>(new FakeRuntime(result));

        var cut = Render<WriteCodeStepView>(p => p.Add(c => c.Step, Step));
        cut.Find("button.primary").Click();
        cut.WaitForState(() => cut.FindAll(".verdict").Count > 0);
        return cut;
    }

    [Fact]
    public void A_passing_submission_still_shows_what_its_test_calls_printed()
    {
        var cut = Run(new TestRunResult(TestRunStatus.Passed, "", null, null, null,
            [new TestOutcome("f(7)", "7", "7", true, "debug 7\n", null, null)]));

        Assert.Contains("All 1 tests passed", cut.Find(".verdict").TextContent);
        Assert.Contains("Printed during f(7)", cut.Markup);
        Assert.Contains("debug 7", cut.Find(".output pre").TextContent);
    }

    [Fact]
    public void A_failing_submission_shows_load_time_output_and_output_from_every_test()
    {
        var cut = Run(new TestRunResult(TestRunStatus.Failed, "loading\n", null, null, null,
        [
            new TestOutcome("f(1)", "1", "1", true, "one\n", null, null),
            new TestOutcome("f(2)", "2", "3", false, "two\n", null, null),
        ]));

        var outputs = cut.FindAll(".output pre").Select(e => e.TextContent).ToList();
        Assert.Equal(["loading\n", "one\n", "two\n"], outputs);
        Assert.Contains("First failing test", cut.Markup);
    }

    [Fact]
    public void An_error_before_the_tests_shows_the_output_printed_so_far()
    {
        var cut = Run(new TestRunResult(TestRunStatus.Error, "partial\n", "Your code raised an error before the tests could run.",
            "ZeroDivisionError: division by zero", 2, []));

        Assert.Contains("Output before the error", cut.Markup);
        Assert.Contains("partial", cut.Find(".output pre").TextContent);
    }

    [Fact]
    public void Silent_code_shows_no_output_section()
    {
        var cut = Run(new TestRunResult(TestRunStatus.Passed, "", null, null, null,
            [new TestOutcome("f(7)", "7", "7", true, "", null, null)]));

        Assert.Empty(cut.FindAll(".output"));
    }
}
