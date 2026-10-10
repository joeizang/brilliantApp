using Brilliant.Core.Content;
using Brilliant.Lessons.UI;
using Bunit;
using Microsoft.AspNetCore.Components.Web;

namespace Brilliant.Lessons.UI.Tests;

public class ParsonsStepViewTests : ShortcutContext
{
    private static readonly ParsonsStep Step = new("step.p", "Grade", "Order the lines", "python",
    [
        new("def grade(score):", 0),
        new("if score >= 50:", 1),
        new("return \"pass\"", 2),
        new("else:", 1),
        new("return \"fail\"", 2),
    ]);

    private IRenderedComponent<ParsonsStepView> Open(Action<ParsonsStepView.Attempt>? onAnswered = null, Action? onContinue = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;   // keyboard-focus calls after a move
        return Render<ParsonsStepView>(p => p
            .Add(c => c.Step, Step)
            .Add(c => c.OnAnswered, a => onAnswered?.Invoke(a))
            .Add(c => c.OnContinue, () => onContinue?.Invoke()));
    }

    private static string[] Texts(IRenderedComponent<ParsonsStepView> cut) => cut.FindAll(".row .text").Select(t => t.TextContent).ToArray();

    // Puts the rows in solution order and indents them (by their buttons) the way the solution does.
    private static void Solve(IRenderedComponent<ParsonsStepView> cut)
    {
        for (var target = 0; target < Step.Lines.Count; target++)
        {
            var from = Array.IndexOf(Texts(cut), Step.Lines[target].Text);
            while (from > target)
            {
                cut.FindAll(".row")[from].QuerySelector("button[aria-label='Move up']")!.Click();
                from--;
            }
        }
        for (var i = 0; i < Step.Lines.Count; i++)
            for (var level = 0; level < Step.Lines[i].Level; level++)
                cut.FindAll(".row")[i].QuerySelector("button[aria-label='Indent']")!.Click();
    }

    [Fact]
    public void Every_line_is_shown_once_in_a_different_order_and_unindented()
    {
        var cut = Open();

        Assert.Equal(Step.Lines.Select(l => l.Text).Order(), Texts(cut).Order());
        Assert.NotEqual(Step.Lines.Select(l => l.Text), Texts(cut));
        Assert.All(cut.FindAll(".row .indent"), i => Assert.Contains("width:0rem", i.GetAttribute("style")));
        Assert.All(cut.FindAll(".row"), r => Assert.Equal("true", r.GetAttribute("draggable")));
    }

    [Fact]
    public void Rows_have_labels_that_give_their_position_and_indentation()
    {
        var cut = Open();
        Assert.All(cut.FindAll(".row"), r => Assert.Matches(@"^Line \d of 5, indented 0 levels: ", r.GetAttribute("aria-label")));
    }

    [Fact]
    public void The_buttons_move_a_line_up_and_down_and_the_ends_cannot_go_further()
    {
        var cut = Open();
        var before = Texts(cut);

        Assert.True(cut.FindAll(".row")[0].QuerySelector("button[aria-label='Move up']")!.HasAttribute("disabled"));
        Assert.True(cut.FindAll(".row")[4].QuerySelector("button[aria-label='Move down']")!.HasAttribute("disabled"));

        cut.FindAll(".row")[0].QuerySelector("button[aria-label='Move down']")!.Click();

        Assert.Equal([before[1], before[0], before[2], before[3], before[4]], Texts(cut));
    }

    [Fact]
    public void Indent_buttons_change_the_indentation_within_its_limits()
    {
        var cut = Open();
        Button(cut, 0, "Indent").Click();
        Button(cut, 0, "Indent").Click();

        Assert.Contains("width:4rem", cut.FindAll(".row .indent")[0].GetAttribute("style"));
        Assert.True(Button(cut, 0, "Indent").HasAttribute("disabled"));   // the solution is never deeper than 2

        Button(cut, 0, "Outdent").Click();
        Assert.Contains("width:2rem", cut.FindAll(".row .indent")[0].GetAttribute("style"));
        Assert.True(Button(cut, 1, "Outdent").HasAttribute("disabled"));
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<ParsonsStepView> cut, int row, string label) =>
        cut.FindAll(".row")[row].QuerySelector($"button[aria-label='{label}']")!;

    [Fact]
    public void Alt_arrows_move_and_indent_the_focused_line()
    {
        var cut = Open();
        var before = Texts(cut);

        cut.FindAll(".row")[2].KeyDown(new KeyboardEventArgs { Key = "ArrowUp", AltKey = true });
        Assert.Equal([before[0], before[2], before[1], before[3], before[4]], Texts(cut));

        cut.FindAll(".row")[1].KeyDown(new KeyboardEventArgs { Key = "ArrowRight", AltKey = true });
        Assert.Contains("width:2rem", cut.FindAll(".row .indent")[1].GetAttribute("style"));

        cut.FindAll(".row")[1].KeyDown(new KeyboardEventArgs { Key = "ArrowLeft", AltKey = true });
        Assert.Contains("width:0rem", cut.FindAll(".row .indent")[1].GetAttribute("style"));
    }

    [Fact]
    public void Arrow_keys_without_alt_do_nothing()
    {
        var cut = Open();
        var before = Texts(cut);

        cut.FindAll(".row")[2].KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });

        Assert.Equal(before, Texts(cut));
    }

    [Fact]
    public void Dragging_a_line_onto_another_moves_it_there()
    {
        var cut = Open();
        var before = Texts(cut);

        cut.FindAll(".row")[4].DragStart();
        cut.FindAll(".row")[1].Drop();

        Assert.Equal([before[0], before[4], before[1], before[2], before[3]], Texts(cut));
        Assert.Empty(cut.FindAll(".row.dragging"));
    }

    [Fact]
    public void Dropping_without_a_drag_in_progress_changes_nothing()
    {
        var cut = Open();
        var before = Texts(cut);

        cut.FindAll(".row")[1].Drop();

        Assert.Equal(before, Texts(cut));
    }

    [Fact]
    public void A_correct_arrangement_is_reported_and_offers_continue()
    {
        ParsonsStepView.Attempt? attempt = null;
        var continued = false;
        var cut = Open(a => attempt = a, () => continued = true);
        Solve(cut);

        cut.Find("button.primary").Click();

        Assert.True(attempt!.Result.IsCorrect);
        Assert.Equal(Step.Lines.Count, attempt.Arrangement.Count);
        Assert.Contains("Correct", cut.Find(".verdict").TextContent);
        Assert.Empty(cut.FindAll(".row.wrong"));

        cut.Find("button.primary").Click();
        Assert.True(continued);
    }

    [Fact]
    public void A_wrong_arrangement_marks_rows_explains_why_and_lets_the_learner_try_again()
    {
        ParsonsStepView.Attempt? attempt = null;
        var cut = Open(a => attempt = a);
        Solve(cut);
        Button(cut, 1, "Outdent").Click();     // right line, wrong indentation

        cut.Find("button.primary").Click();

        Assert.False(attempt!.Result.IsCorrect);
        Assert.Contains("4 of 5", cut.Find(".verdict").TextContent);
        Assert.Single(cut.FindAll(".row.wrong"));
        Assert.Equal("Right line, wrong indentation", cut.Find(".row.wrong .why").TextContent);
        Assert.Equal(4, cut.FindAll(".row.right").Count);
        Assert.All(cut.FindAll(".row"), r => Assert.Equal("false", r.GetAttribute("draggable")));   // locked while reviewing the result

        cut.Find("button.primary").Click();   // Try again

        Assert.Empty(cut.FindAll(".verdict"));
        Assert.Empty(cut.FindAll(".row.wrong"));
        Assert.Equal(Step.Lines.Select(l => l.Text), Texts(cut));   // keeps the learner's arrangement
        Assert.All(cut.FindAll(".row"), r => Assert.Equal("true", r.GetAttribute("draggable")));
    }

    [Fact]
    public void A_line_in_the_wrong_place_says_a_different_line_goes_there()
    {
        var cut = Open();
        // Whatever the shuffle produced, a flat arrangement can't be right, so at least one row explains itself.
        cut.Find("button.primary").Click();

        Assert.NotEmpty(cut.FindAll(".row.wrong .why"));
        Assert.All(cut.FindAll(".row.wrong .why"), w => Assert.Contains(w.TextContent, new[] { "Right line, wrong indentation", "A different line goes here" }));
    }
}
