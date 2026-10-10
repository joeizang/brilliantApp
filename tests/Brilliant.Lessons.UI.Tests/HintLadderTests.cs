using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The hint ladder: revealed one rung at a time, recorded as HintUsed, and counted against the review rating.</summary>
public class HintLadderTests : ShortcutContext
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

    // --- The component -----------------------------------------------------------------------------------------------

    private IRenderedComponent<HintLadder> Ladder(IReadOnlyList<string> hints, List<int>? levels = null, string? caption = null) =>
        Render<HintLadder>(p => p.Add(c => c.Hints, hints).Add(c => c.OnRevealed, l => levels?.Add(l)).Add(c => c.Caption, caption));

    private static string[] Shown(IRenderedComponent<HintLadder> cut) => cut.FindAll(".hint-text").Select(t => t.TextContent.Trim()).ToArray();

    [Fact]
    public void A_step_without_hints_shows_no_ladder()
    {
        var cut = Ladder([]);

        Assert.Empty(cut.FindAll("section"));
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void Nothing_is_revealed_until_asked_for()
    {
        var levels = new List<int>();

        var cut = Ladder(["one", "two"], levels);

        Assert.Empty(Shown(cut));
        Assert.Equal("Need a hint?", cut.Find("button").TextContent.Trim());
        Assert.Empty(levels);
    }

    [Fact]
    public void Hints_are_revealed_one_level_at_a_time_in_order()
    {
        var levels = new List<int>();
        var cut = Ladder(["one", "two", "three"], levels);

        cut.Find("button").Click();
        Assert.Equal(["one"], Shown(cut));

        cut.Find("button").Click();
        Assert.Equal(["one", "two"], Shown(cut));
        Assert.Equal("Show the last hint", cut.Find("button").TextContent.Trim());

        cut.Find("button").Click();
        Assert.Equal(["one", "two", "three"], Shown(cut));
        Assert.Equal([1, 2, 3], levels);
    }

    [Fact]
    public void The_button_goes_away_when_every_hint_is_shown()
    {
        var cut = Ladder(["only"]);

        cut.Find("button").Click();

        Assert.Empty(cut.FindAll("button"));
        Assert.Equal(["only"], Shown(cut));
    }

    [Fact]
    public void A_full_four_rung_ladder_names_its_rungs()
    {
        var cut = Ladder(["a", "b", "c", "d"]);
        for (var i = 0; i < 4; i++) cut.Find("button").Click();

        Assert.Equal(["Nudge", "Pattern hint", "Partial code", "Full solution"], cut.FindAll(".hint-label").Select(l => l.TextContent.Trim()));
    }

    [Fact]
    public void A_shorter_ladder_numbers_its_hints_instead()
    {
        var cut = Ladder(["a", "b"]);
        cut.Find("button").Click();
        cut.Find("button").Click();

        Assert.Equal(["Hint 1 of 2", "Hint 2 of 2"], cut.FindAll(".hint-label").Select(l => l.TextContent.Trim()));
    }

    [Fact]
    public void Hints_are_rendered_as_markdown_and_never_as_raw_html()
    {
        var cut = Ladder(["Try `nums[-1]`", "<script>alert(1)</script>"]);
        cut.Find("button").Click();
        cut.Find("button").Click();

        Assert.Contains("<code>nums[-1]</code>", cut.Markup);
        Assert.DoesNotContain("<script>", cut.Markup);
    }

    [Fact]
    public void The_caption_shows_only_while_there_is_more_to_reveal()
    {
        var cut = Ladder(["a"], caption: "Costs a little");
        Assert.Contains("Costs a little", cut.Markup);

        cut.Find("button").Click();

        Assert.DoesNotContain("Costs a little", cut.Markup);
    }

    // --- In a lesson -------------------------------------------------------------------------------------------------

    private static ChoiceStep Q(string id, params string[] hints) =>
        new(id, "Question " + id, "Pick the right one", false, [new ChoiceOption("right", true, null), new ChoiceOption("wrong", false, null)]) { Hints = hints };

    private static readonly Lesson Lesson = new("lesson.l", "Lesson", "track.t",
        [
            new ExplainStep("step.intro", "Intro", "b", [], null),
            Q("step.q1", "nudge", "pattern", "partial", "solution"),
            Q("step.q2", "only hint"),
        ])
    {
        Concepts = [new Concept("concept.c", "C")],
        ReviewItems = [new ReviewItem("review.q1", "concept.c", "step.q1"), new ReviewItem("review.q2", "concept.c", "step.q2")],
    };

    private static readonly ContentGraph Content = new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [Lesson.Id])], [Lesson]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private readonly ProgressRecorder _recorder;

    public HintLadderTests()
    {
        _recorder = new ProgressRecorder(_log, "device", _clock);
        Services.AddSingleton(_recorder);
    }

    private IEnumerable<ProgressEvent> Hints => _log.Items.Where(e => e.Type == ProgressEventTypes.HintUsed);

    private IEnumerable<ProgressEvent> Answers => _log.Items.Where(e => e.Type == ProgressEventTypes.ReviewAnswered);

    private static JsonElement Data(ProgressEvent e) => JsonDocument.Parse(e.Data!).RootElement;

    private static void Reveal(IRenderedComponent<LessonViewer> cut, int times = 1)
    {
        for (var i = 0; i < times; i++) cut.Find("button.hint-more").Click();
    }

    [Fact]
    public void Revealing_a_hint_in_a_lesson_records_a_HintUsed_event_per_level()
    {
        _recorder.CompleteStep(Lesson, "step.intro");
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, Lesson));

        Reveal(cut, 2);

        Assert.Equal([1, 2], Hints.Select(e => Data(e).GetProperty("level").GetInt32()));
        Assert.All(Hints, e => Assert.Equal(("lesson.l", "step.q1"), (e.LessonId, e.StepId)));
        Assert.All(Hints, e => Assert.Equal(JsonValueKind.Null, Data(e).GetProperty("item").ValueKind));
    }

    [Fact]
    public void Replaying_a_lesson_in_review_mode_shows_hints_but_records_nothing()
    {
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, Lesson).Add(c => c.Review, true));
        cut.Find("button.primary").Click();   // past the intro

        Reveal(cut);

        Assert.Contains("nudge", cut.Markup);
        Assert.Empty(_log.Items);
    }

    [Fact]
    public void Each_step_starts_with_its_own_ladder_closed()
    {
        _recorder.CompleteStep(Lesson, "step.intro");
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, Lesson));
        Reveal(cut, 2);
        cut.Find("ul.options input").Change(true);
        cut.Find("button.primary").Click();   // Check
        cut.Find("button.primary").Click();   // Continue

        Assert.Contains("Question step.q2", cut.Markup);
        Assert.Empty(cut.FindAll(".hint-text"));
        Assert.Equal("Need a hint?", cut.Find("button.hint-more").TextContent.Trim());
    }

    // --- In Review ---------------------------------------------------------------------------------------------------

    private IRenderedComponent<ReviewView> OpenReview()
    {
        foreach (var s in Lesson.Steps) _recorder.CompleteStep(Lesson, s.Id);
        return Render<ReviewView>(p => p.Add(c => c.Content, Content));
    }

    private static void AnswerRight(IRenderedComponent<ReviewView> cut)
    {
        cut.FindAll("ul.options input")[0].Change(true);
        cut.Find("button.primary").Click();
    }

    private static void Reveal(IRenderedComponent<ReviewView> cut, int times)
    {
        for (var i = 0; i < times; i++) cut.Find("button.hint-more").Click();
    }

    [Theory]
    [InlineData(0, 4)]   // Easy
    [InlineData(1, 2)]   // Hard
    [InlineData(2, 2)]   // Hard
    [InlineData(3, 1)]   // Again
    public void Hints_used_in_review_lower_the_rating_stored_with_the_answer(int hints, int rating)
    {
        var cut = OpenReview();
        _clock.Now = T0.AddSeconds(2);
        Reveal(cut, hints);

        AnswerRight(cut);

        var answer = Assert.Single(Answers);
        Assert.Equal((hints, rating), (Data(answer).GetProperty("hintsUsed").GetInt32(), Data(answer).GetProperty("rating").GetInt32()));
    }

    [Fact]
    public void A_hint_asked_in_review_is_recorded_against_its_review_item()
    {
        var cut = OpenReview();

        Reveal(cut, 2);

        Assert.Equal([1, 2], Hints.Select(e => Data(e).GetProperty("level").GetInt32()));
        Assert.All(Hints, e => Assert.Equal(("review.q1", "step.q1"), (Data(e).GetProperty("item").GetString(), e.StepId)));
    }

    [Fact]
    public void Using_a_hint_brings_the_item_back_sooner_than_answering_unaided()
    {
        var cut = OpenReview();
        _clock.Now = T0.AddSeconds(2);
        Reveal(cut, 1);
        AnswerRight(cut);
        var withHint = _recorder.Project(Content).Reviews.Single(r => r.Item.Id == "review.q1").Card!.Due;

        cut.Find("button.primary").Click();   // Continue to q2
        AnswerRight(cut);                      // q2: no hint, same speed
        var unaided = _recorder.Project(Content).Reviews.Single(r => r.Item.Id == "review.q2").Card!.Due;

        Assert.True(withHint < unaided, $"hinted {withHint:u} should be before unaided {unaided:u}");
    }

    [Fact]
    public void The_next_item_starts_with_no_hints_counted()
    {
        var cut = OpenReview();
        Reveal(cut, 3);
        AnswerRight(cut);
        cut.Find("button.primary").Click();   // Continue to q2
        _clock.Now = T0.AddSeconds(2);

        AnswerRight(cut);

        var second = Answers.Last();
        Assert.Equal(("review.q2", 0), (Data(second).GetProperty("item").GetString(), Data(second).GetProperty("hintsUsed").GetInt32()));
    }

    [Fact]
    public void A_hint_asked_after_the_answer_neither_counts_nor_is_recorded()
    {
        var cut = OpenReview();
        _clock.Now = T0.AddSeconds(2);
        cut.FindAll("ul.options input")[1].Change(true);   // wrong first
        cut.Find("button.primary").Click();                 // Check
        Reveal(cut, 1);

        Assert.Empty(Hints);
        Assert.Equal(1, Data(Assert.Single(Answers)).GetProperty("rating").GetInt32());
        Assert.Equal(0, Data(Answers.Single()).GetProperty("hintsUsed").GetInt32());
    }

    [Fact]
    public void The_review_ladder_says_a_hint_has_a_cost()
    {
        var cut = OpenReview();

        Assert.Contains("A hint makes this come back sooner.", cut.Find(".hint-ladder").TextContent);
    }
}
