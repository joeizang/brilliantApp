using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The interleave step (#162): step the threads by hand, undo, and submit a schedule that breaks the invariant.</summary>
public sealed class InterleaveStepViewTests : ShortcutContext
{
    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private readonly MemoryLog _log = new();

    public InterleaveStepViewTests() => Services.AddSingleton(new ProgressRecorder(_log, "device"));

    private static readonly InterleaveStep Counter = new(
        "step.interleave.counter", "Lose an update", "Make counter end at 1.",
        [new("counter", 0)],
        [
            new("A", [new("var t = counter;", "t = counter"), new("counter = t + 1;", "counter = t + 1")]),
            new("B", [new("var t = counter;", "t = counter"), new("counter = t + 1;", "counter = t + 1")]),
        ],
        new(InterleaveInvariant.FinalValue, "counter is 2 once both threads finish", "counter", 2),
        ["A", "B", "A", "B"],
        [new("One after the other", ["A", "A", "B", "B"])],
        "Both threads read 0 before either wrote.");

    private static readonly InterleaveStep Locks = new(
        "step.interleave.locks", "Deadlock", "Make both wait forever.",
        [],
        [
            new("A", [new("lock (first) {", "lock first"), new("lock (second) {", "lock second"), new("}", "unlock second"), new("}", "unlock first")]),
            new("B", [new("lock (second) {", "lock second"), new("lock (first) {", "lock first"), new("}", "unlock first"), new("}", "unlock second")]),
        ],
        new(InterleaveInvariant.NoDeadlock, "both threads always finish"),
        ["A", "B"], [], "Opposite lock orders.");

    private IRenderedComponent<InterleaveStepView> Show(InterleaveStep step, Action<InterleaveStepView.Attempt>? answered = null, Action? continued = null) =>
        Render<InterleaveStepView>(p =>
        {
            p.Add(c => c.Step, step);
            if (answered is not null) p.Add(c => c.OnAnswered, answered);
            if (continued is not null) p.Add(c => c.OnContinue, continued);
        });

    private static void Run(IRenderedComponent<InterleaveStepView> cut, params string[] threads)
    {
        foreach (var name in threads) cut.Find($"button[aria-label='Run next line of thread {name}']").Click();
    }

    private static string Value(IRenderedComponent<InterleaveStepView> cut, string scope = "play", string name = "counter") =>
        cut.Find($".board[data-scope='{scope}'] .shared-var[data-var='{name}'] .value").TextContent;

    [Fact]
    public void It_shows_every_thread_side_by_side_with_the_shared_state_and_the_rule()
    {
        var cut = Show(Counter);

        Assert.Equal(["Thread A", "Thread B"], cut.FindAll(".thread h4").Select(h => h.TextContent));
        Assert.Equal(2, cut.Find("[data-thread='A'] .ops").Children.Length);
        Assert.Equal("0", Value(cut));
        Assert.Contains("counter is 2 once both threads finish", cut.Find(".invariant").TextContent);
        Assert.Equal("next", cut.Find("[data-thread='A'] li").GetAttribute("data-state"));
    }

    [Fact]
    public void Running_a_thread_takes_one_line_and_shows_what_it_did()
    {
        var cut = Show(Counter);

        Run(cut, "A");
        Assert.Contains("t = 0", cut.Find("[data-thread='A'] .locals").TextContent);
        Assert.Equal("0", Value(cut));
        Assert.Equal("done", cut.Find("[data-thread='A'] li").GetAttribute("data-state"));
        Run(cut, "A");

        Assert.Equal("1", Value(cut));
        Assert.Equal(["A", "A"], cut.FindAll(".schedule-item").Select(i => i.TextContent));
        Assert.Contains("Finished", cut.Find("[data-thread='A'] .status").TextContent);
        Assert.True(cut.Find("button[aria-label='Run next line of thread A']").HasAttribute("disabled"));
        Assert.False(cut.Find("button[aria-label='Run next line of thread B']").HasAttribute("disabled"));
    }

    [Fact]
    public void Undo_takes_back_the_last_line_and_start_over_clears_everything()
    {
        var cut = Show(Counter);
        Run(cut, "A", "A");

        cut.FindAll("button").First(b => b.TextContent == "Undo").Click();
        Assert.Equal("0", Value(cut));
        Assert.Equal(["A"], cut.FindAll(".schedule-item").Select(i => i.TextContent));

        cut.FindAll("button").First(b => b.TextContent == "Start over").Click();
        Assert.Empty(cut.FindAll(".schedule-item"));
        Assert.True(cut.FindAll("button").First(b => b.TextContent == "Undo").HasAttribute("disabled"));
    }

    [Fact]
    public void Check_waits_until_every_thread_has_finished()
    {
        var cut = Show(Counter);
        var check = () => cut.Find("button.primary");

        Run(cut, "A", "B", "A");
        Assert.True(check().HasAttribute("disabled"));
        Run(cut, "B");

        Assert.False(check().HasAttribute("disabled"));
    }

    [Fact]
    public void A_lost_update_is_accepted_and_reported_with_its_schedule()
    {
        InterleaveStepView.Attempt? seen = null;
        var continued = false;
        var cut = Show(Counter, a => seen = a, () => continued = true);

        Run(cut, "A", "B", "A", "B");
        Assert.Equal("1", Value(cut));
        cut.Find("button.primary").Click();

        Assert.Contains("You broke it", cut.Find(".verdict").TextContent);
        Assert.Contains("read 0 before either wrote", cut.Find(".explanation").TextContent);
        Assert.True(seen!.Correct);
        Assert.Equal(["A", "B", "A", "B"], seen.Schedule);
        Assert.Empty(cut.FindAll("button[aria-label^='Run next line']"));
        cut.Find("button.primary").Click();
        Assert.True(continued);
    }

    [Fact]
    public void An_order_that_keeps_the_invariant_says_so_and_can_be_undone_for_another_go()
    {
        InterleaveStepView.Attempt? seen = null;
        var cut = Show(Counter, a => seen = a);

        Run(cut, "A", "A", "B", "B");
        cut.Find("button.primary").Click();

        Assert.Contains("Not quite", cut.Find(".verdict").TextContent);
        Assert.Contains("invariant held", cut.Find(".verdict").TextContent);
        Assert.False(seen!.Correct);

        cut.Find("button.primary").Click();   // Try again
        Assert.Empty(cut.FindAll(".verdict"));
        Assert.Equal(4, cut.FindAll(".schedule-item").Count);
        cut.FindAll("button").First(b => b.TextContent == "Undo").Click();
        Assert.False(cut.Find("button[aria-label='Run next line of thread B']").HasAttribute("disabled"));
    }

    [Fact]
    public void A_thread_waiting_for_a_lock_cannot_be_run_and_a_deadlock_can_be_submitted()
    {
        InterleaveStepView.Attempt? seen = null;
        var cut = Show(Locks, a => seen = a);

        Run(cut, "A");
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
        Run(cut, "B");

        Assert.True(cut.Find("button[aria-label='Run next line of thread A']").HasAttribute("disabled"));
        Assert.True(cut.Find("button[aria-label='Run next line of thread B']").HasAttribute("disabled"));
        Assert.Contains("Waiting for second, held by B", cut.Find("[data-thread='A'] .status").TextContent);
        Assert.Contains("held by A", cut.Find(".shared-lock[data-lock='first']").TextContent);
        Assert.Contains("Nothing can run", cut.Find(".stuck").TextContent);
        cut.Find("button.primary").Click();

        Assert.True(seen!.Correct);
        Assert.Contains("You broke it", cut.Find(".verdict").TextContent);
    }

    [Fact]
    public void A_replay_steps_forward_and_back_and_shows_the_shared_state_at_each_line()
    {
        var cut = Show(Counter);

        cut.Find(".replay-choices button").Click();
        Assert.Contains("Before anything runs", cut.Find(".replay-caption").TextContent);
        Assert.Equal("0", Value(cut, "replay"));

        cut.Find(".replay-next").Click();
        Assert.Contains("thread A ran var t = counter;", cut.Find(".replay-caption").TextContent);
        cut.Find(".replay-next").Click();
        Assert.Equal("1", Value(cut, "replay"));
        Assert.Contains("counter = t + 1;", cut.Find(".replay-caption").TextContent);

        cut.Find(".replay-back").Click();
        Assert.Equal("0", Value(cut, "replay"));
        cut.Find(".replay-next").Click();
        cut.Find(".replay-reset").Click();
        Assert.Equal("0", Value(cut, "replay"));
        Assert.Equal(0, cut.FindAll(".board[data-scope='replay'] li[data-state='done']").Count);
        // Replaying never touches the learner's own run.
        Assert.Empty(cut.FindAll(".schedule-item"));
    }

    [Fact]
    public void The_breaking_order_is_offered_as_a_replay_only_once_the_learner_has_broken_it()
    {
        var cut = Show(Counter);
        Assert.DoesNotContain("The break", cut.Find(".replays").TextContent);

        Run(cut, "A", "B", "A", "B");
        cut.Find("button.primary").Click();

        var choices = cut.FindAll(".replay-choices button").Select(b => b.TextContent).ToArray();
        Assert.Equal(["One after the other", "The break"], choices);
        cut.FindAll(".replay-choices button")[1].Click();
        for (var i = 0; i < 4; i++) cut.Find(".replay-next").Click();
        Assert.Equal("1", Value(cut, "replay"));
    }

    [Fact]
    public async Task Keys_run_a_thread_undo_and_check()
    {
        InterleaveStepView.Attempt? seen = null;
        var cut = Show(Counter, a => seen = a);

        await Press(StepCommand.Choose, 1);
        await Press(StepCommand.Choose, 2);
        await Press(StepCommand.Choose, 3);   // there is no third thread
        Assert.Equal(["A", "B"], cut.FindAll(".schedule-item").Select(i => i.TextContent));
        await Press(StepCommand.Previous);
        Assert.Equal(["A"], cut.FindAll(".schedule-item").Select(i => i.TextContent));
        await Press(StepCommand.Advance);     // not finished: nothing to check yet
        Assert.Null(seen);

        foreach (var key in new[] { 2, 1, 2 }) await Press(StepCommand.Choose, key);
        await Press(StepCommand.Run);

        Assert.True(seen!.Correct);
    }

    [Fact]
    public void On_a_phone_the_threads_have_big_buttons_and_the_keyboard_hint_is_left_out()
    {
        Screen.Set(ScreenClass.Phone);

        var cut = Show(Counter);

        Assert.DoesNotContain("Press 1", cut.Find(".hint-text").TextContent);
        Assert.All(cut.FindAll("button.run-thread"), b => Assert.Contains("coarse:min-h-(--touch)", b.ClassName));
        Assert.Contains("sm:grid-cols-2", cut.Find(".board > div:last-child").ClassName);
    }

    [Fact]
    public void A_new_step_starts_from_scratch()
    {
        var cut = Show(Counter);
        Run(cut, "A");

        cut.Render(p => p.Add(c => c.Step, Locks));

        Assert.Empty(cut.FindAll(".schedule-item"));
        Assert.Equal(["Thread A", "Thread B"], cut.FindAll(".thread h4").Select(h => h.TextContent));
        Assert.Contains("free", cut.Find(".shared-lock[data-lock='first']").TextContent);
    }

    private static Lesson LessonWith(InterleaveStep step) => new("lesson.i", "Lesson", "track.t", [step])
    {
        Concepts = [new Concept("concept.c", "C")],
        ReviewItems = [new ReviewItem("review.i", "concept.c", step.Id)],
    };

    [Fact]
    public void Answering_in_a_lesson_records_the_schedule_but_not_when_replaying_it()
    {
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, LessonWith(Counter)));
        Run(cut.FindComponent<InterleaveStepView>(), "A", "B", "A", "B");
        cut.Find("button.primary").Click();

        var recorded = Assert.Single(_log.Items, e => e.Type == ProgressEventTypes.StepAnswered && e.StepId == Counter.Id);
        Assert.Contains("\"schedule\":[\"A\",\"B\",\"A\",\"B\"]", recorded.Data);
        Assert.Contains("\"correct\":true", recorded.Data);

        _log.Items.Clear();
        var replay = Render<LessonViewer>(p => p.Add(c => c.Lesson, LessonWith(Counter)).Add(c => c.Review, true));
        Run(replay.FindComponent<InterleaveStepView>(), "A", "A", "B", "B");
        replay.Find("button.primary").Click();
        Assert.DoesNotContain(_log.Items, e => e.Type == ProgressEventTypes.StepAnswered);
    }

    [Fact]
    public void Review_asks_an_interleave_question_instead_of_skipping_it()
    {
        var lesson = LessonWith(Counter);
        var content = new ContentGraph(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [lesson.Id])], [lesson]);
        Services.GetRequiredService<ProgressRecorder>().CompleteStep(lesson, Counter.Id);

        var cut = Render<ReviewView>(p => p.Add(c => c.Content, content));

        Assert.DoesNotContain("can't be reviewed", cut.Markup);
        Assert.Equal(2, cut.FindAll(".thread").Count);
    }

    [Fact]
    public void A_practice_style_prompt_shows_the_interleave_question()
    {
        var cut = Render<ProblemPrompt>(p => p.Add(c => c.Step, Counter));

        Assert.Equal("Make counter end at 1.", cut.Find(".prompt").TextContent);
    }
}
