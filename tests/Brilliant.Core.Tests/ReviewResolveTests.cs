using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>
/// What the projector adds for the Review Queue Builder: re-solve items for problems answered incorrectly, authored
/// pattern items, and how many reviews were answered today.
/// </summary>
public class ReviewResolveTests
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

    private static readonly WriteCodeStep Write = new("step.p.write", "Write", "?", "python", "", "f", [new CodeTest("1", "1")]);
    private static readonly FillBlankStep Fill = new("step.p.fill", "Fill", "?", "python", "x = {{b}}", [new Blank("b", ["1"], [])]);
    private static readonly ParsonsStep Parsons = new("step.p.parsons", "Order", "?", "python", [new ParsonsLine("x = 1", 0)]);
    private static readonly ChoiceStep Choice = new("step.p.choice", "Choice", "?", false, [new ChoiceOption("a", true, null), new ChoiceOption("b", false, null)]);

    private static readonly Lesson Practice = new("lesson.p", "Practice", "track.t",
        [new ExplainStep("step.p.intro", "intro", "b", [], null), Choice, Write, Fill, Parsons])
    {
        Concepts = [new Concept("concept.p", "P")],
        ReviewItems =
        [
            new ReviewItem("review.p.choice", "concept.p", "step.p.choice"),
            new ReviewItem("review.p.pattern", "concept.p", "step.p.choice", ReviewKind.Pattern),
        ],
    };

    private static ContentGraph Graph(Lesson? lesson = null) => new(
        new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [(lesson ?? Practice).Id])], [lesson ?? Practice]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private void Finish(Lesson? lesson = null)
    {
        lesson ??= Practice;
        foreach (var s in lesson.Steps) Recorder.CompleteStep(lesson, s.Id);
    }

    private LearnerState Project(DateTimeOffset? now = null, ContentGraph? content = null, ReviewQueueOptions? options = null) =>
        LearnerStateProjector.Project(_log.ReadAll(), content ?? Graph(), now ?? _clock.Now, options);

    private static string[] Ids(IEnumerable<ReviewItemState> items) => items.Select(r => r.Item.Id).ToArray();

    private void Answer(string itemId, Rating rating, DateTimeOffset at)
    {
        _clock.Now = at;
        Recorder.ReviewAnswered(Practice.Id, Practice.ReviewItems.SingleOrDefault(i => i.Id == itemId) ?? new ReviewItem(itemId, "", itemId.Replace("resolve.", ""), ReviewKind.Resolve),
            correct: rating != Rating.Again, hintsUsed: 0, TimeSpan.FromSeconds(10), rating);
    }

    // --- Re-solve items --------------------------------------------------------------------------------------------

    [Fact]
    public void A_problem_answered_correctly_first_time_has_no_re_solve()
    {
        Recorder.StepAnswered(Practice.Id, Fill.Id, true, new Dictionary<string, string> { ["b"] = "1" });
        Recorder.CodeSubmitted(Practice.Id, Write.Id, "def f(x): return x", 1, 1);
        Finish();

        Assert.DoesNotContain(Project().Reviews, r => r.Item.Kind == ReviewKind.Resolve);
    }

    [Fact]
    public void A_wrong_fill_blank_answer_makes_a_re_solve_once_the_lesson_is_finished()
    {
        Recorder.StepAnswered(Practice.Id, Fill.Id, false, new Dictionary<string, string> { ["b"] = "2" });
        Recorder.StepAnswered(Practice.Id, Fill.Id, true, new Dictionary<string, string> { ["b"] = "1" });

        var before = Project().Reviews.Single(r => r.Item.Kind == ReviewKind.Resolve);
        Assert.Equal(("resolve.step.p.fill", false), (before.Item.Id, before.IsUnlocked));
        Assert.Equal(Fill, before.Question);
        Assert.DoesNotContain("resolve.step.p.fill", Ids(Project().Queue.Items));

        Finish();

        Assert.Contains("resolve.step.p.fill", Ids(Project().Queue.Items));
    }

    [Fact]
    public void A_failed_code_submission_makes_a_re_solve()
    {
        Recorder.CodeSubmitted(Practice.Id, Write.Id, "def f(x): pass", 0, 1);
        Recorder.CodeSubmitted(Practice.Id, Write.Id, "def f(x): return x", 1, 1);
        Finish();

        Assert.Contains("resolve.step.p.write", Ids(Project().Queue.Items));
    }

    [Fact]
    public void A_wrong_parsons_arrangement_makes_a_re_solve()
    {
        Recorder.StepAnswered(Practice.Id, Parsons.Id, false, [new ParsonsPlacement(0, 1)]);
        Finish();

        Assert.Contains("resolve.step.p.parsons", Ids(Project().Queue.Items));
    }

    [Fact]
    public void Only_problems_get_re_solves_a_wrong_choice_does_not()
    {
        Recorder.StepAnswered(Practice.Id, Choice.Id, false, [1]);
        Finish();

        Assert.DoesNotContain(Project().Reviews, r => r.Item.Kind == ReviewKind.Resolve);
    }

    [Fact]
    public void Each_problem_gets_one_re_solve_however_many_times_it_was_missed()
    {
        for (var i = 0; i < 3; i++) Recorder.CodeSubmitted(Practice.Id, Write.Id, "def f(x): pass", 0, 1);
        Finish();

        Assert.Single(Project().Reviews, r => r.Item.Id == "resolve.step.p.write");
    }

    [Fact]
    public void A_re_solve_is_scheduled_like_any_other_item_and_leaves_the_queue_once_answered()
    {
        Recorder.CodeSubmitted(Practice.Id, Write.Id, "def f(x): pass", 0, 1);
        Finish();
        Answer("resolve.step.p.write", Rating.Good, T0.AddHours(1));

        var state = Project(T0.AddHours(2));
        var resolve = state.Reviews.Single(r => r.Item.Id == "resolve.step.p.write");

        Assert.Equal(1, resolve.Answers);
        Assert.Equal(T0.AddHours(1).AddDays(3), resolve.Card!.Due);
        Assert.DoesNotContain("resolve.step.p.write", Ids(state.Queue.Items));
        Assert.Contains("resolve.step.p.write", Ids(Project(resolve.Card.Due).Queue.Items));
    }

    [Fact]
    public void A_re_solve_answered_Again_comes_back_after_a_day()
    {
        Recorder.StepAnswered(Practice.Id, Fill.Id, false, new Dictionary<string, string> { ["b"] = "2" });
        Finish();
        Answer("resolve.step.p.fill", Rating.Again, T0.AddHours(1));

        Assert.DoesNotContain("resolve.step.p.fill", Ids(Project(T0.AddHours(5)).Queue.Items));
        Assert.Contains("resolve.step.p.fill", Ids(Project(T0.AddHours(1).AddDays(1)).Queue.Items));
    }

    [Fact]
    public void A_re_solve_retires_with_its_problem()
    {
        Recorder.CodeSubmitted(Practice.Id, Write.Id, "def f(x): pass", 0, 1);
        Finish();
        var without = Practice with { Steps = Practice.Steps.Where(s => s.Id != Write.Id).ToList() };

        Assert.DoesNotContain(Project(content: Graph(without)).Reviews, r => r.Item.Kind == ReviewKind.Resolve);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[false]")]
    [InlineData("{\"correct\":\"false\"}")]
    [InlineData("{\"correct\":0}")]
    [InlineData("{}")]
    public void A_malformed_attempt_is_not_a_failure(string? data)
    {
        _log.Items.Add(new ProgressEvent("e1", "device", T0, ProgressEventTypes.StepAnswered, Practice.Id, Fill.Id, data));
        _log.Items.Add(new ProgressEvent("e2", "device", T0, ProgressEventTypes.CodeSubmitted, Practice.Id, Write.Id, data));
        Finish();

        Assert.DoesNotContain(Project().Reviews, r => r.Item.Kind == ReviewKind.Resolve);
    }

    [Fact]
    public void Wrong_answers_given_during_review_do_not_create_more_re_solves()
    {
        Finish();
        Answer("review.p.choice", Rating.Again, T0.AddHours(1));

        Assert.DoesNotContain(Project(T0.AddDays(5)).Reviews, r => r.Item.Kind == ReviewKind.Resolve);
    }

    // --- Authored kinds --------------------------------------------------------------------------------------------

    [Fact]
    public void An_authored_pattern_item_keeps_its_kind_and_is_weighted_ahead_of_concepts()
    {
        Finish();

        var queue = Project().Queue.Items;

        Assert.Equal(["review.p.pattern", "review.p.choice"], Ids(queue));
        Assert.Equal([ReviewKind.Pattern, ReviewKind.Concept], queue.Select(r => r.Item.Kind));
    }

    // --- The daily cap in the projection ---------------------------------------------------------------------------

    [Fact]
    public void Reviews_answered_today_count_against_the_cap_and_earlier_days_do_not()
    {
        Finish();
        Answer("review.p.choice", Rating.Good, T0.AddDays(-1));
        Answer("review.p.pattern", Rating.Good, T0.AddHours(1));

        var state = Project(T0.AddHours(2), options: new ReviewQueueOptions(DailyCap: 1));

        Assert.Equal(1, state.Queue.DoneToday);
        Assert.Empty(state.Queue.Items);
    }

    [Fact]
    public void The_cap_leaves_room_for_what_has_not_been_answered_yet()
    {
        Finish();
        Answer("review.p.pattern", Rating.Good, T0.AddHours(1));
        Recorder.CodeSubmitted(Practice.Id, Write.Id, "def f(x): pass", 0, 1);

        var state = Project(T0.AddHours(2), options: new ReviewQueueOptions(DailyCap: 2));

        Assert.Equal(1, state.Queue.DoneToday);
        Assert.Single(state.Queue.Items);
        Assert.Equal(1, state.Queue.Waiting);
    }

    [Fact]
    public void The_day_rolls_over_at_the_learners_midnight_not_UTC()
    {
        Finish();
        var plusOne = TimeSpan.FromHours(1);
        Answer("review.p.pattern", Rating.Good, new DateTimeOffset(2026, 10, 10, 22, 30, 0, TimeSpan.Zero)); // 23:30 on the 10th at UTC+1
        Answer("review.p.choice", Rating.Good, new DateTimeOffset(2026, 10, 10, 23, 30, 0, TimeSpan.Zero));  // 00:30 on the 11th at UTC+1

        var now = new DateTimeOffset(2026, 10, 11, 8, 0, 0, plusOne);

        Assert.Equal(1, Project(now).Queue.DoneToday);                        // only the 00:30 answer is today for the learner
        Assert.Equal(0, Project(now.ToUniversalTime()).Queue.DoneToday);     // read as UTC both answers are "yesterday"
    }

    [Fact]
    public void Answers_with_bad_data_do_not_use_up_the_cap()
    {
        Finish();
        _log.Items.Add(new ProgressEvent("bad", "device", T0, ProgressEventTypes.ReviewAnswered, Practice.Id, Choice.Id, "{\"item\":\"review.p.choice\",\"rating\":\"3\"}"));

        Assert.Equal(0, Project(T0.AddHours(1)).Queue.DoneToday);
    }

    [Fact]
    public void The_projected_queue_is_the_same_whatever_order_the_events_arrive_in()
    {
        Recorder.CodeSubmitted(Practice.Id, Write.Id, "def f(x): pass", 0, 1);
        Finish();
        Answer("review.p.choice", Rating.Good, T0.AddHours(1));

        var inOrder = Project(T0.AddDays(10));
        var shuffled = LearnerStateProjector.Project(_log.Items.AsEnumerable().Reverse(), Graph(), T0.AddDays(10));

        Assert.Equal(Ids(inOrder.Queue.Items), Ids(shuffled.Queue.Items));
        Assert.Equal(inOrder.Queue.DoneToday, shuffled.Queue.DoneToday);
    }
}
