using Brilliant.Core.Content;
using Brilliant.Core.Python;
using Bunit;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI.Tests;

public class TracePlayerTests : ShortcutContext
{
    private const string Code = "nums = [3, 1, 2]\ni = 0\nwhile i < 2:\n    i += 1\n";

    private static readonly IReadOnlyList<Visual> Visuals = [new Visual("nums", Visual.Array, ["i"])];

    private static TraceFrame Frame(int? line, int? i, params string[] cells)
    {
        var locals = new List<TraceLocal> { new("nums", "list", "[" + string.Join(", ", cells) + "]") };
        if (i is { } n) locals.Add(new TraceLocal("i", "int", n.ToString()));
        return new TraceFrame(line is null ? "return" : "line", line, "<module>", locals,
            new Dictionary<string, TrackedArray> { ["nums"] = new("list", cells, 0) });
    }

    // Four steps: i is unset, then 0, then 1, then the run finishes.
    private static readonly TraceResult Result = new(TraceStatus.Ok,
        [Frame(2, null, "3", "1", "2"), Frame(3, 0, "3", "1", "2"), Frame(4, 0, "3", "1", "2"), Frame(null, 1, "3", "1", "2")],
        "", null, null, null);

    private BunitJSModuleInterop Editor()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/code-editor.js");
        module.Setup<int>("create", _ => true).SetResult(1);
        return module;
    }

    private IRenderedComponent<TracePlayer> Start(TraceResult run, IReadOnlyList<Visual> visuals, out BunitJSModuleInterop module)
    {
        module = Editor();
        var states = TraceModel.Build(run, visuals);
        return Render<TracePlayer>(p => p.Add(c => c.Code, Code).Add(c => c.States, states).Add(c => c.Result, run));
    }

    private IRenderedComponent<TracePlayer> Open(out BunitJSModuleInterop module) => Start(Result, Visuals, out module);

    private IRenderedComponent<TracePlayer> Open(TraceResult run, out BunitJSModuleInterop module) => Start(run, Visuals, out module);

    private static string Pointer(IRenderedComponent<TracePlayer> cut, string name, string attribute) =>
        cut.Find($"g.pointer[data-name='{name}']").GetAttribute(attribute)!;

    [Fact]
    public void Draws_every_cell_of_the_array_with_its_value()
    {
        var cut = Open(out _);

        Assert.Equal(["3", "1", "2"], cut.FindAll("g.cell .cell-text").Select(t => t.TextContent));
        Assert.Equal(["0", "1", "2"], cut.FindAll("g.cell .cell-index").Select(t => t.TextContent));
        Assert.Contains("nums", cut.Find(".array-name").TextContent);
    }

    [Fact]
    public void Shows_the_position_and_cannot_go_back_from_the_first_step()
    {
        var cut = Open(out _);

        Assert.Equal("Step 1 of 4", cut.Find(".position").TextContent);
        Assert.True(cut.Find("button.prev").HasAttribute("disabled"));
        Assert.False(cut.Find("button.next").HasAttribute("disabled"));
    }

    [Fact]
    public void Next_and_Previous_move_through_the_steps_and_stop_at_both_ends()
    {
        var cut = Open(out _);

        cut.Find("button.next").Click();
        Assert.Equal("Step 2 of 4", cut.Find(".position").TextContent);
        cut.Find("button.next").Click();
        cut.Find("button.next").Click();
        Assert.Equal("Step 4 of 4", cut.Find(".position").TextContent);
        Assert.True(cut.Find("button.next").HasAttribute("disabled"));

        cut.Find("button.prev").Click();
        Assert.Equal("Step 3 of 4", cut.Find(".position").TextContent);
    }

    [Fact]
    public void A_pointer_sits_on_its_cell_and_moves_to_the_new_one_on_the_next_step()
    {
        var cut = Open(out _);

        Assert.Empty(cut.FindAll("g.pointer"));        // i does not exist yet
        cut.Find("button.next").Click();
        Assert.Equal("0", Pointer(cut, "i", "data-index"));
        Assert.Equal("i", cut.Find("g.pointer .pointer-label").TextContent);

        cut.Find("button.next").Click();
        cut.Find("button.next").Click();
        Assert.Equal("1", Pointer(cut, "i", "data-index"));
        Assert.Contains("moved", Pointer(cut, "i", "class"));
    }

    [Fact]
    public void A_pointer_is_positioned_by_a_css_transform_so_that_moving_it_can_be_a_transition()
    {
        var cut = Open(out _);
        cut.Find("button.next").Click();
        var before = Pointer(cut, "i", "style");

        cut.Find("button.next").Click();
        cut.Find("button.next").Click();

        var after = Pointer(cut, "i", "style");
        Assert.StartsWith("transform: translate(", before);
        Assert.StartsWith("transform: translate(", after);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Cells_that_changed_since_the_previous_step_are_marked()
    {
        var run = new TraceResult(TraceStatus.Ok,
            [Frame(1, null, "3", "1", "2"), Frame(2, null, "3", "9", "2")], "", null, null, null);
        var cut = Open(run, out _);

        Assert.Empty(cut.FindAll("g.cell.changed"));
        cut.Find("button.next").Click();

        var changed = cut.FindAll("g.cell.changed");
        Assert.Single(changed);
        Assert.Equal("1", changed[0].GetAttribute("data-index"));
    }

    [Fact]
    public void Locals_are_listed_with_their_values_and_the_changed_ones_marked()
    {
        var cut = Open(out _);
        cut.Find("button.next").Click();
        cut.Find("button.next").Click();
        cut.Find("button.next").Click();

        var i = cut.Find(".locals tr[data-name='i']");
        Assert.Equal("1", i.QuerySelector(".local-value")!.TextContent);
        Assert.Contains("changed", i.GetAttribute("class"));
        Assert.DoesNotContain("changed", cut.Find(".locals tr[data-name='nums']").GetAttribute("class"));
    }

    [Fact]
    public void The_code_is_shown_in_a_read_only_editor()
    {
        var cut = Open(out var module);

        cut.WaitForAssertion(() =>
        {
            var create = module.Invocations["create"].Single();
            Assert.Equal(Code, create.Arguments[1]);
            Assert.Equal(true, create.Arguments[3]);    // read-only
        });
    }

    [Fact]
    public void The_current_line_is_highlighted_and_follows_the_step()
    {
        var cut = Open(out var module);

        cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations["highlightLine"].Last().Arguments[1]));
        cut.Find("button.next").Click();
        cut.WaitForAssertion(() => Assert.Equal(3, module.Invocations["highlightLine"].Last().Arguments[1]));
        cut.Find("button.prev").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations["highlightLine"].Last().Arguments[1]));
    }

    [Fact]
    public void The_final_step_highlights_no_line()
    {
        var cut = Open(out var module);
        for (var n = 0; n < 3; n++) cut.Find("button.next").Click();

        cut.WaitForAssertion(() => Assert.Null(module.Invocations["highlightLine"].Last().Arguments[1]));
    }

    [Fact]
    public void Pointers_on_the_same_cell_are_stacked_so_that_both_labels_show()
    {
        var run = new TraceResult(TraceStatus.Ok,
            [new TraceFrame("line", 1, "<module>",
                [new("nums", "list", "[5, 6]"), new("lo", "int", "1"), new("hi", "int", "1")],
                new Dictionary<string, TrackedArray> { ["nums"] = new("list", ["5", "6"], 0) })],
            "", null, null, null);
        var cut = Start(run, [new Visual("nums", Visual.Array, ["lo", "hi"])], out _);

        Assert.Equal("0", Pointer(cut, "lo", "data-row"));
        Assert.Equal("1", Pointer(cut, "hi", "data-row"));
        Assert.Equal("1", Pointer(cut, "lo", "data-index"));
        Assert.Equal("1", Pointer(cut, "hi", "data-index"));
    }

    [Fact]
    public void A_pointer_that_is_off_the_array_is_not_drawn_but_is_still_mentioned()
    {
        var run = new TraceResult(TraceStatus.Ok,
            [new TraceFrame("line", 1, "<module>",
                [new("nums", "list", "[5, 6]"), new("i", "int", "2")],
                new Dictionary<string, TrackedArray> { ["nums"] = new("list", ["5", "6"], 0) })],
            "", null, null, null);
        var cut = Open(run, out _);

        Assert.Empty(cut.FindAll("g.pointer"));
        Assert.Contains("i", cut.Find(".off-array").TextContent);
        Assert.Contains("not on a cell", cut.Find(".off-array").TextContent);
    }

    [Fact]
    public void Cells_beyond_those_recorded_are_counted()
    {
        var run = new TraceResult(TraceStatus.Ok,
            [new TraceFrame("line", 1, "<module>", [new("nums", "list", "[...]")],
                new Dictionary<string, TrackedArray> { ["nums"] = new("list", ["1", "2"], 38) })],
            "", null, null, null);
        var cut = Open(run, out _);

        Assert.Equal("+38 more", cut.Find("g.cell.more").TextContent.Trim());
    }

    [Fact]
    public void Printed_output_appears_on_the_last_step_only()
    {
        var run = Result with { Stdout = "hello\n" };
        var cut = Open(run, out _);

        Assert.Empty(cut.FindAll(".trace-output"));
        for (var n = 0; n < 3; n++) cut.Find("button.next").Click();
        Assert.Equal("hello", cut.Find(".trace-output pre").TextContent.Trim());
    }

    [Fact]
    public void An_error_is_shown_on_the_last_step_only()
    {
        var run = Result with
        {
            Status = TraceStatus.Error, Message = "ZeroDivisionError: division by zero",
            Traceback = "Traceback...\nZeroDivisionError: division by zero", ErrorLine = 3,
        };
        var cut = Open(run, out _);

        Assert.Empty(cut.FindAll(".trace-error"));
        for (var n = 0; n < 3; n++) cut.Find("button.next").Click();
        Assert.Contains("ZeroDivisionError", cut.Find(".trace-error").TextContent);
        Assert.Contains("Traceback", cut.Find("pre.traceback").TextContent);
    }

    [Fact]
    public void A_run_that_was_cut_short_says_so_on_the_last_step()
    {
        var run = Result with { Status = TraceStatus.Truncated, Message = "Stopped after 300 steps." };
        var cut = Open(run, out _);

        for (var n = 0; n < 3; n++) cut.Find("button.next").Click();
        Assert.Contains("Stopped after 300 steps", cut.Find(".trace-note").TextContent);
    }

    [Fact]
    public void Rendering_again_with_the_same_trace_keeps_the_learners_place()
    {
        var states = TraceModel.Build(Result, Visuals);
        Editor();
        var cut = Render<TracePlayer>(p => p.Add(c => c.Code, Code).Add(c => c.States, states).Add(c => c.Result, Result));
        cut.Find("button.next").Click();
        cut.Find("button.next").Click();

        cut.Render(p => p.Add(c => c.Code, Code).Add(c => c.States, states).Add(c => c.Result, Result));

        Assert.Equal("Step 3 of 4", cut.Find(".position").TextContent);
    }

    [Fact]
    public void A_new_trace_starts_again_at_the_first_step()
    {
        var cut = Open(out _);
        cut.Find("button.next").Click();
        cut.Find("button.next").Click();

        var states = TraceModel.Build(Result, Visuals);
        cut.Render(p => p.Add(c => c.Code, Code).Add(c => c.States, states).Add(c => c.Result, Result));

        Assert.Equal("Step 1 of 4", cut.Find(".position").TextContent);
    }
}
