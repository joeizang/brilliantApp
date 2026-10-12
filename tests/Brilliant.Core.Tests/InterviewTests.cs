using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Practice;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>Timed interview runs (#36, slice 4): choosing the set, InterviewStarted / InterviewFinished, the clock, and ending a run.</summary>
public class InterviewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static WriteCodeStep Problem(string id, string pattern) =>
        new(id, id, "?", "python", "", "f", [new CodeTest("1", "1")]) { Pattern = pattern };

    /// <summary>A lesson teaching pattern.{name} with <paramref name="problems"/> problems tagged with it.</summary>
    private static Lesson Teaching(string name, int problems = 2) =>
        new($"lesson.{name}", name, "track.t", Enumerable.Range(1, problems).Select(i => (Step)Problem($"step.{name}.p{i}", $"pattern.{name}")).ToList())
        {
            Patterns = [new Pattern($"pattern.{name}", $"Pattern {name}")],
        };

    private static readonly Lesson A = Teaching("a"), B = Teaching("b"), C = Teaching("c"), D = Teaching("d");

    private static readonly ContentGraph Content =
        new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", new[] { A, B, C, D }.Select(l => l.Id).ToList())], [A, B, C, D]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private void Finish(params Lesson[] lessons)
    {
        foreach (var lesson in lessons)
        foreach (var s in lesson.Steps)
            Recorder.CompleteStep(lesson, s.Id);
    }

    private LearnerState Project() => LearnerStateProjector.Project(_log.ReadAll(), Content, _clock.Now);

    private static Step? StepOf(string id) => Content.Get<Step>(id);

    private InterviewRun Start(InterviewPreset preset = InterviewPreset.Interview, int seed = 1) =>
        Recorder.StartInterview(preset, PracticeSelector.Set(Project(), new Random(seed), InterviewPresets.Problems(preset)))!;

    // --- Choosing the set ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_set_prefers_a_different_pattern_for_each_problem()
    {
        Finish(A, B, C);

        for (var seed = 0; seed < 20; seed++)
        {
            var set = PracticeSelector.Set(Project(), new Random(seed), 3);
            Assert.Equal(3, set.Select(p => p.Pattern.Id).Distinct().Count());
            Assert.All(set, p => Assert.Contains(p.Pattern, p.Choices));
        }
    }

    [Fact]
    public void A_set_repeats_a_pattern_only_once_every_pattern_is_in_it()
    {
        Finish(A, B, C);

        var set = PracticeSelector.Set(Project(), new Random(3), 5);

        Assert.Equal(5, set.Select(p => p.Step.Id).Distinct().Count());
        Assert.Equal(3, set.Take(3).Select(p => p.Pattern.Id).Distinct().Count());
    }

    [Fact]
    public void A_set_is_as_large_as_what_is_on_offer_and_empty_when_Practice_is_closed()
    {
        Assert.Empty(PracticeSelector.Set(Project(), new Random(1), 3));

        Finish(A, B, C);

        Assert.Equal(6, PracticeSelector.Set(Project(), new Random(1), 10).Count);
    }

    // --- Starting ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Starting_fixes_the_problems_their_choices_and_the_clock()
    {
        Finish(A, B, C);
        var set = PracticeSelector.Set(Project(), new Random(1), 2);

        var run = Recorder.StartInterview(InterviewPreset.PhoneScreen, set)!;

        var e = Assert.Single(_log.Items, e => e.Type == ProgressEventTypes.InterviewStarted);
        Assert.Equal(("", ""), (e.LessonId, e.StepId));
        var data = JsonDocument.Parse(e.Data!).RootElement;
        Assert.Equal("phone-screen", data.GetProperty("preset").GetString());
        Assert.Equal(1800, data.GetProperty("limitSeconds").GetInt32());
        Assert.Equal(run.Id, data.GetProperty("run").GetString());

        Assert.Equal(InterviewPreset.PhoneScreen, run.Preset);
        Assert.Equal(T0, run.StartedAt);
        Assert.Equal(TimeSpan.FromMinutes(30), run.Limit);
        Assert.Equal(set.Select(p => (p.Lesson.Id, p.Step.Id)), run.Problems.Select(p => (p.LessonId, p.StepId)));
        Assert.Equal(set.Select(p => p.Choices.Select(c => c.Id)), run.Problems.Select(p => p.Choices));
        Assert.False(run.IsFinished);
        Assert.Equal(run, InterviewRuns.Open(_log.Items), new RunComparer());
    }

    [Fact]
    public void Only_one_run_is_open_at_a_time()
    {
        Finish(A, B, C);
        var first = Start();

        Assert.Null(Recorder.StartInterview(InterviewPreset.PhoneScreen, PracticeSelector.Set(Project(), new Random(2), 2)));
        Assert.Single(_log.Items, e => e.Type == ProgressEventTypes.InterviewStarted);

        Recorder.FinishInterview(first.Id, StepOf);

        Assert.NotNull(Start(InterviewPreset.PhoneScreen));
    }

    [Fact]
    public void Interview_presets_are_two_problems_in_30_minutes_and_three_in_45()
    {
        Assert.Equal((2, TimeSpan.FromMinutes(30)), (InterviewPresets.Problems(InterviewPreset.PhoneScreen), InterviewPresets.Limit(InterviewPreset.PhoneScreen)));
        Assert.Equal((3, TimeSpan.FromMinutes(45)), (InterviewPresets.Problems(InterviewPreset.Interview), InterviewPresets.Limit(InterviewPreset.Interview)));
    }

    // --- The clock -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_time_left_is_wall_time_from_the_start_and_the_run_is_over_at_the_limit()
    {
        Finish(A, B, C);
        var run = Start();

        Assert.Equal(TimeSpan.FromMinutes(45), run.Left(T0));
        Assert.Equal(TimeSpan.FromMinutes(5), run.Left(T0.AddMinutes(40)));
        Assert.False(run.IsOver(T0.AddMinutes(44)));
        Assert.True(run.IsOver(T0.AddMinutes(45)));
        Assert.Equal(TimeSpan.Zero, run.Left(T0.AddHours(3)));
    }

    // --- Answers and finishing ---------------------------------------------------------------------------------------

    [Fact]
    public void A_run_holds_its_first_answer_to_each_part_and_is_complete_when_every_part_is_answered()
    {
        Finish(A, B, C);
        var run = Start(InterviewPreset.PhoneScreen);
        var (one, two) = (run.Problems[0], run.Problems[1]);

        _clock.Now = T0.AddMinutes(1);
        Recorder.PracticePicked(one.LessonId, one.StepId, one.Choices[0], false, TimeSpan.FromSeconds(30), PracticeMode.Interview, run.Id);
        _clock.Now = T0.AddMinutes(2);   // later: at the same moment, which came first is down to the events' random ids
        Recorder.PracticePicked(one.LessonId, one.StepId, one.Choices[1], true, TimeSpan.FromSeconds(40), PracticeMode.Interview, run.Id);
        Recorder.PracticePicked(two.LessonId, two.StepId, two.Choices[0], false, TimeSpan.FromSeconds(30), PracticeMode.Practice, run.Id);
        run = InterviewRuns.Get(_log.Items, run.Id)!;

        Assert.Equal(one.Choices[0], run.Answer(one.StepId, PracticePart.Pick)!.Picked);
        Assert.Null(run.Answer(two.StepId, PracticePart.Pick));   // a practice answer is not part of the run
        Assert.False(run.IsComplete);

        foreach (var p in run.Problems)
        {
            Recorder.PracticePicked(p.LessonId, p.StepId, p.Choices[0], false, TimeSpan.FromSeconds(30), PracticeMode.Interview, run.Id);
            Recorder.PracticeSolved(p.LessonId, StepOf(p.StepId)!, p.StepId, true, 0, TimeSpan.FromMinutes(5), PracticeMode.Interview, run.Id);
        }

        Assert.True(InterviewRuns.Get(_log.Items, run.Id)!.IsComplete);
    }

    [Fact]
    public void Finishing_records_every_unanswered_part_as_wrong_then_the_finish()
    {
        Finish(A, B, C);
        var run = Start(InterviewPreset.PhoneScreen);
        var (one, two) = (run.Problems[0], run.Problems[1]);
        Recorder.PracticePicked(one.LessonId, one.StepId, one.Choices[0], true, TimeSpan.FromSeconds(30), PracticeMode.Interview, run.Id);
        _clock.Now = T0.AddMinutes(12);

        Recorder.FinishInterview(run.Id, StepOf);

        Assert.Equal(ProgressEventTypes.InterviewFinished, _log.Items[^1].Type);
        var ended = InterviewRuns.Get(_log.Items, run.Id)!;
        Assert.True(ended.IsFinished);
        Assert.Null(InterviewRuns.Open(_log.Items));
        Assert.True(ended.IsComplete);
        Assert.Equal(one.Choices[0], ended.Answer(one.StepId, PracticePart.Pick)!.Picked);   // the pick made is kept

        var missed = new[] { ended.Answer(one.StepId, PracticePart.Solve)!, ended.Answer(two.StepId, PracticePart.Pick)!, ended.Answer(two.StepId, PracticePart.Solve)! };
        Assert.All(missed, a => Assert.Equal((false, Rating.Again, PracticeMode.Interview, TimeSpan.Zero), (a.Correct, a.Rating, a.Mode, a.Elapsed)));
        Assert.Null(missed[1].Picked);

        // Both parts of the unanswered problem now come back in Review.
        var state = Project();
        Assert.Equal(Rating.Again, state.Reviews.Single(r => r.Item.Id == "pick." + two.StepId).LastRating);
        Assert.Equal(Rating.Again, state.Reviews.Single(r => r.Item.Id == "resolve." + two.StepId).LastRating);
    }

    [Fact]
    public void Finishing_after_time_ran_out_records_the_unanswered_parts_once()
    {
        Finish(A, B, C);
        var run = Start(InterviewPreset.PhoneScreen);
        _clock.Now = T0.AddHours(5);

        Recorder.FinishInterview(run.Id, StepOf);
        var count = _log.Items.Count;
        Recorder.FinishInterview(run.Id, StepOf);

        Assert.Equal(count, _log.Items.Count);
        var answers = InterviewRuns.Get(_log.Items, run.Id)!.Answers;
        Assert.Equal(4, answers.Count);
        Assert.All(answers, a => Assert.False(a.Correct));
    }

    [Fact]
    public void A_malformed_start_is_ignored()
    {
        foreach (var data in new[] { null, "[]", "{}", "{\"run\":\"r\",\"preset\":\"marathon\",\"limitSeconds\":60,\"steps\":[{\"step\":\"s\",\"lesson\":\"l\"}]}",
                     "{\"run\":\"r\",\"preset\":\"interview\",\"limitSeconds\":0,\"steps\":[{\"step\":\"s\",\"lesson\":\"l\"}]}",
                     "{\"run\":\"r\",\"preset\":\"interview\",\"limitSeconds\":60,\"steps\":[]}", "not json" }
                     .Concat(new[] { "\"1800\"", "null", "true", "{}", "[]", "1.5" }.Select(limit =>
                         $"{{\"run\":\"r\",\"preset\":\"interview\",\"limitSeconds\":{limit},\"steps\":[{{\"step\":\"s\",\"lesson\":\"l\"}}]}}")))
            _log.Append(new ProgressEvent(Guid.NewGuid().ToString("N"), "d", T0, ProgressEventTypes.InterviewStarted, "", "", data));

        Assert.Empty(InterviewRuns.From(_log.Items));
        Assert.Null(InterviewRuns.Open(_log.Items));
    }

    [Fact]
    public void The_first_answer_is_chosen_by_time_then_event_ID_whatever_order_the_log_holds_them_in()
    {
        Finish(A, B, C);
        var run = Start();
        var problem = run.Problems[0];
        var wrong = problem.Choices.First(c => c != StepOf(problem.StepId)!.Pattern);
        _clock.Now = T0.AddMinutes(1);
        Recorder.PracticePicked(problem.LessonId, problem.StepId, wrong, false, TimeSpan.FromMinutes(1), PracticeMode.Interview, run.Id);
        _clock.Now = T0.AddMinutes(2);
        Recorder.PracticePicked(problem.LessonId, problem.StepId, StepOf(problem.StepId)!.Pattern, true, TimeSpan.FromMinutes(2), PracticeMode.Interview, run.Id);

        // Two answers at the same instant (one device answering, another finishing the run): the lower event ID comes first.
        var tied = run.Problems[1];
        var answers = new[] { true, false }.Select(correct =>
        {
            Recorder.PracticePicked(tied.LessonId, tied.StepId, correct ? StepOf(tied.StepId)!.Pattern : null, correct, TimeSpan.Zero, PracticeMode.Interview, run.Id);
            return _log.Items[^1];
        }).ToList();
        var renamed = answers.Select((e, i) => e with { Id = i == 0 ? "b" : "a" }).ToList();   // the wrong one has the lower ID
        _log.Items.RemoveRange(_log.Items.Count - 2, 2);
        _log.Items.AddRange(renamed);

        foreach (var order in new[] { _log.Items.ToList(), Enumerable.Reverse(_log.Items).ToList() })
        {
            var read = InterviewRuns.Get(order, run.Id)!;
            Assert.False(read.Answer(problem.StepId, PracticePart.Pick)!.Correct);
            Assert.False(read.Answer(tied.StepId, PracticePart.Pick)!.Correct);
        }
    }

    /// <summary>Runs hold lists, so records compare them by reference; this compares what they say.</summary>
    private sealed class RunComparer : IEqualityComparer<InterviewRun?>
    {
        public bool Equals(InterviewRun? x, InterviewRun? y) =>
            x is not null && y is not null && x.Id == y.Id && x.Preset == y.Preset && x.StartedAt == y.StartedAt && x.Limit == y.Limit
            && x.IsFinished == y.IsFinished && x.Problems.SequenceEqual(y.Problems, ProblemComparer.Instance) && x.Answers.SequenceEqual(y.Answers);

        public int GetHashCode(InterviewRun? obj) => obj?.Id.GetHashCode() ?? 0;
    }

    private sealed class ProblemComparer : IEqualityComparer<InterviewProblem>
    {
        public static readonly ProblemComparer Instance = new();
        public bool Equals(InterviewProblem? x, InterviewProblem? y) =>
            x is not null && y is not null && (x.LessonId, x.StepId) == (y.LessonId, y.StepId) && x.Choices.SequenceEqual(y.Choices);
        public int GetHashCode(InterviewProblem obj) => obj.StepId.GetHashCode();
    }
}
