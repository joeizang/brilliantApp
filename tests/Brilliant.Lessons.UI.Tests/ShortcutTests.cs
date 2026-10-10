using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>Keyboard commands on the step views not covered by their own test classes, on the lesson screen, and on the shell.</summary>
public class ShortcutTests : ShortcutContext
{
    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private readonly MemoryLog _log = new();

    private static ExplainStep Explain(string id) => new(id, "Title " + id, "Body", [], null);

    private static Lesson TwoExplains(string id, string trackId) => new(id, "Lesson " + id, trackId, [Explain(id + ".1"), Explain(id + ".2")]);

    private void AddRecorder() => Services.AddSingleton(new ProgressRecorder(_log, "device"));

    // ---- predict-output

    private static readonly PredictOutputStep Typed = new("step.t", "T", "What prints?", new CodeSnippet("python", "print(1)"), ["1"], [], []);

    private static readonly PredictOutputStep MultipleChoice = new("step.mc", "M", "What prints?", new CodeSnippet("python", "print(1)"), [], [],
        [new ChoiceOption("0", false, null), new ChoiceOption("1", true, null), new ChoiceOption("2", false, null)]);

    [Fact]
    public async Task Number_keys_pick_a_multiple_choice_prediction_and_Return_checks_it()
    {
        var answers = new List<bool>();
        var cut = Render<PredictOutputStepView>(p => p.Add(c => c.Step, MultipleChoice).Add(c => c.OnAnswered, a => answers.Add(a.Result.IsCorrect)));

        await Press(StepCommand.Choose, 2);
        Assert.Single(cut.FindAll("li.option.selected"));
        await Press(StepCommand.Advance);

        Assert.Equal([true], answers);
        Assert.Contains("Correct", cut.Markup);
    }

    [Fact]
    public async Task Number_keys_do_not_pick_anything_in_a_typed_prediction_but_command_return_checks_it()
    {
        var answers = new List<bool>();
        var cut = Render<PredictOutputStepView>(p => p.Add(c => c.Step, Typed).Add(c => c.OnAnswered, a => answers.Add(a.Result.IsCorrect)));

        await Press(StepCommand.Choose, 1);
        await Press(StepCommand.Run);                      // empty box: nothing to check
        Assert.Empty(answers);

        cut.Find("textarea").Input("1");
        await Press(StepCommand.Run);
        Assert.Equal([true], answers);
    }

    // ---- fill-blank and parsons

    [Fact]
    public async Task Return_checks_and_continues_a_fill_blank_step()
    {
        var step = new FillBlankStep("step.f", "F", "Fill", "python", "x = {{v}}\n", [new Blank("v", ["1"], [])]);
        var continued = 0;
        var cut = Render<FillBlankStepView>(p => p.Add(c => c.Step, step).Add(c => c.OnContinue, () => continued++));

        await Press(StepCommand.Advance);                  // blank is empty: not checkable yet
        Assert.Empty(cut.FindAll(".verdict"));

        cut.Find("input.blank").Input("2");
        await Press(StepCommand.Advance);
        Assert.Contains("isn't right", cut.Find(".verdict").TextContent);

        await Press(StepCommand.Advance);                  // try again keeps the text
        cut.Find("input.blank").Input("1");
        await Press(StepCommand.Advance);
        await Press(StepCommand.Advance);
        Assert.Equal(1, continued);
    }

    [Fact]
    public async Task Return_checks_a_parsons_step()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var step = new ParsonsStep("step.p", "P", "Order", "python", [new("x = 1", 0), new("y = 2", 0)]);
        var answers = new List<bool>();
        var cut = Render<ParsonsStepView>(p => p.Add(c => c.Step, step).Add(c => c.OnAnswered, a => answers.Add(a.Result.IsCorrect)));

        await Press(StepCommand.Run);
        Assert.Single(answers);
        Assert.Single(cut.FindAll(".verdict"));
    }

    // ---- lesson screen

    [Fact]
    public async Task Return_moves_on_from_an_explanation_and_then_off_the_finished_lesson()
    {
        AddRecorder();
        var lesson = TwoExplains("lesson.a", "track.a");
        var exits = 0;
        var progress = 0;
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, lesson)
            .Add(c => c.OnExit, () => exits++).Add(c => c.OnProgress, () => progress++));

        Assert.Contains("Title lesson.a.1", cut.Markup);
        await Press(StepCommand.Advance);
        Assert.Contains("Title lesson.a.2", cut.Markup);
        await Press(StepCommand.Advance);
        Assert.Contains("You've finished this lesson", cut.Markup);
        Assert.Equal(0, exits);
        Assert.Equal(2, progress);

        await Press(StepCommand.Advance);
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task Review_mode_steps_through_with_Return_and_records_nothing()
    {
        AddRecorder();
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, TwoExplains("lesson.a", "track.a")).Add(c => c.Review, true));

        await Press(StepCommand.Advance);
        await Press(StepCommand.Advance);

        Assert.Contains("That's the whole lesson", cut.Markup);
        Assert.Empty(_log.Items);
    }

    [Fact]
    public async Task Return_on_a_question_is_left_to_the_question()
    {
        AddRecorder();
        var lesson = new Lesson("lesson.q", "Q", "track.a", [new ChoiceStep("step.q", "Q", "Which?", false, [new("a", true, null), new("b", false, null)])]);
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, lesson));

        await Press(StepCommand.Advance);                  // nothing picked: stays put
        Assert.Contains("Which?", cut.Markup);
        Assert.Empty(_log.Items);

        await Press(StepCommand.Choose, 1);
        await Press(StepCommand.Advance);
        await Press(StepCommand.Advance);
        Assert.Contains("You've finished this lesson", cut.Markup);
    }

    // ---- shell: back and sidebar

    private IRenderedComponent<CourseShell> OpenShell()
    {
        AddRecorder();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/shortcuts.js");
        var graph = new ContentGraph(new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "Track A", ["lesson.a", "lesson.b"])],
            [TwoExplains("lesson.a", "track.a"), TwoExplains("lesson.b", "track.a")]);
        return Render<CourseShell>(p => p.Add(c => c.Content, graph));
    }

    [Fact]
    public async Task Back_climbs_one_level_at_a_time_and_stops_at_Today()
    {
        var cut = OpenShell();
        cut.Find("button.browse").Click();
        cut.Find("main .card").Click();                              // the track
        cut.Find("main ol.lessons button.primary").Click();          // lesson A
        Assert.Contains("Title lesson.a.1", cut.Find("main").TextContent);

        await Press(StepCommand.Back);
        Assert.Contains("lessons complete", cut.Find("main").TextContent);
        Assert.Empty(cut.FindAll("main .lesson-title"));

        await Press(StepCommand.Back);
        Assert.Equal("Tracks", cut.Find("main h1").TextContent);

        await Press(StepCommand.Back);
        Assert.Equal("Today", cut.Find("main h1").TextContent.Trim());

        await Press(StepCommand.Back);                               // already at the top
        Assert.Equal("Today", cut.Find("main h1").TextContent.Trim());
    }

    [Fact]
    public void The_sidebar_lists_tracks_and_lessons_and_locks_what_is_not_open_yet()
    {
        var cut = OpenShell();

        Assert.Equal("Track A", cut.Find("aside .side-track-title").TextContent.Trim().Split('\n')[0].Trim());
        var lessons = cut.FindAll("aside .side-lesson");
        Assert.Equal(2, lessons.Count);
        Assert.False(lessons[0].HasAttribute("disabled"));
        Assert.True(lessons[1].HasAttribute("disabled"));
    }

    [Fact]
    public void Picking_a_lesson_in_the_sidebar_opens_it_and_marks_it_current()
    {
        var cut = OpenShell();

        cut.FindAll("aside .side-lesson")[0].Click();

        Assert.Contains("Title lesson.a.1", cut.Find("main").TextContent);
        Assert.Contains("current", cut.FindAll("aside .side-lesson")[0].ClassList);
        Assert.Contains("current", cut.Find("aside .side-track-title").ClassList);
    }

    [Fact]
    public async Task Finishing_a_lesson_step_refreshes_the_sidebar()
    {
        var cut = OpenShell();
        cut.FindAll("aside .side-lesson")[0].Click();
        Assert.True(cut.FindAll("aside .side-lesson")[1].HasAttribute("disabled"));

        await Press(StepCommand.Advance);
        await Press(StepCommand.Advance);

        Assert.False(cut.FindAll("aside .side-lesson")[1].HasAttribute("disabled"));
        Assert.Contains("completed", cut.FindAll("aside .side-lesson")[0].ClassList);
    }

    [Fact]
    public async Task The_page_listener_maps_its_commands_onto_the_bus()
    {
        var cut = OpenShell();
        var seen = new List<StepShortcut>();
        Shortcuts.Subscribe(s => { seen.Add(s); return Task.CompletedTask; });

        await cut.InvokeAsync(() => cut.Instance.OnShortcut("run", 0));
        await cut.InvokeAsync(() => cut.Instance.OnShortcut("advance", 0));
        await cut.InvokeAsync(() => cut.Instance.OnShortcut("choose", 3));
        await cut.InvokeAsync(() => cut.Instance.OnShortcut("previous", 0));
        await cut.InvokeAsync(() => cut.Instance.OnShortcut("next", 0));
        await cut.InvokeAsync(() => cut.Instance.OnShortcut("playpause", 0));

        Assert.Equal([new(StepCommand.Run), new(StepCommand.Advance), new(StepCommand.Choose, 3),
            new(StepCommand.Previous), new(StepCommand.Next), new(StepCommand.PlayPause)], seen);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cut.Instance.OnShortcut("nope", 0));
    }
}
