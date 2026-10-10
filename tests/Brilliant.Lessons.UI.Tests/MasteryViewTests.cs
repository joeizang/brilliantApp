using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>Mastery bars on the tracks and track screens, and the concept detail screen behind them.</summary>
public class MasteryViewTests : ShortcutContext
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static ChoiceStep Q(string id) =>
        new(id, "Question " + id, "Pick", false, [new ChoiceOption("right", true, null), new ChoiceOption("wrong", false, null)]);

    private static readonly Lesson Lists = new("lesson.lists", "Lists", "track.py",
        [new ExplainStep("step.intro", "Intro", "b", [], null), Q("step.index"), Q("step.slice")])
    {
        Concepts = [new Concept("concept.index", "List indexing"), new Concept("concept.slice", "Slicing"), new Concept("concept.bare", "Bare idea")],
        ReviewItems = [new ReviewItem("review.index", "concept.index", "step.index"), new ReviewItem("review.slice", "concept.slice", "step.slice", ReviewKind.Pattern)],
    };

    private static readonly Lesson Later = new("lesson.later", "Later", "track.py", [new ExplainStep("step.later", "Later", "b", [], null)])
    {
        Concepts = [new Concept("concept.later", "A later idea")],
    };

    private static readonly ContentGraph Content = new(
        new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion), [new Track("track.py", "Python", [Lists.Id, Later.Id])], [Lists, Later]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder { get; set; } = default!;

    public MasteryViewTests()
    {
        Recorder = new ProgressRecorder(_log, "device", _clock);
        Services.AddSingleton(Recorder);
    }

    private void Finish()
    {
        foreach (var s in Lists.Steps) Recorder.CompleteStep(Lists, s.Id);
    }

    private void Review(string itemId, Rating rating, bool correct = true)
    {
        var state = Recorder.Project(Content).Reviews.Single(r => r.Item.Id == itemId);
        Recorder.ReviewAnswered(state.Lesson.Id, state.Item, correct, 0, TimeSpan.FromSeconds(3), rating);
    }

    private IRenderedComponent<ConceptView> OpenConcept(string id) =>
        Render<ConceptView>(p => p.Add(c => c.Content, Content).Add(c => c.ConceptId, id));

    // --- Mastery bars --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_track_card_shows_its_mastery_apart_from_lessons_complete()
    {
        Finish();
        Review("review.index", Rating.Good);

        var cut = Render<TracksView>(p => p.Add(c => c.Content, Content));

        var meter = cut.Find(".card [role=meter]");
        var expected = (int)Math.Round(100 * Recorder.Project(Content).TrackMastery("track.py"));
        Assert.Equal(expected.ToString(), meter.GetAttribute("aria-valuenow"));
        Assert.InRange(expected, 1, 99);
        Assert.Contains("1 of 2 lessons complete", cut.Markup);
        Assert.Contains("mastered", cut.Find(".card").TextContent);
    }

    [Fact]
    public void A_fresh_track_is_zero_percent_mastered()
    {
        var cut = Render<TracksView>(p => p.Add(c => c.Content, Content));

        Assert.Equal("0", cut.Find(".card [role=meter]").GetAttribute("aria-valuenow"));
    }

    [Fact]
    public void The_track_screen_lists_every_concept_with_its_level_and_a_bar()
    {
        Finish();
        Review("review.index", Rating.Good);

        var cut = Render<TrackView>(p => p.Add(c => c.Content, Content).Add(c => c.Track, Content.Tracks[0]));

        var rows = cut.FindAll("li.concept");
        Assert.Equal(["List indexing", "Slicing", "Bare idea", "A later idea"],
            rows.Select(r => r.QuerySelector(".concept-title")!.TextContent.Trim()));
        Assert.Equal(["Learning", "Seen", "Seen", "Not started"], rows.Select(r => r.QuerySelector(".level")!.TextContent.Trim()));
        Assert.All(rows, r => Assert.NotNull(r.QuerySelector("[role=meter]")));
        Assert.NotEqual("0", rows[0].QuerySelector("[role=meter]")!.GetAttribute("aria-valuenow"));
        Assert.Equal("0", rows[1].QuerySelector("[role=meter]")!.GetAttribute("aria-valuenow"));
    }

    [Fact]
    public void The_track_screen_shows_the_tracks_overall_mastery()
    {
        Finish();
        Review("review.index", Rating.Good);
        var expected = (int)Math.Round(100 * Recorder.Project(Content).TrackMastery("track.py"));

        var cut = Render<TrackView>(p => p.Add(c => c.Content, Content).Add(c => c.Track, Content.Tracks[0]));

        Assert.Equal(expected.ToString(), cut.Find(".track-mastery [role=meter]").GetAttribute("aria-valuenow"));
    }

    [Fact]
    public void Picking_a_concept_opens_it()
    {
        string? opened = null;
        var cut = Render<TrackView>(p => p.Add(c => c.Content, Content).Add(c => c.Track, Content.Tracks[0])
            .Add(c => c.OnOpenConcept, id => opened = id));

        cut.FindAll("li.concept button")[1].Click();

        Assert.Equal("concept.slice", opened);
    }

    // --- Concept detail ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_concept_screen_shows_what_it_is_where_it_was_taught_and_how_well_it_is_known()
    {
        Finish();
        Review("review.index", Rating.Good);

        var cut = OpenConcept("concept.index");

        Assert.Equal("List indexing", cut.Find("h1").TextContent.Trim());
        Assert.Contains("Learning", cut.Find(".concept-level").TextContent);
        Assert.Contains("Lists", cut.Find(".concept-lesson").TextContent);
        Assert.NotEqual("0", cut.Find(".concept-head [role=meter]").GetAttribute("aria-valuenow"));
    }

    [Fact]
    public void The_concept_screen_summarises_lessons_reviews_and_accuracy()
    {
        Recorder.StepAnswered("lesson.lists", "step.index", false, [1]);
        _clock.Now = T0.AddMinutes(1);
        Recorder.StepAnswered("lesson.lists", "step.index", true, [0]);
        Finish();
        _clock.Now = T0.AddDays(1);
        Review("review.index", Rating.Good);

        var stats = OpenConcept("concept.index").Find(".concept-stats").TextContent;

        Assert.Contains("1 of 2", stats.Replace("\n", " "));   // lesson: one right of two
        Assert.Contains("1 of 1", stats.Replace("\n", " "));   // review: one right of one
        Assert.Contains("67%", stats);                          // two right of three overall
    }

    [Fact]
    public void The_history_lists_each_answer_newest_first_with_its_question_and_where_it_happened()
    {
        Recorder.StepAnswered("lesson.lists", "step.index", false, [1]);
        Finish();
        _clock.Now = T0.AddDays(2);
        Review("review.index", Rating.Good);

        var rows = OpenConcept("concept.index").FindAll("li.attempt");

        Assert.Equal(2, rows.Count);
        Assert.Contains("Review", rows[0].TextContent);
        Assert.Contains("Question step.index", rows[0].TextContent);
        Assert.Contains("Oct 12", rows[0].TextContent);
        Assert.Contains("Correct", rows[0].TextContent);
        Assert.Contains("Lesson", rows[1].TextContent);
        Assert.Contains("Incorrect", rows[1].TextContent);
    }

    [Fact]
    public void A_concept_nobody_has_answered_yet_says_so_instead_of_showing_a_percentage()
    {
        Finish();

        var cut = OpenConcept("concept.bare");

        Assert.Contains("No answers yet", cut.Markup);
        Assert.DoesNotContain("%</", cut.Find(".concept-stats").InnerHtml.Replace("0%", ""));
        Assert.Empty(cut.FindAll("li.attempt"));
    }

    [Fact]
    public void The_concept_screen_lists_its_review_items_with_when_each_is_next_due()
    {
        Finish();
        Review("review.index", Rating.Good);

        var rows = OpenConcept("concept.index").FindAll("li.concept-item");

        Assert.Single(rows);
        Assert.Contains("Question step.index", rows[0].TextContent);
        Assert.Contains("Due Oct 13", rows[0].TextContent);    // first Good review: 3 days
    }

    [Fact]
    public void An_item_not_yet_reviewed_says_it_is_new()
    {
        Finish();

        var rows = OpenConcept("concept.slice").FindAll("li.concept-item");

        Assert.Contains("Not reviewed yet", rows[0].TextContent);
        Assert.Contains("Pattern", rows[0].TextContent);
    }

    [Fact]
    public void A_concept_with_no_review_items_explains_why_it_cannot_be_mastered_yet()
    {
        Finish();

        Assert.Contains("No review questions", OpenConcept("concept.bare").Markup);
    }

    [Fact]
    public void A_finished_lesson_can_be_revisited_from_the_concept()
    {
        Finish();
        (Lesson Lesson, bool Review)? opened = null;
        var cut = Render<ConceptView>(p => p.Add(c => c.Content, Content).Add(c => c.ConceptId, "concept.index")
            .Add(c => c.OnOpenLesson, t => opened = t));

        cut.Find("button.revisit").Click();

        Assert.Equal(("lesson.lists", true), (opened!.Value.Lesson.Id, opened.Value.Review));
    }

    [Fact]
    public void A_lesson_not_finished_is_offered_to_continue_not_to_review()
    {
        Recorder.CompleteStep(Lists, "step.intro");

        var cut = OpenConcept("concept.index");

        Assert.Equal("Continue lesson", cut.Find("button.revisit").TextContent.Trim());
        Assert.Contains("Not started", cut.Find(".concept-level").TextContent);
    }

    [Fact]
    public void A_locked_lesson_cannot_be_opened_from_its_concept()
    {
        var cut = OpenConcept("concept.later");

        Assert.True(cut.Find("button.revisit").HasAttribute("disabled"));
    }

    [Fact]
    public void Back_returns_to_the_track()
    {
        var back = 0;
        var cut = Render<ConceptView>(p => p.Add(c => c.Content, Content).Add(c => c.ConceptId, "concept.index")
            .Add(c => c.OnBack, () => back++));

        cut.Find(".crumbs button").Click();

        Assert.Equal(1, back);
    }

    [Fact]
    public void An_unknown_concept_shows_a_way_back_instead_of_crashing()
    {
        var cut = OpenConcept("concept.gone");

        Assert.Contains("no longer", cut.Markup);
        Assert.NotNull(cut.Find(".crumbs button"));
    }

    // --- Navigation ----------------------------------------------------------------------------------------------------

    private IRenderedComponent<CourseShell> OpenShell()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/shortcuts.js");
        return Render<CourseShell>(p => p.Add(c => c.Content, Content));
    }

    [Fact]
    public async Task A_concept_opens_from_its_track_and_back_climbs_to_the_track_and_then_the_tracks()
    {
        Finish();
        var cut = OpenShell();
        cut.Find("main .card").Click();
        cut.FindAll("main li.concept button")[0].Click();
        Assert.Equal("List indexing", cut.Find("main h1").TextContent.Trim());

        await Press(StepCommand.Back);
        Assert.Contains("lessons complete", cut.Find("main").TextContent);
        Assert.Equal("Python", cut.Find("main h1").TextContent.Trim());

        await Press(StepCommand.Back);
        Assert.Equal("Tracks", cut.Find("main h1").TextContent.Trim());
    }

    [Fact]
    public void Revisiting_the_lesson_from_a_concept_returns_to_the_concept_on_exit()
    {
        Finish();
        var cut = OpenShell();
        cut.Find("main .card").Click();
        cut.FindAll("main li.concept button")[0].Click();

        cut.Find("button.revisit").Click();
        Assert.Contains("Intro", cut.Find("main").TextContent);
        cut.Find("main .lesson-bar button.link").Click();

        Assert.Equal("List indexing", cut.Find("main h1").TextContent.Trim());
    }
}
