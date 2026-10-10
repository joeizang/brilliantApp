using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Python;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI.Tests;

public class TraceStepViewTests : ShortcutContext
{
    private static readonly TraceStep Step = new("step.trace", "Walking a list", "Watch **i** move.", "nums = [3, 1]\ni = 0\n",
        [new Visual("nums", Visual.Array, ["i"])]);

    private static TraceFrame Frame(int? line, int i) => new(line is null ? "return" : "line", line, "<module>",
        [new("nums", "list", "[3, 1]"), new("i", "int", i.ToString())],
        new Dictionary<string, TrackedArray> { ["nums"] = new("list", ["3", "1"], 0) });

    private static readonly TraceResult Ok = new(TraceStatus.Ok, [Frame(2, 0), Frame(null, 0)], "", null, null, null);

    private sealed class FakeRuntime(Func<TraceResult> trace) : IPythonRuntime
    {
        public List<(string Code, IReadOnlyList<Visual> Visuals)> Traced { get; } = [];
        public TaskCompletionSource? Gate { get; init; }

        public Task<TestRunResult> RunTestsAsync(string code, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken ct = default) =>
            throw new NotSupportedException("This test does not run tests.");

        public async Task<TraceResult> TraceAsync(string code, IReadOnlyList<Visual> visuals, CancellationToken ct = default)
        {
            Traced.Add((code, visuals));
            if (Gate is not null) await Gate.Task;
            return trace();
        }
    }

    private FakeRuntime Setup(Func<TraceResult> trace, TaskCompletionSource? gate = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/code-editor.js").Setup<int>("create", _ => true).SetResult(1);
        var runtime = new FakeRuntime(trace) { Gate = gate };
        Services.AddSingleton<IPythonRuntime>(runtime);
        return runtime;
    }

    [Fact]
    public void Traces_the_steps_code_with_its_visuals_and_shows_the_player()
    {
        var runtime = Setup(() => Ok);
        var cut = Render<TraceStepView>(p => p.Add(c => c.Step, Step));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".trace-player")));
        var traced = Assert.Single(runtime.Traced);
        Assert.Equal(Step.Code, traced.Code);
        Assert.Equal(Step.Visuals, traced.Visuals);
        Assert.Equal("Walking a list", cut.Find("h2").TextContent);
        Assert.Contains("<strong>i</strong>", cut.Find(".trace-body").InnerHtml);
    }

    [Fact]
    public void Says_that_python_is_starting_until_the_trace_arrives()
    {
        var gate = new TaskCompletionSource();
        Setup(() => Ok, gate);
        var cut = Render<TraceStepView>(p => p.Add(c => c.Step, Step));

        Assert.Contains("Starting Python", cut.Find(".note").TextContent);
        Assert.Empty(cut.FindAll(".trace-player"));

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".trace-player")));
        Assert.Empty(cut.FindAll(".note"));
    }

    [Fact]
    public void A_runtime_that_cannot_start_is_reported_as_a_problem_with_Python_not_the_example()
    {
        Setup(() => throw new InvalidOperationException("worker would not start"));
        var cut = Render<TraceStepView>(p => p.Add(c => c.Step, Step));

        cut.WaitForAssertion(() => Assert.Contains("worker would not start", cut.Find("p.trace-failure").TextContent));
        Assert.Equal("alert", cut.Find("p.trace-failure").GetAttribute("role"));
        Assert.Empty(cut.FindAll(".trace-player"));
    }

    [Fact]
    public void A_trace_with_no_steps_shows_its_message_instead_of_a_player()
    {
        Setup(() => new TraceResult(TraceStatus.Error, [], "", "SyntaxError: invalid syntax", "Traceback...", 1));
        var cut = Render<TraceStepView>(p => p.Add(c => c.Step, Step));

        cut.WaitForAssertion(() => Assert.Contains("SyntaxError", cut.Find(".trace-error").TextContent));
        Assert.Empty(cut.FindAll(".trace-player"));
    }

    [Fact]
    public void A_trace_that_failed_part_way_still_plays_the_steps_before_the_error()
    {
        Setup(() => new TraceResult(TraceStatus.Error, [Frame(2, 0)], "", "ValueError: nope", "Traceback...", 2));
        var cut = Render<TraceStepView>(p => p.Add(c => c.Step, Step));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".trace-player")));
        Assert.Contains("ValueError", cut.Find(".trace-error").TextContent);
    }

    // ---- in a lesson

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static readonly Lesson Lesson = new("lesson.t", "Trace lesson", "track.t",
        [Step, new ExplainStep("step.after", "After", "Done", [], null)]);

    private MemoryLog OpenLesson(out IRenderedComponent<LessonViewer> cut)
    {
        Setup(() => Ok);
        var log = new MemoryLog();
        Services.AddSingleton(new ProgressRecorder(log, "device"));
        cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, Lesson));
        return log;
    }

    [Fact]
    public void A_lesson_shows_a_trace_step_and_Continue_completes_it()
    {
        var log = OpenLesson(out var cut);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".trace-player")));
        cut.Find("button.continue").Click();

        Assert.Contains(log.Items, e => e.Type == ProgressEventTypes.StepCompleted && e.StepId == "step.trace");
        Assert.Contains("After", cut.Markup);
    }

    [Fact]
    public async Task Return_completes_a_trace_step()
    {
        var log = OpenLesson(out var cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".trace-player")));

        await Press(StepCommand.Advance);

        Assert.Contains(log.Items, e => e.Type == ProgressEventTypes.StepCompleted && e.StepId == "step.trace");
    }

    [Fact]
    public void A_trace_step_shows_no_hint_ladder()
    {
        OpenLesson(out var cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".trace-player")));

        Assert.Empty(cut.FindAll(".hint-ladder"));
    }
}
