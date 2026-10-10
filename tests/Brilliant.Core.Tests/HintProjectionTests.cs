using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>What the projector derives from HintUsed events: re-solves for problems solved with help, and the hints already taken on an item.</summary>
public class HintProjectionTests
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
        ReviewItems = [new ReviewItem("review.p.choice", "concept.p", "step.p.choice")],
    };

    private static readonly ContentGraph Content = new(
        new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [Practice.Id])], [Practice]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private void Finish()
    {
        foreach (var s in Practice.Steps) Recorder.CompleteStep(Practice, s.Id);
    }

    private LearnerState Project() => LearnerStateProjector.Project(_log.ReadAll(), Content, _clock.Now);

    private ReviewItemState Item(string id) => Project().Reviews.Single(r => r.Item.Id == id);

    private void Answer(string itemId, int hints = 0)
    {
        var item = Item(itemId);
        Recorder.ReviewAnswered(item, correct: true, hints, TimeSpan.FromSeconds(2));
    }

    // --- Hinted lesson problems come back as re-solves -----------------------------------------------------------------

    [Theory]
    [InlineData("step.p.write")]
    [InlineData("step.p.fill")]
    [InlineData("step.p.parsons")]
    public void A_problem_solved_with_hints_in_a_lesson_gets_a_re_solve_even_without_a_wrong_answer(string stepId)
    {
        Recorder.HintUsed("lesson.p", stepId, 1);
        Recorder.CodeSubmitted("lesson.p", stepId, "return 1", 1, 1);   // passed first time
        Finish();

        var resolve = Item($"resolve.{stepId}");

        Assert.Equal(ReviewKind.Resolve, resolve.Item.Kind);
        Assert.True(resolve.IsUnlocked);
        Assert.True(resolve.IsNew);
    }

    [Fact]
    public void Revealing_the_full_solution_then_passing_first_time_is_not_forgotten()
    {
        foreach (var level in new[] { 1, 2, 3, 4 }) Recorder.HintUsed("lesson.p", "step.p.write", level);
        Recorder.CodeSubmitted("lesson.p", "step.p.write", "return 1", 1, 1);
        Finish();

        Assert.Contains(Project().Queue.Items, i => i.Item.Id == "resolve.step.p.write");
    }

    [Fact]
    public void A_re_solve_waits_for_the_lesson_to_finish()
    {
        Recorder.HintUsed("lesson.p", "step.p.write", 1);
        Recorder.CompleteStep(Practice, "step.p.intro");

        Assert.False(Item("resolve.step.p.write").IsUnlocked);
    }

    [Fact]
    public void Without_hints_or_a_wrong_answer_a_problem_has_no_re_solve()
    {
        Recorder.CodeSubmitted("lesson.p", "step.p.write", "return 1", 1, 1);
        Finish();

        Assert.DoesNotContain(Project().Reviews, r => r.Item.Kind == ReviewKind.Resolve);
    }

    [Fact]
    public void Hinted_choice_questions_get_no_re_solve_because_their_review_items_cover_them()
    {
        Recorder.HintUsed("lesson.p", "step.p.choice", 2);
        Finish();

        Assert.DoesNotContain(Project().Reviews, r => r.Item.Kind == ReviewKind.Resolve);
    }

    [Fact]
    public void Hints_asked_during_review_do_not_create_re_solves()
    {
        Recorder.HintUsed("lesson.p", "step.p.write", 1, "resolve.step.p.write");
        Finish();

        Assert.DoesNotContain(Project().Reviews, r => r.Item.Kind == ReviewKind.Resolve);
    }

    // --- Lesson hints carry into the first review ----------------------------------------------------------------------

    [Fact]
    public void Hints_taken_in_the_lesson_count_against_the_first_review_of_a_concept_item()
    {
        Recorder.HintUsed("lesson.p", "step.p.choice", 1);
        Recorder.HintUsed("lesson.p", "step.p.choice", 3);
        Finish();

        var item = Item("review.p.choice");

        Assert.Equal(3, item.LessonHints);
        Assert.Equal(3, item.CarriedHints);
    }

    [Fact]
    public void The_carried_hints_end_with_the_first_review()
    {
        Recorder.HintUsed("lesson.p", "step.p.choice", 3);
        Finish();
        _clock.Now = T0.AddMinutes(5);
        Answer("review.p.choice", hints: 3);

        var item = Item("review.p.choice");

        Assert.Equal(3, item.LessonHints);
        Assert.Equal(0, item.CarriedHints);
    }

    [Fact]
    public void A_re_solve_is_judged_on_its_own_hints_not_those_that_created_it()
    {
        Recorder.HintUsed("lesson.p", "step.p.write", 3);
        Finish();

        Assert.Equal(0, Item("resolve.step.p.write").CarriedHints);
    }

    // --- Hints already taken on an unanswered attempt ------------------------------------------------------------------

    [Fact]
    public void Hints_asked_in_review_are_remembered_until_the_item_is_answered()
    {
        Finish();
        Recorder.HintUsed("lesson.p", "step.p.choice", 1, "review.p.choice");
        Recorder.HintUsed("lesson.p", "step.p.choice", 2, "review.p.choice");

        Assert.Equal(2, Item("review.p.choice").AttemptHints);
    }

    [Fact]
    public void Answering_closes_the_attempt_so_the_next_one_starts_without_hints()
    {
        Finish();
        _clock.Now = T0.AddMinutes(1);
        Recorder.HintUsed("lesson.p", "step.p.choice", 3, "review.p.choice");
        _clock.Now = T0.AddMinutes(2);
        Answer("review.p.choice", hints: 3);

        Assert.Equal(0, Item("review.p.choice").AttemptHints);

        _clock.Now = T0.AddDays(2);
        Recorder.HintUsed("lesson.p", "step.p.choice", 1, "review.p.choice");

        Assert.Equal(1, Item("review.p.choice").AttemptHints);
    }

    [Fact]
    public void One_items_hints_do_not_leak_to_another()
    {
        Finish();
        Recorder.HintUsed("lesson.p", "step.p.write", 3, "resolve.step.p.write");
        Recorder.HintUsed("lesson.p", "step.p.choice", 1, "review.p.choice");

        Assert.Equal(1, Item("review.p.choice").AttemptHints);
    }

    [Fact]
    public void Malformed_hint_events_are_ignored()
    {
        Finish();
        foreach (var data in new string?[] { null, "not json", "[]", "{}", "{\"level\":\"x\"}", "{\"level\":0}", "{\"level\":-2,\"item\":\"review.p.choice\"}", "{\"level\":2,\"item\":7}" })
            _log.Append(new ProgressEvent(Guid.NewGuid().ToString("N"), "d", T0, ProgressEventTypes.HintUsed, "lesson.p", "step.p.choice", data));

        var item = Item("review.p.choice");

        Assert.Equal((0, 0), (item.AttemptHints, item.LessonHints));
        Assert.DoesNotContain(Project().Reviews, r => r.Item.Kind == ReviewKind.Resolve);
    }

    [Fact]
    public void The_order_events_arrive_in_does_not_matter()
    {
        Finish();
        _clock.Now = T0.AddMinutes(1);
        Recorder.HintUsed("lesson.p", "step.p.choice", 1, "review.p.choice");
        _clock.Now = T0.AddMinutes(2);
        Recorder.HintUsed("lesson.p", "step.p.choice", 2, "review.p.choice");
        Assert.Equal(2, Item("review.p.choice").AttemptHints);

        _log.Items.Reverse();

        Assert.Equal(2, Item("review.p.choice").AttemptHints);
    }
}
