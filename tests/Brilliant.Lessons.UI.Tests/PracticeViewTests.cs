using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Practice;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>Practice UI (#36, slice 3): the card on Tracks, the pick-then-solve flow, its results, and <c>pick.</c> items in Review.</summary>
public class PracticeViewTests : ShortcutContext
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

    /// <summary>A lesson teaching pattern.{name} ("Pattern {name}") with one fill-in-the-blank problem tagged with it; the blank's answer is "x".</summary>
    private static Lesson Teaching(string name) =>
        new($"lesson.{name}", $"Lesson {name}", "track.t",
        [
            new FillBlankStep($"step.{name}.p", $"Use pattern {name} here", $"Solve problem {name}", "python", "y = {{b}}\n",
                [new Blank("b", ["x"], [])])
            {
                Pattern = $"pattern.{name}",
                PatternCue = $"The `{name}` in the prompt points to pattern {name}.",
                Hints = ["A nudge", "The pattern"],
            },
        ])
        {
            Patterns = [new Pattern($"pattern.{name}", $"Pattern {name}")],
        };

    private static readonly Lesson[] Lessons = [Teaching("a"), Teaching("b"), Teaching("c"), Teaching("d")];

    private static readonly ContentGraph Content =
        new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", Lessons.Select(l => l.Id).ToList())], Lessons);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private readonly ProgressRecorder _recorder;

    public PracticeViewTests()
    {
        _recorder = new ProgressRecorder(_log, "device", _clock);
        Services.AddSingleton(_recorder);
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/shortcuts.js");
    }

    private void Finish(int lessons)
    {
        foreach (var lesson in Lessons.Take(lessons))
        foreach (var s in lesson.Steps)
            _recorder.CompleteStep(lesson, s.Id);
    }

    private IRenderedComponent<PracticeView> Open(Action? onExit = null, int seed = 1) =>
        Render<PracticeView>(p => p.Add(c => c.Content, Content).Add(c => c.Random, new Random(seed)).Add(c => c.OnExit, () => onExit?.Invoke()));

    // The problem on screen, found from its prompt ("Solve problem a").
    private static string Shown(IRenderedComponent<PracticeView> cut) => cut.Find("p.prompt").TextContent.Trim()[^1..];

    private static void PickPattern(IRenderedComponent<PracticeView> cut, string title)
    {
        var index = cut.FindAll(".pattern-pick ul.options li").ToList().FindIndex(li => li.TextContent.Trim() == title);
        Assert.True(index >= 0, $"{title} is not offered");
        cut.FindAll(".pattern-pick ul.options input")[index].Change(true);
        cut.Find(".pattern-pick button.primary").Click();
    }

    private static string WrongPattern(IRenderedComponent<PracticeView> cut) =>
        cut.FindAll(".pattern-pick ul.options li").Select(li => li.TextContent.Trim()).First(t => t != "Pattern " + Shown(cut));

    private static void Solve(IRenderedComponent<PracticeView> cut, string answer)
    {
        cut.Find("input.blank").Input(answer);
        cut.Find(".fill-step button.primary").Click();
    }

    private IEnumerable<JsonElement> Practice => _log.Items
        .Where(e => e.Type == ProgressEventTypes.PracticeAnswered).Select(e => JsonDocument.Parse(e.Data!).RootElement);

    // --- The card on Tracks --------------------------------------------------------------------------------------------

    private IRenderedComponent<TracksView> Tracks(Action? onPractice = null) =>
        Render<TracksView>(p => p.Add(c => c.Content, Content).Add(c => c.OnOpenPractice, () => onPractice?.Invoke()));

    [Fact]
    public void The_card_is_disabled_with_a_reason_until_three_patterns_are_learned()
    {
        Finish(2);

        var card = Tracks().Find("button.practice-card");

        Assert.True(card.HasAttribute("disabled"));
        Assert.Equal("Finish lessons that teach 3 patterns to unlock Practice (2 so far).", card.QuerySelector(".practice-reason")!.TextContent.Trim());
    }

    [Fact]
    public void With_three_patterns_learned_the_card_opens_Practice_and_names_the_weakest_pattern()
    {
        Finish(3);
        var opened = false;
        var cut = Tracks(() => opened = true);

        var card = cut.Find("button.practice-card");
        Assert.False(card.HasAttribute("disabled"));
        Assert.Equal("Weakest: Pattern a: seen, 0%", card.QuerySelector(".practice-weakest")!.TextContent.Trim());

        card.Click();
        Assert.True(opened);
    }

    [Fact]
    public void Tracks_without_a_Practice_handler_has_no_card()
    {
        Finish(3);

        var cut = Render<TracksView>(p => p.Add(c => c.Content, Content));

        Assert.Empty(cut.FindAll(".practice-card"));
    }

    [Fact]
    public void From_Tracks_the_card_opens_Practice_without_the_bottom_tabs_and_Back_returns_to_Tracks()
    {
        Finish(3);
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));
        cut.Find("nav.phone-nav button:nth-child(2)").Click();   // Tracks

        cut.Find("button.practice-card").Click();
        Assert.Single(cut.FindAll(".practice-title"));
        Assert.Empty(cut.FindAll("nav.phone-nav"));

        cut.InvokeAsync(() => Press(StepCommand.Back)).Wait();
        Assert.Empty(cut.FindAll(".practice-title"));
        Assert.Contains("Tracks", cut.Find("nav.phone-nav [aria-current=page]").TextContent);

        cut.Find("button.practice-card").Click();
        cut.Find(".practice-bar button").Click();
        Assert.Contains("Tracks", cut.Find("nav.phone-nav [aria-current=page]").TextContent);
    }

    // --- Difficulty (#128) -------------------------------------------------------------------------------------------------

    // Four lessons whose problems are easy (fill-in-the-blank) and one whose problem is authored hard.
    private static readonly Lesson Hard = Teaching("h") is var h ? h with { Steps = [((FillBlankStep)h.Steps[0]) with { Difficulty = Difficulty.Hard }] } : null!;

    private static readonly ContentGraph Mixed =
        new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [.. Lessons.Select(l => l.Id), Hard.Id])], [.. Lessons, Hard]);

    private IRenderedComponent<PracticeView> OpenMixed()
    {
        Finish(3);
        foreach (var s in Hard.Steps) _recorder.CompleteStep(Hard, s.Id);
        return Render<PracticeView>(p => p.Add(c => c.Content, Mixed).Add(c => c.Random, new Random(1)));
    }

    [Fact]
    public void Practice_offers_each_difficulty_with_its_count_and_shows_the_problems_level()
    {
        var cut = OpenMixed();

        Assert.Equal(["Any", "Easy (3)", "Medium (0)", "Hard (1)"], cut.FindAll("select.practice-difficulty option").Select(o => o.TextContent.Trim()));
        Assert.Equal("any", cut.Find("select.practice-difficulty").GetAttribute("value"));
        Assert.Contains(cut.Find(".practice-level").TextContent.Trim(), new[] { "Easy", "Hard" });
    }

    [Fact]
    public void Choosing_a_difficulty_swaps_an_unanswered_problem_for_one_of_it()
    {
        var cut = OpenMixed();

        cut.Find("select.practice-difficulty").Change("hard");
        Assert.Equal(("h", "Hard"), (Shown(cut), cut.Find(".practice-level").TextContent.Trim()));

        cut.Find("select.practice-difficulty").Change("easy");
        Assert.NotEqual("h", Shown(cut));
        Assert.Equal("Easy", cut.Find(".practice-level").TextContent.Trim());
    }

    [Fact]
    public void Once_the_pattern_is_picked_a_new_difficulty_waits_for_the_next_problem()
    {
        var cut = OpenMixed();
        cut.Find("select.practice-difficulty").Change("easy");
        var shown = Shown(cut);
        PickPattern(cut, "Pattern " + shown);

        cut.Find("select.practice-difficulty").Change("hard");
        Assert.Equal(shown, Shown(cut));

        cut.Find(".pattern-pick button.primary").Click();   // Solve it
        Solve(cut, "x");
        cut.Find(".fill-step button.primary").Click();      // the result
        cut.Find(".practice-actions button.primary").Click();   // Next problem
        Assert.Equal("h", Shown(cut));
    }

    [Fact]
    public void A_difficulty_with_nothing_on_offer_says_so()
    {
        var cut = OpenMixed();

        cut.Find("select.practice-difficulty").Change("medium");

        Assert.Equal("No medium problems are on offer yet. Choose another difficulty, or finish more lessons.", cut.Find(".practice-none-at-level").TextContent.Trim());
        cut.Find("select.practice-difficulty").Change("any");
        Assert.Empty(cut.FindAll(".practice-none-at-level"));
    }

    // --- The streak and Today's suggestion (#128) ----------------------------------------------------------------------

    // One answered pick on the given day, for pattern a's problem.
    private void PractiseOn(int day)
    {
        _clock.Now = T0.AddDays(day);
        _recorder.PracticePicked("lesson.a", "step.a.p", "pattern.a", true, TimeSpan.FromSeconds(10), PracticeMode.Practice, $"run.{day}");
    }

    private IRenderedComponent<TodayView> Today(Action? onPractice = null) =>
        Render<TodayView>(p => p.Add(c => c.Content, Content).Add(c => c.OnOpenPractice, () => onPractice?.Invoke()));

    [Fact]
    public void The_Tracks_card_shows_the_practice_streak_and_asks_for_today_until_it_is_practised()
    {
        Finish(3);
        Assert.Empty(Tracks().FindAll(".practice-streak"));

        PractiseOn(0);
        PractiseOn(1);
        Assert.Equal("2-day practice streak", Tracks().Find(".practice-card .practice-streak").TextContent.Trim());

        _clock.Now = T0.AddDays(2);
        Assert.Equal("2-day practice streak · practise today to keep it", Tracks().Find(".practice-card .practice-streak").TextContent.Trim());
    }

    [Fact]
    public void With_reviews_done_Today_suggests_the_two_weakest_patterns_and_opens_Practice()
    {
        Finish(3);
        PractiseOn(0);
        var opened = false;

        var card = Today(() => opened = true).Find("button.today-practice");

        Assert.Equal("Reviews are done. Practise your weakest patterns: Pattern b and Pattern c.", card.QuerySelector(".card-meta")!.ChildNodes[0].TextContent.Trim());
        Assert.Equal("1-day practice streak", card.QuerySelector(".practice-streak")!.TextContent.Trim());
        card.Click();
        Assert.True(opened);
    }

    [Fact]
    public void Today_suggests_no_practice_while_reviews_are_due_before_Practice_opens_or_without_a_handler()
    {
        Finish(2);
        Assert.Empty(Today().FindAll(".today-practice"));                         // two patterns: Practice is locked

        Finish(3);
        Assert.Empty(Render<TodayView>(p => p.Add(c => c.Content, Content)).FindAll(".today-practice"));

        PractiseOn(0);
        _clock.Now = T0.AddDays(30);                                              // the pick item has come due
        Assert.Empty(Today().FindAll(".today-practice"));
    }

    [Fact]
    public void Practice_opened_from_Today_goes_back_to_Today()
    {
        Finish(3);
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));

        cut.Find("button.today-practice").Click();
        Assert.Single(cut.FindAll(".practice-title"));
        cut.InvokeAsync(() => Press(StepCommand.Back)).Wait();
        Assert.Contains("Today", cut.Find("nav.phone-nav [aria-current=page]").TextContent);

        cut.Find("button.today-practice").Click();
        cut.Find(".practice-bar button").Click();
        Assert.Contains("Today", cut.Find("nav.phone-nav [aria-current=page]").TextContent);
    }

    // --- The flow ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Before_three_patterns_the_screen_says_why_and_offers_nothing()
    {
        Finish(1);

        var cut = Open();

        Assert.Contains("(1 so far)", cut.Find(".practice-unavailable").TextContent);
        Assert.Empty(cut.FindAll(".pattern-pick"));
    }

    [Fact]
    public void The_problem_is_unlabelled_and_offers_every_learned_pattern_without_hints()
    {
        Finish(3);

        var cut = Open();
        var name = Shown(cut);

        Assert.Equal("Problem", cut.Find(".problem-prompt h2").TextContent.Trim());
        Assert.DoesNotContain($"Use pattern {name} here", cut.Markup);
        Assert.DoesNotContain($"Lesson {name}", cut.Markup);
        Assert.Equal(["Pattern a", "Pattern b", "Pattern c"], cut.FindAll(".pattern-pick ul.options li").Select(li => li.TextContent.Trim()).Order());
        Assert.Empty(cut.FindAll(".hint-ladder"));
        Assert.Empty(cut.FindAll("input.blank"));
    }

    [Fact]
    public void A_right_pick_is_recorded_with_the_pattern_chosen()
    {
        Finish(3);
        var cut = Open();
        var name = Shown(cut);
        _clock.Now += TimeSpan.FromSeconds(10);

        PickPattern(cut, "Pattern " + name);

        var pick = Assert.Single(Practice);
        Assert.Equal("pick", pick.GetProperty("part").GetString());
        Assert.True(pick.GetProperty("correct").GetBoolean());
        Assert.Equal($"pattern.{name}", pick.GetProperty("picked").GetString());
        Assert.Equal("practice", pick.GetProperty("mode").GetString());
        Assert.Equal((int)Rating.Easy, pick.GetProperty("rating").GetInt32());
        Assert.Contains($"Right: it's Pattern {name}.", cut.Find(".pattern-pick .verdict").TextContent);
        Assert.Contains($"points to pattern {name}", cut.Find(".pattern-pick .pattern-cue").TextContent);
    }

    [Fact]
    public void A_wrong_pick_is_told_the_right_pattern_and_still_goes_on_to_the_solve()
    {
        Finish(3);
        var cut = Open();
        var name = Shown(cut);
        var wrong = WrongPattern(cut);
        Assert.Empty(cut.FindAll(".pattern-cue"));

        PickPattern(cut, wrong);

        var pick = Assert.Single(Practice);
        Assert.False(pick.GetProperty("correct").GetBoolean());
        Assert.Equal("pattern." + wrong[^1..], pick.GetProperty("picked").GetString());
        Assert.Contains($"Not this time: it's Pattern {name}.", cut.Find(".pattern-pick .verdict").TextContent);
        Assert.Equal($"The {name} in the prompt points to pattern {name}.", cut.Find(".pattern-pick .pattern-cue").TextContent.Trim());
        Assert.NotNull(cut.Find(".pattern-pick .pattern-cue code"));
        Assert.Single(cut.FindAll(".pattern-pick ul.options li.right"));
        Assert.Single(cut.FindAll(".pattern-pick ul.options li.wrong"));
        Assert.All(cut.FindAll(".pattern-pick ul.options input"), i => Assert.True(i.HasAttribute("disabled")));

        cut.Find(".pattern-pick button.primary").Click();

        Assert.Equal("Problem", cut.Find(".fill-step h2").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll(".hint-ladder"));
    }

    [Fact]
    public void A_solve_is_recorded_once_with_the_hints_asked_for_before_it()
    {
        Finish(3);
        var cut = Open();
        var name = Shown(cut);
        PickPattern(cut, "Pattern " + name);
        cut.Find(".pattern-pick button.primary").Click();

        cut.Find(".hint-more").Click();
        Solve(cut, "nope");
        cut.Find(".fill-step button.primary").Click();   // try again
        cut.Find(".hint-more").Click();                  // after the first answer: not counted
        Solve(cut, "x");

        var solve = Assert.Single(Practice, p => p.GetProperty("part").GetString() == "solve");
        Assert.False(solve.GetProperty("correct").GetBoolean());
        Assert.Equal(1, solve.GetProperty("hintsUsed").GetInt32());
        var hint = Assert.Single(_log.Items, e => e.Type == ProgressEventTypes.HintUsed);
        Assert.Equal($"resolve.step.{name}.p", JsonDocument.Parse(hint.Data!).RootElement.GetProperty("item").GetString());
        Assert.Equal($"lesson.{name}", hint.LessonId);
    }

    [Fact]
    public void After_a_wrong_solve_the_learner_can_move_on_to_the_result()
    {
        Finish(3);
        var cut = Open();
        PickPattern(cut, WrongPattern(cut));
        cut.Find(".pattern-pick button.primary").Click();
        Assert.Empty(cut.FindAll(".practice-move-on"));

        Solve(cut, "nope");
        cut.Find(".practice-move-on").Click();

        Assert.Equal(["wrong", "wrong"], cut.FindAll(".practice-mark .sr-only").Select(s => s.TextContent));
    }

    [Fact]
    public void The_result_shows_both_marks_the_time_and_when_each_comes_back()
    {
        Finish(3);
        var cut = Open();
        var name = Shown(cut);
        _clock.Now += TimeSpan.FromSeconds(20);
        PickPattern(cut, "Pattern " + name);
        cut.Find(".pattern-pick button.primary").Click();
        _clock.Now += TimeSpan.FromSeconds(70);
        Solve(cut, "x");
        cut.Find(".fill-step button.primary").Click();

        Assert.Equal(["right", "right"], cut.FindAll(".practice-mark .sr-only").Select(s => s.TextContent));
        Assert.Contains($"Pattern {name}", cut.Find(".practice-result-pick").TextContent);
        Assert.Equal("Time: 1m 30s", cut.Find(".practice-time").TextContent.Trim());
        var next = cut.FindAll(".practice-next").Select(p => p.TextContent.Trim()).ToList();
        Assert.Equal(2, next.Count);
        Assert.StartsWith("The pattern comes back in Review in ", next[0]);
        Assert.StartsWith("The solution comes back in Review in ", next[1]);
    }

    [Fact]
    public void Next_problem_is_a_different_problem_in_the_same_run_and_Finish_leaves()
    {
        Finish(3);
        var left = false;
        var cut = Open(() => left = true);
        var first = Shown(cut);
        PickPattern(cut, "Pattern " + first);
        cut.Find(".pattern-pick button.primary").Click();
        Solve(cut, "x");
        cut.Find(".fill-step button.primary").Click();

        cut.Find(".practice-actions button.primary").Click();

        var second = Shown(cut);
        Assert.NotEqual(first, second);
        PickPattern(cut, "Pattern " + second);
        Assert.Single(Practice.Select(p => p.GetProperty("run").GetString()).Distinct());

        _clock.Now += TimeSpan.FromSeconds(1);
        cut.Find(".pattern-pick button.primary").Click();
        Solve(cut, "x");
        cut.Find(".fill-step button.primary").Click();
        cut.Find(".practice-finish").Click();
        Assert.True(left);
    }

    [Fact]
    public void Practice_leaves_lesson_progress_and_the_daily_count_alone()
    {
        Finish(3);
        var before = _recorder.Project(Content);
        var cut = Open();
        PickPattern(cut, "Pattern " + Shown(cut));
        cut.Find(".pattern-pick button.primary").Click();
        Solve(cut, "x");

        var after = _recorder.Project(Content);
        Assert.Equal(before.Tracks.Select(t => t.CompletedLessons), after.Tracks.Select(t => t.CompletedLessons));
        Assert.Equal(0, _log.Items.Count(e => e.Type is ProgressEventTypes.StepAnswered or ProgressEventTypes.ReviewAnswered));
    }

    [Fact]
    public void Shortcuts_choose_check_and_continue_the_pick()
    {
        Finish(3);
        var cut = Open();

        cut.InvokeAsync(() => Press(StepCommand.Choose, 2)).Wait();
        cut.InvokeAsync(() => Press(StepCommand.Advance)).Wait();
        Assert.Single(Practice);

        cut.InvokeAsync(() => Press(StepCommand.Advance)).Wait();
        Assert.NotEmpty(cut.FindAll(".fill-step"));
    }

    // --- Pick items in Review --------------------------------------------------------------------------------------------

    [Fact]
    public void A_due_pick_item_is_reviewed_as_the_unlabelled_problem_with_a_pattern_choice()
    {
        Finish(3);
        var practice = Open();
        var name = Shown(practice);
        PickPattern(practice, WrongPattern(practice));   // Again: due again within the day
        _clock.Now += TimeSpan.FromDays(2);

        var review = Render<ReviewView>(p => p.Add(c => c.Content, Content));
        Assert.Equal("Pattern", review.Find(".review-kind").TextContent.Trim());
        Assert.Equal("Problem", review.Find(".problem-prompt h2").TextContent.Trim());
        Assert.Empty(review.FindAll(".hint-ladder"));
        Assert.Empty(review.FindAll("input.blank"));
        Assert.Empty(review.FindAll(".pattern-cue"));

        _clock.Now += TimeSpan.FromSeconds(30);
        var index = review.FindAll(".pattern-pick ul.options li").ToList().FindIndex(li => li.TextContent.Trim() == "Pattern " + name);
        review.FindAll(".pattern-pick ul.options input")[index].Change(true);
        review.Find(".pattern-pick button.primary").Click();

        var answer = Assert.Single(_log.Items, e => e.Type == ProgressEventTypes.ReviewAnswered);
        var data = JsonDocument.Parse(answer.Data!).RootElement;
        Assert.Equal($"pick.step.{name}.p", data.GetProperty("item").GetString());
        Assert.True(data.GetProperty("correct").GetBoolean());
        Assert.Equal((int)RatingInference.InferPick(true, TimeSpan.FromSeconds(30)), data.GetProperty("rating").GetInt32());
        Assert.Contains("You'll see this again", review.Markup);
        Assert.Contains($"points to pattern {name}", review.Find(".pattern-pick .pattern-cue").TextContent);
    }

    // --- Practice-only problems (#127) ---------------------------------------------------------------------------------

    [Fact]
    public void A_practice_only_problem_is_offered_solved_and_recorded_against_its_lesson()
    {
        // Each lesson is a single explanation; its problem lives only in Practice.
        var lessons = Lessons.Select(l => l with { Steps = [new ExplainStep($"{l.Id}.intro", "Intro", "Read me", [], null)], Practice = l.Steps }).ToArray();
        var content = new ContentGraph(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", lessons.Select(l => l.Id).ToList())], lessons);
        foreach (var lesson in lessons.Take(3))
            _recorder.CompleteStep(lesson, lesson.Steps[0].Id);

        var cut = Render<PracticeView>(p => p.Add(c => c.Content, content).Add(c => c.Random, new Random(1)).Add(c => c.OnExit, () => { }));
        var name = Shown(cut);
        PickPattern(cut, "Pattern " + name);
        cut.Find(".pattern-pick button.primary").Click();
        Solve(cut, "x");

        var solve = Practice.Last();
        Assert.Equal(("solve", true), (solve.GetProperty("part").GetString(), solve.GetProperty("correct").GetBoolean()));
        var recorded = _log.Items.Last(e => e.Type == ProgressEventTypes.PracticeAnswered);
        Assert.Equal(($"lesson.{name}", $"step.{name}.p"), (recorded.LessonId, recorded.StepId));
        Assert.Contains(LearnerStateProjector.Project(_log.ReadAll(), content, _clock.Now).Reviews, r => r.Item.Id == $"resolve.step.{name}.p");
    }
}
