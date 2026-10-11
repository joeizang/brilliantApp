using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>
/// What changes on a phone: the "best on a larger screen" note and the tap-friendly alternative of a step, the form Review asks
/// questions in, the bottom tabs, and touch dragging in Parsons. Whatever the form, progress is recorded against the step itself.
/// </summary>
public class PhoneLayoutTests : ShortcutContext
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static ParsonsStep Arrange(string id) => new(id, "Arrange", "Put these in order", "python", [new("x = 1", 0), new("y = 2", 0)]);

    private static ChoiceStep Choice(string id, bool large, Step? alternative = null) =>
        new(id, "Question " + id, "Pick one", false, [new ChoiceOption("right", true, null), new ChoiceOption("wrong", false, null)])
        {
            LargeScreenPreferred = large,
            PhoneAlternative = alternative,
        };

    private static readonly Lesson Heavy = new("lesson.h", "Heavy", "track.t", [Choice("step.heavy", true, Arrange("step.heavy.arrange"))])
    {
        Concepts = [new Concept("concept.h", "H")],
        ReviewItems = [new ReviewItem("review.heavy", "concept.h", "step.heavy")],
    };

    private static readonly Lesson Stuck = new("lesson.s", "Stuck", "track.t", [Choice("step.stuck", true)])
    {
        Concepts = [new Concept("concept.s", "S")],
        ReviewItems = [new ReviewItem("review.stuck", "concept.s", "step.stuck")],
    };

    private static ContentGraph Graph(params Lesson[] lessons) =>
        new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", lessons.Select(l => l.Id).ToList())], lessons);

    private readonly MemoryLog _log = new();
    private readonly ProgressRecorder _recorder;

    public PhoneLayoutTests()
    {
        _recorder = new ProgressRecorder(_log, "device", new ManualTimeProvider());
        Services.AddSingleton(_recorder);
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/shortcuts.js");
    }

    private IRenderedComponent<LessonViewer> OpenLesson(Lesson lesson) => Render<LessonViewer>(p => p.Add(c => c.Lesson, lesson));

    private IEnumerable<ProgressEvent> Of(string type) => _log.Items.Where(e => e.Type == type);

    // Puts a two-line Parsons board in order using the move buttons, then checks it.
    private static void SolveArrange(IRenderedComponent<LessonViewer> cut)
    {
        if (cut.FindAll(".row .text")[0].TextContent != "x = 1") cut.FindAll(".row")[1].QuerySelector("button[aria-label='Move up']")!.Click();
        cut.Find("button.primary").Click();
    }

    // ---- lesson steps

    [Fact]
    public void On_a_desktop_a_flagged_step_has_no_note()
    {
        var cut = OpenLesson(Heavy);

        Assert.Empty(cut.FindAll(".large-screen-note"));
        Assert.NotEmpty(cut.FindAll("ul.options"));
    }

    [Fact]
    public void On_a_phone_a_flagged_step_says_it_is_best_on_a_larger_screen_and_offers_the_alternative()
    {
        Screen.Set(ScreenClass.Phone);
        var cut = OpenLesson(Heavy);

        Assert.Contains("best on a larger screen", cut.Find(".large-screen-note").TextContent);
        Assert.Contains("Try the arrange-the-lines version", cut.Find(".large-screen-note button").TextContent);
        Assert.NotEmpty(cut.FindAll("ul.options"));   // the step itself is still what is shown until they choose
    }

    [Fact]
    public void A_flagged_step_without_an_alternative_says_so_and_offers_nothing_to_tap()
    {
        Screen.Set(ScreenClass.Phone);
        var cut = OpenLesson(Stuck);

        Assert.Contains("best on a larger screen", cut.Find(".large-screen-note").TextContent);
        Assert.Empty(cut.FindAll(".large-screen-note button"));
    }

    [Fact]
    public void An_unflagged_step_is_untouched_on_a_phone()
    {
        Screen.Set(ScreenClass.Phone);
        var cut = OpenLesson(new Lesson("lesson.p", "Plain", "track.t", [Choice("step.plain", false)]));

        Assert.Empty(cut.FindAll(".large-screen-note"));
    }

    [Fact]
    public void Choosing_the_alternative_swaps_the_step_and_Switch_back_restores_it()
    {
        Screen.Set(ScreenClass.Phone);
        var cut = OpenLesson(Heavy);

        cut.Find(".large-screen-note button").Click();
        Assert.NotEmpty(cut.FindAll(".parsons-step"));
        Assert.Empty(cut.FindAll("ul.options"));
        Assert.Contains("tap-friendly", cut.Find(".large-screen-note").TextContent);

        cut.Find(".large-screen-note button").Click();   // Switch back
        Assert.Empty(cut.FindAll(".parsons-step"));
        Assert.NotEmpty(cut.FindAll("ul.options"));
    }

    [Fact]
    public void The_alternative_is_recorded_and_completed_as_the_step_it_stands_in_for()
    {
        Screen.Set(ScreenClass.Phone);
        var cut = OpenLesson(Heavy);
        cut.Find(".large-screen-note button").Click();

        SolveArrange(cut);
        var answered = Assert.Single(Of(ProgressEventTypes.StepAnswered));
        Assert.Equal("step.heavy", answered.StepId);

        cut.Find("button.primary").Click();   // Continue
        var completed = Assert.Single(Of(ProgressEventTypes.StepCompleted));
        Assert.Equal("step.heavy", completed.StepId);
        Assert.DoesNotContain(_log.Items, e => e.StepId == "step.heavy.arrange");
    }

    [Fact]
    public void Growing_the_window_while_the_alternative_is_open_returns_to_the_step()
    {
        Screen.Set(ScreenClass.Phone);
        var cut = OpenLesson(Heavy);
        cut.Find(".large-screen-note button").Click();

        cut.InvokeAsync(() => Screen.Set(ScreenClass.Desktop));

        Assert.Empty(cut.FindAll(".parsons-step"));
        Assert.Empty(cut.FindAll(".large-screen-note"));
    }

    // ---- review

    [Fact]
    public void Review_asks_the_phone_form_on_a_phone_and_records_the_answer_against_the_same_item()
    {
        foreach (var s in Heavy.Steps) _recorder.CompleteStep(Heavy, s.Id);
        Screen.Set(ScreenClass.Phone);
        var cut = Render<ReviewView>(p => p.Add(c => c.Content, Graph(Heavy)));

        Assert.NotEmpty(cut.FindAll(".parsons-step"));
        Assert.Empty(cut.FindAll("ul.options"));
        Assert.Empty(cut.FindAll(".large-screen-note"));   // it has a phone form, so nothing to apologise for

        cut.FindAll(".row")[1].QuerySelector("button[aria-label='Move up']")!.Click();
        if (cut.FindAll(".row .text")[0].TextContent != "x = 1") cut.FindAll(".row")[1].QuerySelector("button[aria-label='Move up']")!.Click();
        cut.Find("button.primary").Click();

        var answer = Assert.Single(Of(ProgressEventTypes.ReviewAnswered));
        Assert.Contains("review.heavy", answer.Data);
    }

    [Fact]
    public void Review_asks_the_original_on_a_desktop()
    {
        foreach (var s in Heavy.Steps) _recorder.CompleteStep(Heavy, s.Id);
        var cut = Render<ReviewView>(p => p.Add(c => c.Content, Graph(Heavy)));

        Assert.NotEmpty(cut.FindAll("ul.options"));
        Assert.Empty(cut.FindAll(".parsons-step"));
    }

    [Fact]
    public void Review_notes_a_question_that_has_no_phone_form()
    {
        foreach (var s in Stuck.Steps) _recorder.CompleteStep(Stuck, s.Id);
        Screen.Set(ScreenClass.Phone);
        var cut = Render<ReviewView>(p => p.Add(c => c.Content, Graph(Stuck)));

        Assert.Contains("best on a larger screen", cut.Find(".large-screen-note").TextContent);
        Assert.NotEmpty(cut.FindAll("ul.options"));   // still answerable
    }

    // ---- bottom tabs

    [Fact]
    public void The_bottom_tabs_show_on_Today_and_Tracks_with_the_due_count_and_hide_in_a_lesson()
    {
        foreach (var s in Heavy.Steps) _recorder.CompleteStep(Heavy, s.Id);
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Graph(Heavy)));

        var tabs = cut.Find("nav.phone-nav");
        var buttons = tabs.QuerySelectorAll("button");
        Assert.Equal(4, buttons.Length);
        Assert.Contains("Today", buttons[0].TextContent);
        Assert.Contains("Tracks", buttons[1].TextContent);
        Assert.Equal("Review", buttons[2].QuerySelector("span:not([aria-hidden]):not(.sr-only)")!.TextContent);
        Assert.DoesNotContain("@", buttons[2].TextContent);   // raw Razor once leaked into this label
        Assert.DoesNotContain("{", buttons[2].TextContent);
        Assert.Equal("(1 due)", buttons[2].QuerySelector(".sr-only")!.TextContent);
        Assert.Contains("Sync", buttons[3].TextContent);
        Assert.Equal("1", tabs.QuerySelector(".due-count")!.TextContent);
        Assert.Contains("Today", tabs.QuerySelector("[aria-current=page]")!.TextContent);

        tabs.QuerySelectorAll("button")[1].Click();   // Tracks
        Assert.Contains("Tracks", cut.Find("nav.phone-nav [aria-current=page]").TextContent);

        cut.Find("nav.phone-nav button:nth-child(3)").Click();   // Review
        Assert.Empty(cut.FindAll("nav.phone-nav"));
    }

    // ---- Parsons by touch

    private IRenderedComponent<ParsonsStepView> OpenParsons(Action<ParsonsStepView.Attempt>? onAnswered = null) =>
        Render<ParsonsStepView>(p => p
            .Add(c => c.Step, new ParsonsStep("step.p", "P", "Order", "python", [new("a = 1", 0), new("b = 2", 0), new("c = 3", 0)]))
            .Add(c => c.OnAnswered, a => onAnswered?.Invoke(a)));

    [Fact]
    public void Parsons_starts_the_touch_drag_module_on_its_list()
    {
        var module = JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/parsons-touch.js");
        module.SetupVoid("attach", _ => true);

        OpenParsons();

        module.VerifyInvoke("attach");
    }

    [Fact]
    public void Each_row_knows_its_index_for_the_touch_drag_and_the_grip_is_there_to_grab()
    {
        var cut = OpenParsons();

        Assert.Equal(["0", "1", "2"], cut.FindAll(".rows > li").Select(li => li.GetAttribute("data-row")!).ToArray());
        Assert.Equal(3, cut.FindAll(".rows .grip").Count);
        Assert.Equal("false", cut.Find(".rows").GetAttribute("data-locked"));
    }

    [Fact]
    public async Task A_finger_dropping_a_line_on_another_moves_it_there()
    {
        var cut = OpenParsons();
        var before = cut.FindAll(".row .text").Select(t => t.TextContent).ToArray();

        await cut.InvokeAsync(() => cut.Instance.MoveTo(0, 2));

        var after = cut.FindAll(".row .text").Select(t => t.TextContent).ToArray();
        Assert.Equal([before[1], before[2], before[0]], after);
    }

    [Fact]
    public async Task A_late_or_out_of_range_touch_move_is_ignored()
    {
        var cut = OpenParsons();
        var before = cut.FindAll(".row .text").Select(t => t.TextContent).ToArray();

        await cut.InvokeAsync(() => cut.Instance.MoveTo(0, 9));
        await cut.InvokeAsync(() => cut.Instance.MoveTo(-1, 1));
        Assert.Equal(before, cut.FindAll(".row .text").Select(t => t.TextContent).ToArray());

        cut.Find("button.primary").Click();   // Check: the board is locked from now on
        var checkedOrder = cut.FindAll(".row .text").Select(t => t.TextContent).ToArray();
        Assert.Equal("true", cut.Find(".rows").GetAttribute("data-locked"));
        await cut.InvokeAsync(() => cut.Instance.MoveTo(0, 1));
        Assert.Equal(checkedOrder, cut.FindAll(".row .text").Select(t => t.TextContent).ToArray());
    }

    [Fact]
    public void Parsons_help_talks_about_the_handle_on_a_phone_and_keys_elsewhere()
    {
        Assert.Contains("⌥", OpenParsons().Find(".hint-text").TextContent);

        Screen.Set(ScreenClass.Phone);
        var phone = OpenParsons().Find(".hint-text").TextContent;
        Assert.Contains("handle", phone);
        Assert.DoesNotContain("⌥", phone);
    }

    // ---- the screen class itself

    [Fact]
    public void ScreenInfo_reports_changes_once_and_understands_the_page_names()
    {
        var info = new ScreenInfo();
        var changes = 0;
        info.Changed += () => changes++;

        info.OnScreenChanged("phone");
        info.OnScreenChanged("phone");
        Assert.Equal(ScreenClass.Phone, info.Class);
        Assert.True(info.IsPhone);
        Assert.Equal(1, changes);

        info.OnScreenChanged("tablet");
        Assert.Equal(ScreenClass.Tablet, info.Class);
        info.OnScreenChanged("nonsense");
        Assert.Equal(ScreenClass.Desktop, info.Class);
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task ScreenInfo_follows_the_page_through_its_module_and_stops_when_disposed()
    {
        var module = JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/screen.js");
        module.Setup<string>("attach", _ => true).SetResult("phone");
        module.SetupVoid("detach", _ => true).SetVoidResult();
        var info = new ScreenInfo();

        var watch = await info.WatchAsync(JSInterop.JSRuntime);

        Assert.NotNull(watch);
        Assert.Equal(ScreenClass.Phone, info.Class);
        await watch!.DisposeAsync();
        module.VerifyInvoke("detach");
    }

    [Fact]
    public async Task ScreenInfo_stays_on_the_desktop_if_the_page_cannot_tell_it()
    {
        var module = JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/screen.js");
        module.Setup<string>("attach", _ => true).SetException(new JSException("no matchMedia"));
        var info = new ScreenInfo();

        Assert.Null(await info.WatchAsync(JSInterop.JSRuntime));
        Assert.Equal(ScreenClass.Desktop, info.Class);
    }
}
