using Brilliant.Core.Content;
using Brilliant.Lessons.UI;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

public class FillBlankStepViewTests : ShortcutContext
{
    private static readonly FillBlankStep Step = new("step.fb", "Sum it", "Complete the loop", "python",
        "total = 0\nfor i in {{iter}}:\n    total {{op}} i\n",
        [
            new Blank("iter", ["range(n)"], [new MistakePattern(["range(1, n)"], null, "That stops one short of n.")]),
            new Blank("op", ["+="], []),
        ]);

    private IRenderedComponent<FillBlankStepView> Open(Action<FillBlankStepView.Attempt>? onAnswered = null, Action? onContinue = null) =>
        Render<FillBlankStepView>(p => p
            .Add(c => c.Step, Step)
            .Add(c => c.OnAnswered, a => onAnswered?.Invoke(a))
            .Add(c => c.OnContinue, () => onContinue?.Invoke()));

    private static void Fill(IRenderedComponent<Microsoft.AspNetCore.Components.IComponent> cut, int blank, string text) =>
        cut.FindAll("input.blank")[blank].Input(text);

    [Fact]
    public void The_code_is_read_only_text_with_one_editable_input_per_blank()
    {
        var cut = Open();

        Assert.Equal(2, cut.FindAll("input.blank").Count);
        Assert.Equal(["Blank 1 of 2", "Blank 2 of 2"], cut.FindAll("input.blank").Select(i => i.GetAttribute("aria-label")));
        var text = string.Join("", cut.FindAll(".line .code").Select(c => c.TextContent));
        Assert.Contains("total = 0", text);
        Assert.Contains("for i in ", text);
        Assert.DoesNotContain("{{", text);
        Assert.Equal(3, cut.FindAll(".line").Count);
        Assert.All(cut.FindAll(".line .code"), c => Assert.Empty(c.QuerySelectorAll("input")));
    }

    [Fact]
    public void Check_waits_until_every_blank_has_something_in_it()
    {
        var cut = Open();
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));

        Fill(cut, 0, "range(n)");
        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));

        Fill(cut, 1, "+=");
        Assert.False(cut.Find("button.primary").HasAttribute("disabled"));
    }

    [Fact]
    public void Right_answers_show_a_verdict_and_continue()
    {
        FillBlankStepView.Attempt? attempt = null;
        var continued = false;
        var cut = Open(a => attempt = a, () => continued = true);
        Fill(cut, 0, "range(n)");
        Fill(cut, 1, " += ");

        cut.Find("button.primary").Click();

        Assert.Contains("Correct!", cut.Find(".verdict").TextContent);
        Assert.True(attempt!.Result.IsCorrect);
        Assert.Equal(new Dictionary<string, string> { ["iter"] = "range(n)", ["op"] = " += " }, attempt.Responses);
        Assert.All(cut.FindAll("input.blank"), i => Assert.True(i.HasAttribute("disabled")));
        cut.Find("button.primary").Click();
        Assert.True(continued);
    }

    [Fact]
    public void Wrong_blanks_are_marked_with_their_feedback_and_the_right_ones_are_kept()
    {
        var cut = Open();
        Fill(cut, 0, "range(1, n)");
        Fill(cut, 1, "+=");

        cut.Find("button.primary").Click();

        var blanks = cut.FindAll("input.blank");
        Assert.Contains("wrong", blanks[0].ClassList);
        Assert.Contains("right", blanks[1].ClassList);
        Assert.Contains("One blank isn't right yet", cut.Find(".verdict").TextContent);
        Assert.Contains("Blank 1:", cut.Find(".mistake-feedback").TextContent);
        Assert.Contains("That stops one short of n.", cut.Find(".mistake-feedback").TextContent);

        cut.Find("button.primary").Click();   // Try again

        Assert.Equal("range(1, n)", cut.FindAll("input.blank")[0].GetAttribute("value"));
        Assert.Equal("+=", cut.FindAll("input.blank")[1].GetAttribute("value"));
        Assert.All(cut.FindAll("input.blank"), i => Assert.False(i.HasAttribute("disabled")));

        Fill(cut, 0, "range(n)");
        cut.Find("button.primary").Click();
        Assert.Contains("Correct!", cut.Find(".verdict").TextContent);
    }

    [Fact]
    public void The_lesson_viewer_records_a_fill_blank_attempt_and_completes_the_step()
    {
        var log = new RecordingLog();
        Services.AddSingleton(new Brilliant.Core.Progress.ProgressRecorder(log, "d"));
        var lesson = new Lesson("lesson.one", "One", "track.a", [Step]);
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, lesson));

        Fill(cut, 0, "range(n)");
        Fill(cut, 1, "+=");
        cut.Find("button.primary").Click();
        cut.Find("button.primary").Click();   // Continue

        Assert.Equal(["StepAnswered", "StepCompleted", "LessonCompleted"], log.Items.Select(e => e.Type));
        Assert.Contains("\"iter\":\"range(n)\"", log.Items[0].Data);
    }

    private sealed class RecordingLog : Brilliant.Core.Progress.IProgressEventLog
    {
        public List<Brilliant.Core.Progress.ProgressEvent> Items { get; } = [];
        public bool Append(Brilliant.Core.Progress.ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<Brilliant.Core.Progress.ProgressEvent> ReadAll() => Items;
        public Brilliant.Core.Progress.EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }
}
