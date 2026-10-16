using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>"I know this — test me" (#146): offered at the start of a lesson, passes only on a perfect run, and costs nothing when missed.</summary>
public class TestOutTests : ShortcutContext
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

    // The right option is always first, so "answer right" is the first radio and "answer wrong" the second.
    private static ChoiceStep Q(string id) =>
        new(id, "Question " + id, "Pick", false, [new ChoiceOption("right", true, null), new ChoiceOption("wrong", false, null)]);

    private static Lesson WithQuestions(int count) =>
        new("lesson.l", "Lesson", "track.t", [new ExplainStep("step.intro", "Intro", "body", [], null), .. Enumerable.Range(1, count).Select(i => Q($"step.q{i}"))])
        {
            Concepts = [new Concept("concept.c", "C")],
            ReviewItems = Enumerable.Range(1, count).Select(i => new ReviewItem($"review.q{i}", "concept.c", $"step.q{i}")).ToList(),
        };

    private static ContentGraph Graph(Lesson lesson) => new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [lesson.Id])], [lesson]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private readonly ProgressRecorder _recorder;

    public TestOutTests()
    {
        _recorder = new ProgressRecorder(_log, "device", _clock);
        Services.AddSingleton(_recorder);
    }

    private IRenderedComponent<LessonViewer> Open(Lesson lesson, bool review = false) =>
        Render<LessonViewer>(p => p.Add(c => c.Lesson, lesson).Add(c => c.Review, review));

    private static void Answer(IRenderedComponent<LessonViewer> cut, string option)
    {
        var index = cut.FindAll("ul.options li").ToList().FindIndex(li => li.TextContent.Trim() == option);
        cut.FindAll("ul.options input")[index].Change(true);
        cut.Find("button.primary").Click();   // Check
    }

    private static void Continue(IRenderedComponent<LessonViewer> cut) => cut.Find("button.primary").Click();

    private static void StartTest(IRenderedComponent<LessonViewer> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Contains("I know this — test me")).Click();

    // --- Offering ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_fresh_lesson_with_enough_review_items_offers_the_test()
    {
        var cut = Open(WithQuestions(4));

        Assert.Contains("I know this — test me", cut.Markup);
        Assert.Contains("Answer 4 questions", cut.Markup);
        Assert.Contains("Intro", cut.Markup);   // the lesson itself is still right there
    }

    [Fact]
    public void A_lesson_without_enough_review_items_does_not_offer_it()
    {
        Assert.DoesNotContain("test me", Open(WithQuestions(2)).Markup);
    }

    [Fact]
    public void A_lesson_already_started_does_not_offer_it()
    {
        var lesson = WithQuestions(4);
        _recorder.CompleteStep(lesson, "step.intro");

        Assert.DoesNotContain("test me", Open(lesson).Markup);
    }

    [Fact]
    public void Review_mode_does_not_offer_it()
    {
        Assert.DoesNotContain("test me", Open(WithQuestions(4), review: true).Markup);
    }

    // --- Passing -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Asking_for_the_test_shows_its_first_question_and_records_nothing()
    {
        var cut = Open(WithQuestions(4));

        StartTest(cut);

        Assert.Contains("Question step.q1", cut.Markup);
        Assert.Contains("question 1 of 4", cut.Find(".testout-progress").TextContent);
        Assert.Empty(_log.Items);
    }

    [Fact]
    public void Answering_every_question_right_completes_the_lesson_and_records_the_test_out()
    {
        var lesson = WithQuestions(4);
        var cut = Open(lesson);
        StartTest(cut);

        for (var i = 0; i < 4; i++)
        {
            Answer(cut, "right");
            Continue(cut);
        }

        var recorded = Assert.Single(_log.Items);
        Assert.Equal(ProgressEventTypes.LessonTestedOut, recorded.Type);
        Assert.Contains("tested out", cut.Markup);
        Assert.Empty(cut.FindAll(".testout-offer"));
        var state = _recorder.Project(Graph(lesson));
        Assert.Equal(LessonStatus.Completed, state.Tracks[0].Lessons[0].Status);
        Assert.Equal(0, state.Today.DoneToday);
    }

    [Fact]
    public void A_perfect_test_out_leaves_the_lesson_s_items_scheduled_far_out()
    {
        var lesson = WithQuestions(4);
        var cut = Open(lesson);
        StartTest(cut);
        for (var i = 0; i < 4; i++) { Answer(cut, "right"); Continue(cut); }

        var reviews = _recorder.Project(Graph(lesson)).Reviews;

        Assert.All(reviews, r => Assert.True(r.Card!.Due - T0 >= TimeSpan.FromDays(21)));
        Assert.Equal(ConceptLevel.Known, _recorder.Project(Graph(lesson)).Concepts.Single().Level);
    }

    // --- Missing -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_wrong_answer_ends_the_test_with_nothing_recorded()
    {
        var cut = Open(WithQuestions(4));
        StartTest(cut);

        Answer(cut, "right");
        Continue(cut);
        Answer(cut, "wrong");

        Assert.Contains("Nothing is held against you", cut.Markup);
        Assert.Empty(_log.Items);
    }

    [Fact]
    public void After_a_miss_the_lesson_starts_normally_from_its_first_step()
    {
        var lesson = WithQuestions(4);
        var cut = Open(lesson);
        StartTest(cut);
        Answer(cut, "wrong");

        cut.Find(".testout-missed button.primary").Click();

        Assert.Contains("Intro", cut.Markup);
        Assert.Contains("5 of 5 steps remaining", cut.Markup);
        Assert.Empty(_log.Items);
        Assert.Equal(LessonStatus.Available, _recorder.Project(Graph(lesson)).Tracks[0].Lessons[0].Status);
        Assert.All(_recorder.Project(Graph(lesson)).Reviews, r => Assert.Null(r.Card));
    }

    [Fact]
    public void A_miss_does_not_stop_the_learner_trying_again_later()
    {
        var lesson = WithQuestions(4);
        var cut = Open(lesson);
        StartTest(cut);
        Answer(cut, "wrong");
        cut.Find(".testout-missed button.primary").Click();

        // Nothing was recorded, so the offer is still there while no step has been played.
        Assert.Contains("I know this — test me", cut.Markup);
    }

    [Fact]
    public void A_lesson_that_was_tested_out_shows_as_finished_with_no_steps_to_play()
    {
        var lesson = WithQuestions(4);
        _recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));

        var cut = Open(lesson);

        Assert.Contains("tested out", cut.Markup);
        Assert.Empty(cut.FindAll(".lesson-step"));
        Assert.DoesNotContain("test me", cut.Markup);
    }

    [Fact]
    public void The_track_screen_shows_a_tested_out_lesson_as_completed_not_as_having_new_steps()
    {
        var lesson = WithQuestions(4);
        _recorder.LessonTestedOut(lesson, LessonTestOut.Select(lesson));

        var cut = Render<TrackView>(p => p.Add(c => c.Content, Graph(lesson)).Add(c => c.Track, Graph(lesson).Tracks[0]));

        Assert.Contains("1 of 1 lessons complete", cut.Markup);
        Assert.DoesNotContain("new step", cut.Markup);
        Assert.Equal("Review", cut.Find("li.lesson button").TextContent.Trim());
    }
}
