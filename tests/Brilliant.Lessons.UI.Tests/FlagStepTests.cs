using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>Flagging a step from the lesson, and the Flagged steps screen with its export.</summary>
public class FlagStepTests : ShortcutContext
{
    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static readonly Lesson Lesson = new("lesson.l", "Lesson", "track.t",
        [new ExplainStep("step.a", "Alpha", "Body", [], null), new ExplainStep("step.b", "Beta", "Body", [], null)]);

    private static readonly ContentGraph Content = new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [Lesson.Id])], [Lesson]);

    private readonly MemoryLog _log = new();

    public FlagStepTests() => Services.AddSingleton(new ProgressRecorder(_log, "device"));

    private IRenderedComponent<FlagStep> OpenFlag(string stepId = "step.a") =>
        Render<FlagStep>(p => p.Add(c => c.LessonId, Lesson.Id).Add(c => c.StepId, stepId));

    private IEnumerable<ProgressEvent> Flags => _log.Items.Where(e => e.Type == ProgressEventTypes.StepFlagged);

    [Fact]
    public void Flagging_with_a_note_records_it_against_the_step()
    {
        var cut = OpenFlag();

        cut.Find("button.flag-open").Click();
        cut.Find("textarea.flag-note").Input("the second example contradicts the first");
        cut.Find("button.flag-save").Click();

        var flag = Assert.Single(Flags);
        Assert.Equal(("lesson.l", "step.a"), (flag.LessonId, flag.StepId));
        Assert.Equal("the second example contradicts the first", JsonDocument.Parse(flag.Data!).RootElement.GetProperty("note").GetString());
        Assert.Contains("Flagged", cut.Find(".flag-saved").TextContent);
        Assert.Empty(cut.FindAll("textarea"));
    }

    [Fact]
    public void A_note_is_optional()
    {
        var cut = OpenFlag();

        cut.Find("button.flag-open").Click();
        cut.Find("button.flag-save").Click();

        Assert.Single(Flags);
        Assert.Null(FlaggedSteps.From(_log.Items, Content).Single().Note);
    }

    [Fact]
    public void Cancelling_records_nothing_and_closes_the_note_box()
    {
        var cut = OpenFlag();

        cut.Find("button.flag-open").Click();
        cut.Find("textarea.flag-note").Input("never mind");
        cut.Find("button.flag-cancel").Click();

        Assert.Empty(Flags);
        Assert.Empty(cut.FindAll("textarea"));
        Assert.NotEmpty(cut.FindAll("button.flag-open"));
    }

    [Fact]
    public void Flagging_does_not_complete_the_step()
    {
        var cut = OpenFlag();
        cut.Find("button.flag-open").Click();
        cut.Find("button.flag-save").Click();

        Assert.DoesNotContain(_log.Items, e => e.Type == ProgressEventTypes.StepCompleted);
    }

    [Fact]
    public void Every_lesson_step_has_the_flag_control_even_when_replaying()
    {
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, Lesson).Add(c => c.Review, true));

        Assert.NotEmpty(cut.FindAll("button.flag-open"));
    }

    [Fact]
    public void The_flagged_screen_lists_flags_and_copies_the_export()
    {
        var recorder = new ProgressRecorder(_log, "device");
        recorder.StepFlagged("lesson.l", "step.b", "too fast");
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetVoidResult();

        var cut = Render<FlaggedView>(p => p.Add(c => c.Content, Content));

        var item = Assert.Single(cut.FindAll("li.flagged-item"));
        Assert.Contains("Beta", item.TextContent);
        Assert.Contains("too fast", item.TextContent);
        cut.Find("button.copy-export").Click();
        var copied = Assert.Single(JSInterop.Invocations["navigator.clipboard.writeText"]);
        Assert.Equal(FlaggedSteps.ToMarkdown(FlaggedSteps.From(_log.Items, Content)), copied.Arguments[0]);
        cut.WaitForAssertion(() => Assert.Contains("Copied", cut.Find(".copy-status").TextContent));
    }

    [Fact]
    public void The_flagged_screen_explains_when_nothing_is_flagged()
    {
        var cut = Render<FlaggedView>(p => p.Add(c => c.Content, Content));

        Assert.Contains("Nothing flagged yet", cut.Find(".flagged-none").TextContent);
        Assert.Empty(cut.FindAll("button.copy-export"));
    }

    [Fact]
    public void A_failing_clipboard_is_reported_not_thrown()
    {
        new ProgressRecorder(_log, "device").StepFlagged("lesson.l", "step.a");
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetException(new Microsoft.JSInterop.JSException("denied"));

        var cut = Render<FlaggedView>(p => p.Add(c => c.Content, Content));
        cut.Find("button.copy-export").Click();

        Assert.Contains("Couldn't reach the clipboard", cut.Find(".copy-status").TextContent);
    }
}
