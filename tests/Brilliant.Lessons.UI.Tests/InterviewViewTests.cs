using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Drafts;
using Brilliant.Core.Practice;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>Interview UI (#36, slice 4): the card on Tracks, setup, the blind run against the clock, resume, and the summary.</summary>
public class InterviewViewTests : ShortcutContext
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        private readonly object _gate = new();
        private readonly List<ProgressEvent> _items = [];
        public List<ProgressEvent> Items { get { lock (_gate) return [.. _items]; } }
        public bool Append(ProgressEvent e) { lock (_gate) _items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) { var items = Items; return new(items.Skip((int)cursor).ToList(), items.Count); }
    }

    private sealed class MemoryDrafts : ICodeDraftStore
    {
        public Dictionary<string, string> Saved { get; } = [];
        public string? Get(string stepId) => Saved.GetValueOrDefault(stepId);
        public void Save(string stepId, string code) => Saved[stepId] = code;
        public void Reset(string stepId) => Saved.Remove(stepId);
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
    private readonly MemoryDrafts _drafts = new();

    public InterviewViewTests()
    {
        _recorder = new ProgressRecorder(_log, "device", _clock);
        Services.AddSingleton(_recorder);
        Services.AddSingleton<ICodeDraftStore>(_drafts);
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/shortcuts.js");
    }

    private void Finish(int lessons)
    {
        foreach (var lesson in Lessons.Take(lessons))
        foreach (var s in lesson.Steps)
            _recorder.CompleteStep(lesson, s.Id);
    }

    private IRenderedComponent<InterviewView> Open(Action? onExit = null, TimeSpan? tick = null) =>
        Render<InterviewView>(p => p.Add(c => c.Content, Content).Add(c => c.Random, new Random(1))
            .Add(c => c.Tick, tick ?? TimeSpan.FromHours(1)).Add(c => c.OnExit, () => onExit?.Invoke()));

    // Opens the screen and starts a run of the preset ("Phone screen" or "Interview").
    private IRenderedComponent<InterviewView> Start(string preset = "Phone screen", TimeSpan? tick = null)
    {
        var cut = Open(tick: tick);
        cut.FindAll("button.interview-preset").First(b => b.TextContent.Contains(preset)).Click();
        cut.Find("button.interview-start").Click();
        return cut;
    }

    // The problem on screen, found from its prompt ("Solve problem a").
    private static string Shown(IRenderedComponent<InterviewView> cut) => cut.Find("p.prompt").TextContent.Trim()[^1..];

    private static void PickPattern(IRenderedComponent<InterviewView> cut, string title)
    {
        var index = cut.FindAll(".pattern-pick ul.options li").ToList().FindIndex(li => li.TextContent.Trim() == title);
        Assert.True(index >= 0, $"{title} is not offered");
        cut.FindAll(".pattern-pick ul.options input")[index].Change(true);
        cut.Find(".pattern-pick button.primary").Click();
    }

    private static string WrongPattern(IRenderedComponent<InterviewView> cut) =>
        cut.FindAll(".pattern-pick ul.options li").Select(li => li.TextContent.Trim()).First(t => t != "Pattern " + Shown(cut));

    private static void Solve(IRenderedComponent<InterviewView> cut, string answer)
    {
        cut.Find("input.blank").Input(answer);
        cut.Find(".fill-step button.primary").Click();
    }

    private IEnumerable<JsonElement> Answers => _log.Items
        .Where(e => e.Type == ProgressEventTypes.PracticeAnswered).Select(e => JsonDocument.Parse(e.Data!).RootElement);

    private InterviewRun Run => InterviewRuns.From(_log.Items).Single();

    // --- Setup ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Before_three_patterns_the_screen_says_why_and_offers_no_presets()
    {
        Finish(2);
        var exited = false;
        var cut = Open(() => exited = true);

        Assert.Equal("Finish lessons that teach 3 patterns to unlock Practice (2 so far).", cut.Find(".practice-unavailable").TextContent.Trim());
        Assert.Empty(cut.FindAll("button.interview-preset"));
        cut.Find(".practice-actions button").Click();
        Assert.True(exited);
    }

    [Fact]
    public void A_preset_asks_for_confirmation_and_only_Start_starts_the_clock()
    {
        Finish(3);
        var cut = Open();

        var presets = cut.FindAll("button.interview-preset");
        Assert.Equal(3, presets.Count);
        Assert.Equal("Phone screen", presets[0].QuerySelector(".card-title")!.TextContent.Trim());
        Assert.Equal("2 problems, 30 minutes", presets[0].QuerySelector(".card-meta")!.TextContent.Trim());
        Assert.Equal("Interview", presets[1].QuerySelector(".card-title")!.TextContent.Trim());
        Assert.Equal("3 problems, 45 minutes", presets[1].QuerySelector(".card-meta")!.TextContent.Trim());
        Assert.Equal("Custom", presets[2].QuerySelector(".card-title")!.TextContent.Trim());

        cut.FindAll("button.interview-preset")[1].Click();
        Assert.Contains("The clock starts now and can't be paused.", cut.Find(".interview-confirm").TextContent);
        cut.Find(".interview-confirm button.secondary").Click();
        Assert.Empty(cut.FindAll(".interview-confirm"));
        Assert.DoesNotContain(_log.Items, e => e.Type == ProgressEventTypes.InterviewStarted);

        cut.FindAll("button.interview-preset")[1].Click();
        cut.Find("button.interview-start").Click();

        Assert.Equal(InterviewPreset.Interview, Run.Preset);
        Assert.Equal(3, Run.Problems.Count);
        Assert.Equal("45:00", cut.Find(".interview-clock").TextContent.Trim());
        Assert.Equal(3, cut.FindAll("button.interview-tab").Count);
    }

    // --- Custom runs and the budget (#128) -----------------------------------------------------------------------------

    [Fact]
    public void A_custom_run_asks_for_the_problem_count_and_minutes_then_starts_with_them()
    {
        Finish(4);
        var cut = Open();
        cut.FindAll("button.interview-preset").Single(b => b.TextContent.Contains("Custom")).Click();

        // Only as many problems as there are to draw from (four lessons, one problem each).
        Assert.Equal(["1", "2", "3", "4"], cut.FindAll("select.custom-problems option").Select(o => o.TextContent));
        Assert.Equal("10", cut.FindAll("select.custom-minutes option").First().TextContent);
        Assert.Equal("90", cut.FindAll("select.custom-minutes option").Last().TextContent);

        cut.Find("select.custom-problems").Change("4");
        cut.Find("select.custom-minutes").Change("20");
        Assert.Contains("Custom: 4 problems, 20 minutes.", cut.Find("#interview-confirm-text").TextContent);
        cut.Find("button.interview-start").Click();

        Assert.Equal((InterviewPreset.Custom, 4, TimeSpan.FromMinutes(20)), (Run.Preset, Run.Problems.Count, Run.Limit));
        Assert.Equal("20:00", cut.Find(".interview-clock").TextContent.Trim());
        Assert.Equal("0:00 of 5:00", cut.Find(".interview-budget").TextContent.Trim());
    }

    [Fact]
    public void A_custom_run_of_one_problem_says_so()
    {
        Finish(3);
        var cut = Open();
        cut.FindAll("button.interview-preset").Single(b => b.TextContent.Contains("Custom")).Click();
        cut.Find("select.custom-problems").Change("1");

        Assert.Contains("Custom: 1 problem, 45 minutes.", cut.Find("#interview-confirm-text").TextContent);
    }

    [Fact]
    public void The_budget_counts_time_on_the_problem_on_screen_and_says_when_it_is_over()
    {
        Finish(3);
        var cut = Start();   // Phone screen: 2 problems, 15 minutes each
        string Budget() => cut.Find(".interview-budget").TextContent.Trim();
        Assert.Equal("0:00 of 15:00", Budget());

        _clock.Now = T0.AddMinutes(4);
        cut.FindAll("button.interview-tab")[1].Click();   // problem 1 keeps its 4 minutes; problem 2 starts from zero
        Assert.Equal("0:00 of 15:00", Budget());

        _clock.Now = T0.AddMinutes(20);
        cut.FindAll("button.interview-tab")[1].Click();   // redraws, as the clock's tick does
        Assert.Equal("16:00 of 15:00 (over budget)", Budget());
        Assert.Contains("over", cut.Find(".interview-budget").ClassList);

        cut.FindAll("button.interview-tab")[0].Click();
        Assert.Equal("4:00 of 15:00", Budget());
        Assert.DoesNotContain("over", cut.Find(".interview-budget").ClassList);
    }

    [Fact]
    public void The_summary_says_when_a_problem_went_over_its_budget()
    {
        Finish(3);
        var cut = Start();
        _clock.Now = T0.AddMinutes(16);
        PickPattern(cut, "Pattern " + Shown(cut));
        cut.Find("button.interview-finish").Click();
        cut.Find(".interview-finish-confirm button.primary").Click();

        Assert.Equal("Time: 16m 00s of a 15m 00s budget (over)", cut.FindAll(".result-time")[0].TextContent.Trim());
    }

    [Fact]
    public void Finishing_before_any_answer_keeps_the_time_spent()
    {
        Finish(3);
        var cut = Start();
        _clock.Now = T0.AddMinutes(16);
        cut.Render();   // as the clock's tick does
        Assert.Equal("16:00 of 15:00 (over budget)", cut.Find(".interview-budget").TextContent.Trim());

        cut.Find("button.interview-finish").Click();
        cut.Find(".interview-finish-confirm button.primary").Click();

        var times = cut.FindAll(".result-time").Select(t => t.TextContent.Trim()).ToList();
        Assert.Equal(["Time: 16m 00s of a 15m 00s budget (over)", "Time: none"], times);
    }

    [Fact]
    public async Task Leaving_and_reopening_a_run_keeps_each_problems_time_but_not_the_time_away()
    {
        Finish(3);
        var cut = Start();
        _clock.Now = T0.AddMinutes(4);
        cut.FindAll("button.interview-tab")[1].Click();
        _clock.Now = T0.AddMinutes(6);
        await DisposeComponentsAsync();   // left with problem 2 on screen: 2 minutes on it

        _clock.Now = T0.AddMinutes(10);
        var again = Open();
        Assert.Equal("4:00 of 15:00", again.Find(".interview-budget").TextContent.Trim());   // back on the first problem not submitted
        again.FindAll("button.interview-tab")[1].Click();
        Assert.Equal("2:00 of 15:00", again.Find(".interview-budget").TextContent.Trim());
    }

    [Fact]
    public void Time_on_screen_is_recorded_every_so_often_so_closing_the_app_loses_little()
    {
        Finish(3);
        var cut = Start(tick: TimeSpan.FromMilliseconds(20));
        _clock.Now = T0.AddMinutes(3);

        cut.WaitForAssertion(() => Assert.Equal(TimeSpan.FromMinutes(3), Run.TimeOn(Run.Problems[0].StepId)));
    }

    [Fact]
    public void Time_after_the_clock_ran_out_does_not_count()
    {
        Finish(3);
        var cut = Start();
        _clock.Now = T0.AddMinutes(45);   // the phone screen ended at 30
        PickPattern(cut, "Pattern " + Shown(cut));

        Assert.Equal("Time: 30m 00s of a 15m 00s budget (over)", cut.FindAll(".result-time")[0].TextContent.Trim());
    }

    // --- The run -------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_pick_is_locked_in_without_a_verdict_and_recorded_as_an_interview_answer()
    {
        Finish(3);
        var cut = Start();

        Assert.Equal("Problem", cut.Find("h2").TextContent.Trim());
        Assert.Equal("Lock in", cut.Find(".pattern-pick button.primary").TextContent.Trim());
        var shown = Shown(cut);
        var wrong = WrongPattern(cut);
        _clock.Now = T0.AddSeconds(40);
        PickPattern(cut, wrong);

        var pick = Assert.Single(Answers);
        Assert.Equal("interview", pick.GetProperty("mode").GetString());
        Assert.Equal(Run.Id, pick.GetProperty("run").GetString());
        Assert.False(pick.GetProperty("correct").GetBoolean());
        Assert.Equal(40_000, pick.GetProperty("elapsedMs").GetInt64());

        // Straight on to the solve: no verdict, no cue, no hints, and the pick shown as made.
        Assert.Empty(cut.FindAll(".verdict"));
        Assert.Empty(cut.FindAll(".pattern-cue"));
        Assert.DoesNotContain("Hint", cut.Markup);
        Assert.Contains(wrong, cut.Find(".pick-locked").TextContent);
        Assert.Equal("Submit", cut.Find(".fill-step button.primary").TextContent.Trim());
        Assert.Equal(shown, Shown(cut));
    }

    [Fact]
    public void A_submitted_solution_is_recorded_without_a_verdict_and_the_run_moves_to_the_next_problem()
    {
        Finish(3);
        var cut = Start();
        var first = Shown(cut);
        PickPattern(cut, "Pattern " + first);
        _clock.Now = T0.AddMinutes(2);
        Solve(cut, "wrong");

        var solve = Answers.Single(a => a.GetProperty("part").GetString() == "solve");
        Assert.False(solve.GetProperty("correct").GetBoolean());
        Assert.Empty(cut.FindAll(".verdict"));
        Assert.NotEqual(first, Shown(cut));
        Assert.Contains("done", cut.FindAll("button.interview-tab")[0].ClassName);

        // Going back shows it as submitted, with nothing left to do on it.
        cut.FindAll("button.interview-tab")[0].Click();
        Assert.Equal("Solution submitted.", cut.Find(".solve-submitted").TextContent.Trim());
        Assert.Empty(cut.FindAll("input.blank"));
    }

    [Fact]
    public void Work_not_yet_submitted_is_kept_when_switching_problems_and_after_leaving()
    {
        Finish(3);
        var cut = Start();
        var first = Shown(cut);
        PickPattern(cut, "Pattern " + first);
        cut.Find("input.blank").Input("keep this work");

        cut.FindAll("button.interview-tab")[1].Click();
        cut.FindAll("button.interview-tab")[0].Click();
        Assert.Equal("keep this work", cut.Find("input.blank").GetAttribute("value"));

        var resumed = Open();
        Assert.Equal(first, Shown(resumed));
        Assert.Equal("keep this work", resumed.Find("input.blank").GetAttribute("value"));
        Assert.Equal($"interview.{Run.Id}.step.{first}.p.blanks", Assert.Single(_drafts.Saved).Key);
    }

    [Fact]
    public void Submitting_the_last_problem_ends_the_run_with_the_summary()
    {
        Finish(3);
        var cut = Start();

        for (var i = 0; i < 2; i++)
        {
            PickPattern(cut, "Pattern " + Shown(cut));
            Solve(cut, "x");
        }

        Assert.True(Run.IsFinished);
        Assert.Single(cut.FindAll(".interview-summary"));
        Assert.Equal(4, Answers.Count());
    }

    [Fact]
    public void The_clock_turns_urgent_in_the_last_five_minutes()
    {
        Finish(3);
        var cut = Start(tick: TimeSpan.FromMilliseconds(20));
        Assert.DoesNotContain("urgent", cut.Find(".interview-clock").ClassName);

        _clock.Now = T0.AddMinutes(25).AddSeconds(30);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("4:30", cut.Find(".interview-clock").TextContent.Trim());
            Assert.Contains("urgent", cut.Find(".interview-clock").ClassName);
        });
    }

    [Fact]
    public void When_time_runs_out_the_unanswered_parts_are_recorded_and_the_summary_shown()
    {
        Finish(3);
        var cut = Start(tick: TimeSpan.FromMilliseconds(20));
        PickPattern(cut, "Pattern " + Shown(cut));

        _clock.Now = T0.AddMinutes(30);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".interview-summary")));
        Assert.True(Run.IsFinished);
        Assert.Equal(4, Answers.Count());   // the pick made, then three parts unanswered
        Assert.Equal(3, Answers.Count(a => !a.GetProperty("correct").GetBoolean()));
    }

    [Fact]
    public void Finish_early_asks_first_then_ends_the_run()
    {
        Finish(3);
        var cut = Start();

        cut.Find("button.interview-finish").Click();
        Assert.Contains("counts as unanswered", cut.Find(".interview-finish-confirm").TextContent);
        cut.Find(".interview-finish-confirm button.secondary").Click();
        Assert.Empty(cut.FindAll(".interview-finish-confirm"));
        Assert.False(Run.IsFinished);

        cut.Find("button.interview-finish").Click();
        cut.Find(".interview-finish-confirm button.primary").Click();

        Assert.True(Run.IsFinished);
        Assert.Equal(4, Answers.Count());
        Assert.Single(cut.FindAll(".interview-summary"));
    }

    // --- Resume --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Leaving_and_coming_back_resumes_the_run_where_it_stood_with_the_clock_still_running()
    {
        Finish(3);
        var cut = Start();
        var first = Shown(cut);
        PickPattern(cut, "Pattern " + first);
        cut.Find(".practice-bar button").Click();

        _clock.Now = T0.AddMinutes(10);
        var resumed = Open();

        Assert.Equal("20:00", resumed.Find(".interview-clock").TextContent.Trim());
        Assert.Equal(first, Shown(resumed));
        Assert.Contains("Pattern " + first, resumed.Find(".pick-locked").TextContent);
        Assert.Empty(resumed.FindAll(".pattern-pick"));

        // The second problem offers the same patterns, in the same order, as when the run started.
        resumed.FindAll("button.interview-tab")[1].Click();
        var stored = Run.Problems[1].Choices.Select(id => Content.TryGet<Pattern>(id, out var p) ? p.Title : id);
        Assert.Equal(stored, resumed.FindAll(".pattern-pick ul.options li").Select(li => li.TextContent.Trim()));
    }

    [Fact]
    public void Coming_back_after_the_time_is_up_finishes_the_run_and_shows_the_summary()
    {
        Finish(3);
        Start();

        _clock.Now = T0.AddHours(2);
        var cut = Open();

        Assert.Single(cut.FindAll(".interview-summary"));
        Assert.True(Run.IsFinished);
        Assert.Equal(4, Answers.Count());
    }

    // --- The summary ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_summary_names_each_pattern_the_pick_the_solution_and_the_time_then_the_weakest_pattern()
    {
        Finish(3);
        var cut = Start();
        var first = Shown(cut);
        _clock.Now = T0.AddSeconds(30);
        PickPattern(cut, "Pattern " + first);
        _clock.Now = T0.AddSeconds(90);
        Solve(cut, "x");
        var second = Shown(cut);
        var wrong = WrongPattern(cut);
        PickPattern(cut, wrong);
        cut.Find("button.interview-finish").Click();
        cut.Find(".interview-finish-confirm button.primary").Click();

        var results = cut.FindAll(".interview-result");
        Assert.Equal(2, results.Count);

        Assert.Contains($"It was Pattern {first}. You picked it.", results[0].QuerySelector(".result-pick")!.TextContent);
        Assert.Contains("The solution passed.", results[0].QuerySelector(".result-solve")!.TextContent);
        Assert.Equal("Time: 1m 30s of a 15m 00s budget", results[0].QuerySelector(".result-time")!.TextContent.Trim());

        Assert.Contains($"It was Pattern {second}. You picked {wrong}.", results[1].QuerySelector(".result-pick")!.TextContent);
        Assert.Contains("You didn't submit a solution.", results[1].QuerySelector(".result-solve")!.TextContent);

        Assert.StartsWith($"Weakest pattern this run: Pattern {second}", cut.Find(".interview-weakest").TextContent.Trim());
        Assert.Equal("Your answers are now in Review. What you didn't answer doesn't count against you.", cut.Find(".interview-review").TextContent.Trim());
    }

    [Fact]
    public void A_problem_never_picked_says_no_answer()
    {
        Finish(3);
        var cut = Start();
        cut.Find("button.interview-finish").Click();
        cut.Find(".interview-finish-confirm button.primary").Click();

        Assert.All(cut.FindAll(".result-pick"), p => Assert.Contains("You gave no answer.", p.TextContent));
        Assert.All(cut.FindAll(".result-time"), p => Assert.Equal("Time: none", p.TextContent.Trim()));
        Assert.All(cut.FindAll(".result-solve"), p => Assert.Contains("You didn't submit a solution.", p.TextContent));
        Assert.Equal("Nothing was answered, so Review is unchanged.", cut.Find(".interview-review").TextContent.Trim());
    }

    // --- The card on Tracks --------------------------------------------------------------------------------------------

    private IRenderedComponent<TracksView> Tracks(Action? onInterview = null) =>
        Render<TracksView>(p => p.Add(c => c.Content, Content).Add(c => c.OnOpenInterview, () => onInterview?.Invoke()));

    [Fact]
    public void The_card_is_disabled_with_Practices_reason_until_Practice_opens()
    {
        Finish(2);

        var card = Tracks().Find("button.interview-card");

        Assert.True(card.HasAttribute("disabled"));
        Assert.Equal("Finish lessons that teach 3 patterns to unlock Practice (2 so far).", card.QuerySelector(".interview-status")!.TextContent.Trim());
    }

    [Fact]
    public void The_card_says_what_a_run_is_then_how_long_is_left_then_that_time_is_up()
    {
        Finish(3);
        var opened = false;
        var card = Tracks(() => opened = true).Find("button.interview-card");
        Assert.False(card.HasAttribute("disabled"));
        Assert.Equal("1 to 5 problems against one clock.", card.QuerySelector(".interview-status")!.TextContent.Trim());
        card.Click();
        Assert.True(opened);

        _recorder.StartInterview(InterviewPreset.PhoneScreen, PracticeSelector.Set(_recorder.Project(Content), new Random(1), 2));
        _clock.Now = T0.AddMinutes(12).AddSeconds(20);
        Assert.Equal("In progress: 18 min left.", Tracks().Find(".interview-status").TextContent.Trim());

        _clock.Now = T0.AddMinutes(31);
        Assert.Equal("Time's up. See how you did.", Tracks().Find(".interview-status").TextContent.Trim());
    }

    [Fact]
    public void Tracks_without_an_interview_handler_has_no_card()
    {
        Finish(3);

        Assert.Empty(Render<TracksView>(p => p.Add(c => c.Content, Content)).FindAll("button.interview-card"));
    }

    [Fact]
    public void From_Tracks_the_card_opens_the_interview_and_Back_returns_to_Tracks()
    {
        Finish(3);
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));
        cut.Find("nav.phone-nav button:nth-child(2)").Click();   // Tracks

        cut.Find("button.interview-card").Click();
        Assert.Single(cut.FindAll(".interview-title"));
        Assert.Empty(cut.FindAll("nav.phone-nav"));

        cut.InvokeAsync(() => Press(StepCommand.Back)).Wait();
        Assert.Empty(cut.FindAll(".interview-title"));

        // Practice and the interview are separate screens.
        cut.Find("button.practice-card").Click();
        Assert.Single(cut.FindAll(".practice-title"));
        Assert.Empty(cut.FindAll(".interview-title"));
    }
}
