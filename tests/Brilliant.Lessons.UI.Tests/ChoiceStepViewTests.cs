using Brilliant.Core.Content;
using Brilliant.Lessons.UI;
using Bunit;

namespace Brilliant.Lessons.UI.Tests;

public class ChoiceStepViewTests : ShortcutContext
{
    private static readonly ChoiceStep Single = new("step.c", "Pick", "Which?", false,
        [new ChoiceOption("a", false, null), new ChoiceOption("b", true, null), new ChoiceOption("c", false, null)]);

    private static readonly ChoiceStep Multi = new("step.m", "Pick", "Which?", true,
        [new ChoiceOption("a", true, null), new ChoiceOption("b", true, null), new ChoiceOption("c", false, null)]);

    private IRenderedComponent<ChoiceStepView> Open(ChoiceStep step, List<bool>? answers = null, Action? onContinue = null) =>
        Render<ChoiceStepView>(p => p
            .Add(c => c.Step, step)
            .Add(c => c.OnAnswered, r => answers?.Add(r.IsCorrect))
            .Add(c => c.OnContinue, () => onContinue?.Invoke()));

    private static string[] Picked(IRenderedComponent<ChoiceStepView> cut) =>
        cut.FindAll("li.option.selected").Select(li => li.TextContent.Trim()).ToArray();

    [Fact]
    public async Task A_number_key_picks_that_answer_and_another_moves_the_pick()
    {
        var cut = Open(Single);

        await Press(StepCommand.Choose, 3);
        Assert.Equal(["c"], Picked(cut));

        await Press(StepCommand.Choose, 1);
        Assert.Equal(["a"], Picked(cut));
    }

    [Fact]
    public async Task Number_keys_toggle_answers_in_a_multi_select_step()
    {
        var cut = Open(Multi);

        await Press(StepCommand.Choose, 1);
        await Press(StepCommand.Choose, 2);
        Assert.Equal(["a", "b"], Picked(cut));

        await Press(StepCommand.Choose, 1);
        Assert.Equal(["b"], Picked(cut));
    }

    [Fact]
    public async Task A_number_with_no_such_answer_does_nothing()
    {
        var cut = Open(Single);
        await Press(StepCommand.Choose, 4);
        Assert.Empty(Picked(cut));
    }

    [Fact]
    public async Task Return_checks_then_continues_when_right()
    {
        var answers = new List<bool>();
        var continued = 0;
        var cut = Open(Single, answers, () => continued++);

        await Press(StepCommand.Advance);                 // nothing picked yet: nothing to check
        Assert.Empty(answers);

        await Press(StepCommand.Choose, 2);
        await Press(StepCommand.Advance);
        Assert.Equal([true], answers);
        Assert.Contains("Correct", cut.Markup);
        Assert.Equal(0, continued);

        await Press(StepCommand.Advance);
        Assert.Equal(1, continued);
    }

    [Fact]
    public async Task Return_after_a_wrong_answer_tries_again_and_clears_the_pick()
    {
        var answers = new List<bool>();
        var cut = Open(Single, answers);

        await Press(StepCommand.Choose, 1);
        await Press(StepCommand.Advance);
        Assert.Equal([false], answers);
        Assert.Contains("Not quite", cut.Markup);

        await Press(StepCommand.Advance);
        Assert.Empty(Picked(cut));
        Assert.Equal("Check", cut.Find("button.primary").TextContent);
    }

    [Fact]
    public async Task Run_checks_but_never_moves_on_so_a_doubly_delivered_command_is_harmless()
    {
        var answers = new List<bool>();
        var continued = 0;
        var cut = Open(Single, answers, () => continued++);

        await Press(StepCommand.Choose, 2);
        await Press(StepCommand.Run);
        await Press(StepCommand.Run);

        Assert.Equal([true], answers);
        Assert.Equal(0, continued);
    }

    [Fact]
    public async Task A_locked_step_ignores_number_keys()
    {
        var cut = Open(Single);
        await Press(StepCommand.Choose, 1);
        await Press(StepCommand.Advance);                 // checked: locked
        await Press(StepCommand.Choose, 2);
        Assert.Equal(["a"], Picked(cut).Concat(cut.FindAll("li.option.wrong").Select(li => li.TextContent.Trim())).ToArray());
    }

    [Fact]
    public async Task A_removed_step_stops_listening()
    {
        var answers = new List<bool>();
        var cut = Open(Single, answers);
        await Press(StepCommand.Choose, 2);

        await DisposeComponentsAsync();
        await Press(StepCommand.Advance);

        Assert.Empty(answers);
    }
}
