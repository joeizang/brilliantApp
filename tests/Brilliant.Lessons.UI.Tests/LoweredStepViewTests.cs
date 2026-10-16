using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The lowered step (#154): the snippet, what the compiler made of it (C# or IL), and an answer box that works like a prediction's.</summary>
public class LoweredStepViewTests : ShortcutContext
{
    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static readonly LoweredCode Made = new("while (e.MoveNext()) { }", ".method public static int32 Total()");

    private static readonly LoweredStep Choice = new("step.l", "What foreach becomes", "Which call ends the loop?", "foreach (var x in xs) { }",
        [], [], [new ChoiceOption("MoveNext", true, "Right."), new ChoiceOption("Dispose", false, "Dispose runs after.")]) { Lowered = Made };

    private static readonly LoweredStep Typed = Choice with { Options = [], Accepted = ["MoveNext"], Mistakes = [new MistakePattern(["Next"], null, "C# calls it MoveNext.")] };

    private readonly MemoryLog _log = new();

    public LoweredStepViewTests() => Services.AddSingleton(new ProgressRecorder(_log, "device"));

    [Fact]
    public void The_snippet_and_its_lowered_csharp_are_shown_and_the_toggle_switches_to_il()
    {
        var cut = Render<LoweredStepView>(p => p.Add(c => c.Step, Choice));

        Assert.Contains("foreach (var x in xs)", cut.Markup);
        Assert.Equal("while (e.MoveNext()) { }", cut.Find("pre.lowered-code code").TextContent);
        Assert.Equal("true", cut.Find("button.lowered-show-csharp").GetAttribute("aria-pressed"));

        cut.Find("button.lowered-show-il").Click();

        Assert.Equal(Made.Il, cut.Find("pre.lowered-code code").TextContent);
        Assert.Equal("il", cut.Find("pre.lowered-code").GetAttribute("data-language"));
        Assert.Equal("true", cut.Find("button.lowered-show-il").GetAttribute("aria-pressed"));
        Assert.Equal("false", cut.Find("button.lowered-show-csharp").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void The_next_question_opens_on_the_lowered_csharp_even_if_the_last_was_left_on_il()
    {
        var cut = Render<LoweredStepView>(p => p.Add(c => c.Step, Choice));
        cut.Find("button.lowered-show-il").Click();

        cut.Render(p => p.Add(c => c.Step, Choice with { Id = "step.next" }));

        Assert.Equal(Made.CSharp, cut.Find("pre.lowered-code code").TextContent);
    }

    [Fact]
    public void A_long_lowered_listing_scrolls_inside_its_own_box()
    {
        var cut = Render<LoweredStepView>(p => p.Add(c => c.Step, Choice));

        Assert.Contains("max-h-[60vh]", cut.Find("pre.lowered-code").ClassName);
    }

    [Fact]
    public void A_pack_without_the_compiler_output_says_so_instead_of_showing_nothing()
    {
        var cut = Render<LoweredStepView>(p => p.Add(c => c.Step, Choice with { Lowered = null }));

        Assert.Contains("isn't in this pack", cut.Find(".lowered-missing").TextContent);
        Assert.Empty(cut.FindAll("button.lowered-show-il"));
    }

    [Fact]
    public void A_multiple_choice_answer_is_checked_and_reported()
    {
        PredictOutputStepView.Attempt? seen = null;
        var cut = Render<LoweredStepView>(p => p.Add(c => c.Step, Choice).Add(c => c.OnAnswered, a => seen = a));

        cut.FindAll("ul.options input")[1].Change(true);
        cut.Find("button.primary").Click();
        Assert.Contains("Not quite", cut.Find(".verdict").TextContent);
        Assert.False(seen!.Result.IsCorrect);

        cut.Find("button.primary").Click();   // Try again
        cut.FindAll("ul.options input")[0].Change(true);
        cut.Find("button.primary").Click();

        Assert.Contains("Correct", cut.Find(".verdict").TextContent);
        Assert.True(seen.Result.IsCorrect);
    }

    [Fact]
    public void A_typed_answer_uses_the_steps_mistake_patterns()
    {
        var cut = Render<LoweredStepView>(p => p.Add(c => c.Step, Typed));

        Assert.Equal("Your answer", cut.Find("textarea").GetAttribute("aria-label"));
        cut.Find("textarea").Input("Next");
        cut.Find("button.primary").Click();
        Assert.Equal("C# calls it MoveNext.", cut.Find(".mistake-feedback").TextContent);

        cut.Find("button.primary").Click();   // Try again
        cut.Find("textarea").Input("MoveNext");
        cut.Find("button.primary").Click();
        Assert.Contains("Correct", cut.Find(".verdict").TextContent);
    }

    [Fact]
    public void It_works_on_a_phone_too()
    {
        Screen.Set(ScreenClass.Phone);

        var cut = Render<LoweredStepView>(p => p.Add(c => c.Step, Choice));

        Assert.NotEmpty(cut.FindAll("button.lowered-show-il"));
        Assert.NotEmpty(cut.FindAll("ul.options input"));
    }

    private static Lesson LessonWith(LoweredStep step) => new("lesson.l", "Lesson", "track.t", [step])
    {
        Concepts = [new Concept("concept.c", "C")],
        ReviewItems = [new ReviewItem("review.l", "concept.c", step.Id)],
    };

    [Fact]
    public void Answering_in_a_lesson_is_recorded_but_not_when_replaying_it()
    {
        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, LessonWith(Choice)));
        cut.FindAll("ul.options input")[0].Change(true);
        cut.Find("button.primary").Click();
        Assert.Single(_log.Items, e => e.Type == ProgressEventTypes.StepAnswered && e.StepId == "step.l");

        _log.Items.Clear();
        var replay = Render<LessonViewer>(p => p.Add(c => c.Lesson, LessonWith(Choice)).Add(c => c.Review, true));
        replay.FindAll("ul.options input")[0].Change(true);
        replay.Find("button.primary").Click();
        Assert.DoesNotContain(_log.Items, e => e.Type == ProgressEventTypes.StepAnswered);
    }

    [Fact]
    public void Review_asks_a_lowered_question_instead_of_skipping_it()
    {
        var lesson = LessonWith(Choice);
        var content = new ContentGraph(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [lesson.Id])], [lesson]);
        Services.GetRequiredService<ProgressRecorder>().CompleteStep(lesson, Choice.Id);

        var cut = Render<ReviewView>(p => p.Add(c => c.Content, content));

        Assert.DoesNotContain("can't be reviewed", cut.Markup);
        Assert.NotEmpty(cut.FindAll("pre.lowered-code"));
    }
}
