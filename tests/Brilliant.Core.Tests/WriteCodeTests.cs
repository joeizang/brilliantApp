using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Python;

namespace Brilliant.Core.Tests;

public class WriteCodeTests
{
    private static readonly WriteCodeStep Step = new("step.w", "W", "Write add", "python", "def add(a, b):\n    pass\n", "add",
        [new CodeTest("1, 2", "3"), new CodeTest("0, 0", "0")]);

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    [Fact]
    public void Write_code_step_survives_a_pack_round_trip()
    {
        var lesson = new Lesson("lesson.one", "One", "track.a", [Step with { Hints = ["nudge"] }]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "A", ["lesson.one"])], [lesson]);
        ms.Position = 0;

        var loaded = Assert.IsType<WriteCodeStep>(ContentPackFormat.Load(ms).Get<Lesson>("lesson.one").Steps[0]);

        Assert.Equal(("python", "add", Step.Starter), (loaded.Language, loaded.Entrypoint, loaded.Starter));
        Assert.Equal(Step.Tests, loaded.Tests);
        Assert.Equal(["nudge"], loaded.Hints);
    }

    [Fact]
    public void Code_submissions_are_recorded_with_the_code_and_test_counts()
    {
        var log = new MemoryLog();
        var recorder = new ProgressRecorder(log, "device");

        recorder.CodeSubmitted("lesson.one", "step.w", "def add(a, b): return a - b", passedTests: 1, totalTests: 2);
        recorder.CodeSubmitted("lesson.one", "step.w", "def add(a, b): return a + b", passedTests: 2, totalTests: 2);

        Assert.All(log.Items, e => Assert.Equal(ProgressEventTypes.CodeSubmitted, e.Type));
        using var first = JsonDocument.Parse(log.Items[0].Data!);
        Assert.Equal("def add(a, b): return a - b", first.RootElement.GetProperty("code").GetString());
        Assert.False(first.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal((1, 2), (first.RootElement.GetProperty("passedTests").GetInt32(), first.RootElement.GetProperty("totalTests").GetInt32()));
        using var second = JsonDocument.Parse(log.Items[1].Data!);
        Assert.True(second.RootElement.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void A_submission_with_no_tests_is_never_recorded_as_passed()
    {
        var log = new MemoryLog();
        new ProgressRecorder(log, "d").CodeSubmitted("lesson.one", "step.w", "x", 0, 0);
        using var doc = JsonDocument.Parse(log.Items.Single().Data!);
        Assert.False(doc.RootElement.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void Submitting_code_does_not_complete_the_step()
    {
        var lesson = new Lesson("lesson.one", "One", "track.a", [Step]);
        var log = new MemoryLog();
        var recorder = new ProgressRecorder(log, "d");

        recorder.CodeSubmitted(lesson.Id, Step.Id, "def add(a, b): return a + b", 2, 2);
        Assert.Equal(0, LessonProgress.From(lesson, log.Items).CompletedCount);

        recorder.CompleteStep(lesson, Step.Id);
        Assert.True(LessonProgress.From(lesson, log.Items).IsComplete);
    }

    [Fact]
    public void Run_result_helpers_find_the_first_failure()
    {
        var result = new TestRunResult(TestRunStatus.Failed, "", null, null, null,
        [
            new TestOutcome("add(1, 2)", "3", "3", true, "", null, null),
            new TestOutcome("add(2, 2)", "4", "5", false, "", null, null),
            new TestOutcome("add(3, 3)", "6", "7", false, "", null, null),
        ]);

        Assert.Equal(1, result.PassedCount);
        Assert.Equal("add(2, 2)", result.FirstFailure!.Call);
    }

    [Fact]
    public void Outputs_cover_file_loading_and_every_test_call_whatever_the_verdict()
    {
        var passing = new TestRunResult(TestRunStatus.Passed, "", null, null, null,
            [new TestOutcome("f(7)", "7", "7", true, "debug 7\n", null, null)]);
        Assert.Equal([new OutputSection("Printed during f(7)", "debug 7\n")], passing.Outputs);

        var failing = new TestRunResult(TestRunStatus.Failed, "loading\n", null, null, null,
        [
            new TestOutcome("f(1)", "1", "1", true, "one\n", null, null),
            new TestOutcome("f(2)", "2", "3", false, "", null, null),
            new TestOutcome("f(3)", "3", "4", false, "three\n", null, null),
        ]);
        Assert.Equal(["Output when your file loaded", "Printed during f(1)", "Printed during f(3)"], failing.Outputs.Select(o => o.Label));

        var error = new TestRunResult(TestRunStatus.Error, "partial\n", "m", "tb", 1, []);
        Assert.Equal("Output before the error", error.Outputs.Single().Label);
        Assert.Empty(new TestRunResult(TestRunStatus.Passed, "", null, null, null, []).Outputs);
    }
}
