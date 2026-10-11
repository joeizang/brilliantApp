using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Tests;

/// <summary>
/// On a phone the Review Queue Builder offers the tap-friendly form of a question that has one. Only the form changes:
/// what is due, in what order and on what schedule must not depend on the device, or two devices would disagree about the queue.
/// </summary>
public class PhoneReviewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private static readonly ParsonsStep Tap = new("step.p.write-tap", "Arrange", "?", "python", [new ParsonsLine("def f(x):", 0), new ParsonsLine("return x", 1)]);
    private static readonly WriteCodeStep Write = new("step.p.write", "Write", "?", "python", "", "f", [new CodeTest("1", "1")])
    {
        LargeScreenPreferred = true,
        PhoneAlternative = Tap,
    };
    private static readonly WriteCodeStep Heavy = new("step.p.heavy", "Heavy", "?", "python", "", "g", [new CodeTest("1", "1")]) { LargeScreenPreferred = true };
    private static readonly ChoiceStep Choice = new("step.p.choice", "Choice", "?", false, [new ChoiceOption("a", true, null), new ChoiceOption("b", false, null)]);

    private static readonly Lesson Practice = new("lesson.p", "Practice", "track.t", [Choice, Write, Heavy])
    {
        Concepts = [new Concept("concept.p", "P")],
        ReviewItems =
        [
            new ReviewItem("review.p.choice", "concept.p", "step.p.choice"),
            new ReviewItem("review.p.write", "concept.p", "step.p.write"),
            new ReviewItem("review.p.heavy", "concept.p", "step.p.heavy"),
        ],
    };

    private static readonly ContentGraph Content = new(
        new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [Practice.Id])], [Practice]);

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private ReviewItemState Item(ReviewKind kind, Step question, string id) =>
        new(Practice, new ReviewItem(id, "concept.p", question.Id, kind), question, true, null, 0);

    private static string[] PresentedIds(ReviewQueue queue) => queue.Items.Select(i => i.Presented.Id).ToArray();

    private LearnerState Project(ScreenClass screen)
    {
        foreach (var s in Practice.Steps) Recorder.CompleteStep(Practice, s.Id);
        return LearnerStateProjector.Project(_log.ReadAll(), Content, _clock.Now, screen: screen);
    }

    // --- Which form is offered -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ScreenClass.Desktop)]
    [InlineData(ScreenClass.Tablet)]
    public void A_screen_that_is_not_a_phone_gets_the_question_as_authored(ScreenClass screen)
    {
        var queue = ReviewQueueBuilder.Build([Item(ReviewKind.Concept, Write, "review.p.write")], T0, 0, screen: screen);

        Assert.Equal(["step.p.write"], PresentedIds(queue));
        Assert.Null(queue.Items[0].Shown);
    }

    [Fact]
    public void A_phone_gets_the_tap_friendly_form_of_a_question_that_has_one()
    {
        var queue = ReviewQueueBuilder.Build([Item(ReviewKind.Concept, Write, "review.p.write")], T0, 0, screen: ScreenClass.Phone);

        var item = Assert.Single(queue.Items);
        Assert.Same(Tap, item.Presented);
        Assert.Equal("step.p.write", item.Question.Id);   // what the item re-asks is unchanged
        Assert.Equal("step.p.write", item.Item.StepId);
    }

    [Fact]
    public void A_re_solve_is_offered_in_its_phone_form_on_a_phone()
    {
        var queue = ReviewQueueBuilder.Build([Item(ReviewKind.Resolve, Write, "resolve.step.p.write")], T0, 0, screen: ScreenClass.Phone);

        Assert.Equal(["step.p.write-tap"], PresentedIds(queue));
        Assert.Equal(ReviewKind.Resolve, queue.Items[0].Item.Kind);
    }

    [Fact]
    public void Questions_without_a_phone_form_are_offered_as_they_are()
    {
        var queue = ReviewQueueBuilder.Build(
            [Item(ReviewKind.Concept, Choice, "review.p.choice"), Item(ReviewKind.Concept, Heavy, "review.p.heavy")], T0, 0, screen: ScreenClass.Phone);

        Assert.Equal(["step.p.choice", "step.p.heavy"], PresentedIds(queue));
    }

    [Fact]
    public void A_large_screen_question_without_a_phone_form_says_so_on_a_phone()
    {
        var queue = ReviewQueueBuilder.Build(
            [Item(ReviewKind.Concept, Heavy, "review.p.heavy"), Item(ReviewKind.Concept, Write, "review.p.write"), Item(ReviewKind.Concept, Choice, "review.p.choice")],
            T0, 0, screen: ScreenClass.Phone);

        Assert.Equal([true, false, false], queue.Items.Select(i => i.NeedsLargeScreen));
    }

    // --- The queue itself does not depend on the screen ------------------------------------------------------------

    [Fact]
    public void Which_items_are_due_and_in_what_order_does_not_depend_on_the_screen()
    {
        var items = new[]
        {
            Item(ReviewKind.Concept, Write, "review.p.write"), Item(ReviewKind.Pattern, Choice, "review.p.choice"),
            Item(ReviewKind.Resolve, Heavy, "resolve.step.p.heavy"), Item(ReviewKind.Resolve, Write, "resolve.step.p.write"),
        };
        var options = new ReviewQueueOptions(DailyCap: 3);

        var desktop = ReviewQueueBuilder.Build(items, T0, 1, options, ScreenClass.Desktop);
        var phone = ReviewQueueBuilder.Build(items, T0, 1, options, ScreenClass.Phone);

        Assert.Equal(desktop.Items.Select(i => i.Item.Id), phone.Items.Select(i => i.Item.Id));
        Assert.Equal((desktop.Waiting, desktop.DoneToday), (phone.Waiting, phone.DoneToday));
    }

    [Fact]
    public void The_learner_state_is_the_same_on_every_screen_apart_from_the_form_of_queued_questions()
    {
        var phone = Project(ScreenClass.Phone);
        var desktop = LearnerStateProjector.Project(_log.ReadAll(), Content, _clock.Now);

        Assert.Equal(desktop.Reviews.Select(r => (r.Item.Id, r.Card, r.Answers, r.IsUnlocked)), phone.Reviews.Select(r => (r.Item.Id, r.Card, r.Answers, r.IsUnlocked)));
        Assert.Equal(desktop.Tracks.SelectMany(t => t.Lessons).Select(l => (l.Lesson.Id, l.Status)), phone.Tracks.SelectMany(t => t.Lessons).Select(l => (l.Lesson.Id, l.Status)));
        Assert.Equal(desktop.Queue.Waiting, phone.Queue.Waiting);
        Assert.Equal(desktop.Queue.Items.Select(i => i.Item.Id), phone.Queue.Items.Select(i => i.Item.Id));
        Assert.Contains(phone.Queue.Items, i => i.Presented.Id == "step.p.write-tap");
        Assert.DoesNotContain(desktop.Queue.Items, i => i.Presented.Id == "step.p.write-tap");
    }

    [Fact]
    public void An_answer_recorded_from_a_phone_form_schedules_the_same_item_as_one_from_the_full_form()
    {
        var phone = Project(ScreenClass.Phone).Queue.Items.Single(i => i.Item.Id == "review.p.write");
        Recorder.ReviewAnswered(phone, correct: true, hintsUsed: 0, TimeSpan.FromSeconds(30));

        var after = LearnerStateProjector.Project(_log.ReadAll(), Content, _clock.Now);

        var answered = after.Reviews.Single(r => r.Item.Id == "review.p.write");
        Assert.Equal(1, answered.Answers);
        Assert.NotNull(answered.Card);
        Assert.Equal("step.p.write", answered.Question.Id);
    }

    [Fact]
    public void The_rating_is_judged_against_the_form_that_was_answered()
    {
        var phone = Item(ReviewKind.Concept, Write, "review.p.write").For(ScreenClass.Phone);
        // 40 s is a middling time to arrange lines (Parsons: easy under 30 s, hard over 150 s) but quick for writing a function (easy under 90 s).
        var onPhone = Recorder.ReviewAnswered(phone, true, 0, TimeSpan.FromSeconds(40));
        var onDesktop = Recorder.ReviewAnswered(phone.For(ScreenClass.Desktop), true, 0, TimeSpan.FromSeconds(40));

        Assert.Equal(Rating.Good, onPhone);
        Assert.Equal(Rating.Easy, onDesktop);
    }

    // --- Screen classes --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(360, ScreenClass.Phone)]
    [InlineData(412, ScreenClass.Phone)]
    [InlineData(600, ScreenClass.Phone)]
    [InlineData(601, ScreenClass.Tablet)]
    [InlineData(800, ScreenClass.Tablet)]
    [InlineData(895, ScreenClass.Tablet)]
    [InlineData(896, ScreenClass.Desktop)]
    [InlineData(1440, ScreenClass.Desktop)]
    public void A_screen_is_classed_by_its_width(double width, ScreenClass expected) =>
        Assert.Equal(expected, ScreenClasses.FromWidth(width));
}
