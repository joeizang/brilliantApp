using System.Globalization;
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
        return Show(module, TraceModel.Build(run, visuals), run);
    }

    // The player's first render is not finished when Render returns: it highlights the first line once the editor is ready,
    // and that takes a hop onto another thread. Until it has happened, a click's re-render may not be visible yet, so a test that
    // clicked straight away read stale markup whenever the machine was busy. Wait for the highlight before the test starts.
    private IRenderedComponent<TracePlayer> Show(BunitJSModuleInterop module, IReadOnlyList<TraceState> states, TraceResult run)
    {
        var cut = Render<TracePlayer>(p => p.Add(c => c.Code, Code).Add(c => c.States, states).Add(c => c.Result, run));
        cut.WaitForAssertion(() => module.VerifyInvoke("highlightLine"));
        return cut;
    }

    private IRenderedComponent<TracePlayer> Open(out BunitJSModuleInterop module) => Start(Result, Visuals, out module);

    private IRenderedComponent<TracePlayer> Open(TraceResult run, out BunitJSModuleInterop module) => Start(run, Visuals, out module);

    private static string Pointer(IRenderedComponent<TracePlayer> cut, string name, string attribute) =>
        cut.Find($"g.pointer[data-name='{name}']").GetAttribute(attribute)!;

    [Fact]
    public void The_code_box_does_not_end_with_a_blank_line()
    {
        // YAML block text ends in a newline; shown as it is, the editor would number an empty last line the example never runs.
        var cut = Open(out _);

        Assert.Equal(Code.TrimEnd('\n'), cut.FindComponent<CodeEditor>().Instance.InitialCode);
    }

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
            Assert.Equal(Code.TrimEnd('\n'), create.Arguments[1]);
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
        var cut = Show(Editor(), states, Result);
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

    // ---- Dicts and sets ----

    private static readonly IReadOnlyList<Visual> CountsVisual = [new Visual("counts", Visual.Dict, ["word"])];

    private static TraceFrame TableFrame(int? line, string variable, TrackedTable table, params TraceLocal[] locals) =>
        new(line is null ? "return" : "line", line, "<module>", locals, new Dictionary<string, TrackedArray>(),
            new Dictionary<string, TrackedTable> { [variable] = table });

    /// <summary>The id the recorder would give a key: a stand-in that is the same for the same label.</summary>
    private static int IdOf(string key) => key.Aggregate(17, (hash, c) => hash * 31 + c);

    private static TrackedTable Dict(int more, params (string Key, string Value)[] rows) =>
        new("dict", "dict", rows.Select(r => new TableRow(r.Key, r.Value, IdOf(r.Key))).ToList(), more, new Dictionary<string, int>());

    private static TrackedTable Members(params string[] members) =>
        new("set", "set", members.Select(m => new TableRow(m, null, IdOf(m))).ToList(), 0, new Dictionary<string, int>());

    private static TraceFrame CountsFrame(int? line, string? word, params (string Key, string Value)[] rows)
    {
        var locals = new List<TraceLocal> { new("counts", "dict", "{...}") };
        if (word is not null) locals.Add(new TraceLocal("word", "str", word));
        var table = Dict(0, rows);
        if (word is not null && rows.Any(r => r.Key == word)) table = table with { Hits = new Dictionary<string, int> { ["word"] = IdOf(word) } };
        return TableFrame(line, "counts", table, [.. locals]);
    }

    // Two long keys clip to the same label; the recorder still gives them different ids.
    private static TraceFrame ClashingFrame(int line, string firstValue, string secondValue) =>
        TableFrame(line, "counts",
            new TrackedTable("dict", "dict", [new TableRow("'aaaa…", firstValue, 1), new TableRow("'aaaa…", secondValue, 2)], 0, new Dictionary<string, int>()),
            new TraceLocal("counts", "dict", "{...}"));

    private static TraceResult RunOf(params TraceFrame[] frames) => new(TraceStatus.Ok, frames, "", null, null, null);

    private IRenderedComponent<TracePlayer> OpenCounts(TraceResult run) => Start(run, CountsVisual, out _);

    [Fact]
    public void A_dict_is_drawn_as_a_table_of_keys_and_values_in_order()
    {
        var cut = OpenCounts(RunOf(CountsFrame(1, null, ("'b'", "2"), ("'a'", "1"))));

        var table = cut.Find("figure.trace-table[data-variable='counts']");
        Assert.Contains("counts", table.QuerySelector(".table-name")!.TextContent);
        Assert.Contains("dict", table.QuerySelector(".table-name")!.TextContent);
        Assert.Equal(["'b'", "'a'"], cut.FindAll("table.dict .row-key").Select(k => k.TextContent));
        Assert.Equal(["2", "1"], cut.FindAll("table.dict .row-value").Select(v => v.TextContent));
    }

    [Fact]
    public void A_set_is_drawn_as_a_list_of_members_with_no_values()
    {
        var run = RunOf(TableFrame(1, "seen", Members("1", "20", "30"), new TraceLocal("seen", "set", "{...}")));
        var cut = Start(run, [new Visual("seen", Visual.Set, [])], out _);

        Assert.Equal(["1", "20", "30"], cut.FindAll("ul.set .row-key").Select(k => k.TextContent));
        Assert.Empty(cut.FindAll(".row-value"));
        Assert.Contains("set", cut.Find("figure.trace-table .table-name").TextContent);
    }

    [Fact]
    public void An_empty_table_says_so()
    {
        var cut = OpenCounts(RunOf(CountsFrame(1, null)));

        Assert.Empty(cut.FindAll("tr.row"));
        Assert.Contains("empty", cut.Find(".table-empty").TextContent);
    }

    [Fact]
    public void Rows_beyond_those_recorded_are_counted()
    {
        var run = RunOf(TableFrame(1, "counts", Dict(30, ("'a'", "1")), new TraceLocal("counts", "dict", "{...}")));
        var cut = OpenCounts(run);

        Assert.Equal("+30 more", cut.Find(".table-more").TextContent.Trim());
    }

    [Fact]
    public void A_row_added_since_the_previous_step_is_marked_added()
    {
        var cut = OpenCounts(RunOf(CountsFrame(1, null, ("'a'", "1")), CountsFrame(2, null, ("'a'", "1"), ("'b'", "1"))));
        Assert.Empty(cut.FindAll("tr.row.added"));

        cut.Find("button.next").Click();

        var added = Assert.Single(cut.FindAll("tr.row.added"));
        Assert.Equal("'b'", added.QuerySelector(".row-key")!.TextContent);
    }

    [Fact]
    public void A_row_whose_value_changed_is_marked_updated()
    {
        var cut = OpenCounts(RunOf(CountsFrame(1, null, ("'a'", "1"), ("'b'", "1")), CountsFrame(2, null, ("'a'", "2"), ("'b'", "1"))));

        cut.Find("button.next").Click();

        var updated = Assert.Single(cut.FindAll("tr.row.updated"));
        Assert.Equal("'a'", updated.QuerySelector(".row-key")!.TextContent);
        Assert.Equal("2", updated.QuerySelector(".row-value")!.TextContent);
    }

    [Fact]
    public void A_removed_row_stays_as_a_ghost_for_one_step_and_then_goes()
    {
        var cut = OpenCounts(RunOf(
            CountsFrame(1, null, ("'a'", "1"), ("'b'", "1")), CountsFrame(2, null, ("'a'", "1")), CountsFrame(3, null, ("'a'", "1"))));

        cut.Find("button.next").Click();
        var gone = Assert.Single(cut.FindAll("tr.row.removed"));
        Assert.Equal("'b'", gone.QuerySelector(".row-key")!.TextContent);

        cut.Find("button.next").Click();
        Assert.Empty(cut.FindAll("tr.row.removed"));
        Assert.Single(cut.FindAll("tr.row"));
    }

    [Fact]
    public void The_row_a_pointer_looks_up_is_marked_with_the_pointers_name()
    {
        var cut = OpenCounts(RunOf(CountsFrame(1, "'b'", ("'a'", "1"), ("'b'", "2"))));

        var looked = Assert.Single(cut.FindAll("tr.row.looked-up"));
        Assert.Equal("'b'", looked.QuerySelector(".row-key")!.TextContent);
        Assert.Equal("word", looked.QuerySelector(".looked-up-by")!.TextContent);
    }

    [Fact]
    public void A_pointer_whose_key_is_not_a_row_is_called_out_as_a_failed_lookup()
    {
        var cut = OpenCounts(RunOf(CountsFrame(1, "'z'", ("'a'", "1"))));

        Assert.Empty(cut.FindAll("tr.row.looked-up"));
        var miss = cut.Find(".table-miss").TextContent;
        Assert.Contains("word", miss);
        Assert.Contains("no such key", miss);
    }

    [Fact]
    public void Nothing_is_called_out_when_every_lookup_finds_its_row()
    {
        var cut = OpenCounts(RunOf(CountsFrame(1, "'a'", ("'a'", "1"))));

        Assert.Empty(cut.FindAll(".table-miss"));
    }

    [Fact]
    public void Rows_whose_labels_read_the_same_are_all_drawn_and_stepping_through_them_works()
    {
        var run = RunOf(ClashingFrame(1, "1", "2"), ClashingFrame(2, "1", "3"), ClashingFrame(3, "4", "3"));
        var cut = OpenCounts(run);
        Assert.Equal(["1", "2"], cut.FindAll("table.dict .row-value").Select(v => v.TextContent));

        cut.Find("button.next").Click();
        Assert.Equal(["1", "3"], cut.FindAll("table.dict .row-value").Select(v => v.TextContent));
        Assert.Equal("3", Assert.Single(cut.FindAll("tr.row.updated")).QuerySelector(".row-value")!.TextContent);

        cut.Find("button.next").Click();
        cut.Find("button.prev").Click();
        cut.Find("button.prev").Click();
        Assert.Equal(["1", "2"], cut.FindAll("table.dict .row-value").Select(v => v.TextContent));
    }

    [Fact]
    public void Rows_whose_labels_read_the_same_do_not_stop_playback()
    {
        var cut = Start(RunOf(ClashingFrame(1, "1", "2"), ClashingFrame(2, "1", "3"), ClashingFrame(3, "4", "3")), CountsVisual, out _);
        cut.Find("button.play").Click();

        Tick(cut);
        Tick(cut);

        Assert.Equal("Step 3 of 3", Position(cut));
        Assert.Equal(["4", "3"], cut.FindAll("table.dict .row-value").Select(v => v.TextContent));
    }

    [Fact]
    public void A_pointer_marks_the_row_python_found_even_when_the_reprs_differ()
    {
        var table = Dict(0, ("1", "'found'")) with { Hits = new Dictionary<string, int> { ["key"] = IdOf("1") } };
        var run = RunOf(TableFrame(1, "counts", table, new TraceLocal("counts", "dict", "{...}"), new TraceLocal("key", "float", "1.0")));
        var cut = Start(run, [new Visual("counts", Visual.Dict, ["key"])], out _);

        Assert.Equal("key", Assert.Single(cut.FindAll("tr.row.looked-up")).QuerySelector(".looked-up-by")!.TextContent);
        Assert.Empty(cut.FindAll(".table-miss"));
    }

    [Fact]
    public void Arrays_and_tables_can_be_drawn_together()
    {
        var run = RunOf(new TraceFrame("line", 1, "<module>", [new("nums", "list", "[1]"), new("counts", "dict", "{...}")],
            new Dictionary<string, TrackedArray> { ["nums"] = new("list", ["1"], 0) },
            new Dictionary<string, TrackedTable> { ["counts"] = Dict(0, ("1", "1")) }));
        var cut = Start(run, [new Visual("nums", Visual.Array, []), new Visual("counts", Visual.Dict, [])], out _);

        Assert.Single(cut.FindAll("figure.array"));
        Assert.Single(cut.FindAll("figure.trace-table"));
    }

    // ---- Stacks and queues ----

    private static TraceFrame Seq(int? line, string name, string type, string[] cells, int more = 0, params TraceLocal[] locals) =>
        new(line is null ? "return" : "line", line, "<module>", locals, new Dictionary<string, TrackedArray> { [name] = new(type, cells, more) });

    private IRenderedComponent<TracePlayer> Sequence(string kind, string type, params string[][] steps) =>
        Start(RunOf(steps.Select((cells, i) => Seq(i + 1, "x", type, cells)).ToArray()), [new Visual("x", kind, [])], out _);

    /// <summary>The (x, y) a cell, marker or pointer is placed at by its CSS transform.</summary>
    private static (int X, int Y) At(AngleSharp.Dom.IElement element)
    {
        var match = System.Text.RegularExpressions.Regex.Match(element.GetAttribute("style")!, @"translate\((-?\d+)px, (-?\d+)px\)");
        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    private static List<string> Texts(IRenderedComponent<TracePlayer> cut, string selector) =>
        cut.FindAll(selector).Select(t => t.TextContent).ToList();

    [Fact]
    public void A_stack_stands_bottom_to_top_with_its_top_marked()
    {
        var cut = Sequence(Visual.Stack, "list", ["1", "2", "3"]);

        var cells = cut.FindAll("figure.stack g.cell:not(.gone)");
        Assert.Equal(["1", "2", "3"], cells.Select(c => c.QuerySelector(".cell-text")!.TextContent));
        Assert.True(At(cells[0]).Y > At(cells[1]).Y && At(cells[1]).Y > At(cells[2]).Y);   // index 0 at the bottom
        Assert.Equal(At(cells[0]).X, At(cells[2]).X);
        var top = cut.Find("g.end-marker.top");
        Assert.Equal("2", top.GetAttribute("data-index"));
        Assert.Contains("top", top.TextContent);
        Assert.Contains("bottom to top", cut.Find("figure.stack svg").GetAttribute("aria-label"));
        Assert.Contains("stack", cut.Find(".sequence-kind").TextContent);
        Assert.Empty(cut.FindAll("figure.array"));
    }

    [Fact]
    public void A_push_grows_in_at_the_top_and_the_cells_below_stay_put()
    {
        var cut = Sequence(Visual.Stack, "list", ["1", "2"], ["1", "2", "3"]);
        var before = cut.FindAll("figure.stack g.cell").Select(c => (c.GetAttribute("data-id"), At(c).Y)).ToList();

        cut.Find("button.next").Click();

        var cells = cut.FindAll("figure.stack g.cell:not(.gone)");
        Assert.Equal(["3"], cells.Where(c => c.ClassList.Contains("added")).Select(c => c.QuerySelector(".cell-text")!.TextContent));
        Assert.Contains("animate-cell-in", cells[2].QuerySelector(".cell-body")!.ClassName);
        Assert.Equal(before.Select(b => b.Item1), cells.Take(2).Select(c => c.GetAttribute("data-id")));
        Assert.Equal("2", cut.Find("g.end-marker.top").GetAttribute("data-index"));
        Assert.Empty(cut.FindAll("g.cell.gone"));
    }

    [Fact]
    public void A_pop_leaves_the_top_as_a_ghost_for_one_step_and_says_so()
    {
        var cut = Sequence(Visual.Stack, "list", ["1", "2", "3"], ["1", "2"], ["1", "2"]);
        var topY = At(cut.FindAll("figure.stack g.cell")[2]).Y;

        cut.Find("button.next").Click();

        var ghost = cut.Find("g.cell.gone");
        Assert.Contains("at-back", ghost.ClassName);
        Assert.Equal("3", ghost.QuerySelector(".cell-text")!.TextContent);
        Assert.Equal("true", ghost.GetAttribute("aria-hidden"));
        Assert.Contains("animate-cell-out", ghost.QuerySelector(".cell-body")!.ClassName);
        Assert.Equal(topY, At(ghost).Y);   // it leaves from where the top was
        Assert.Equal("3 popped off the top.", cut.Find(".gone-note").TextContent);
        Assert.Equal(["1", "2"], Texts(cut, "figure.stack g.cell:not(.gone) .cell-text"));
        Assert.Equal("1", cut.Find("g.end-marker.top").GetAttribute("data-index"));

        cut.Find("button.next").Click();

        Assert.Empty(cut.FindAll("g.cell.gone"));
        Assert.Empty(cut.FindAll(".gone-note"));
    }

    [Fact]
    public void An_empty_stack_says_so_and_has_no_top()
    {
        var cut = Sequence(Visual.Stack, "list", ["7"], []);

        cut.Find("button.next").Click();

        Assert.Contains("empty", cut.Find(".sequence-empty").TextContent);
        Assert.Empty(cut.FindAll("g.end-marker"));
        Assert.Contains("empty", cut.Find("figure.stack svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_tall_stack_scrolls_inside_its_own_box()
    {
        var cut = Sequence(Visual.Stack, "list", Enumerable.Range(0, 30).Select(n => n.ToString()).ToArray());

        var box = cut.Find("figure.stack .sequence-scroll").ClassName;
        Assert.Contains("overflow-y-auto", box);
        Assert.Contains("max-h-", box);
    }

    [Fact]
    public void A_queue_runs_front_to_back_with_both_ends_marked()
    {
        var cut = Sequence(Visual.Queue, "deque", ["1", "2", "3"]);

        var cells = cut.FindAll("figure.queue g.cell");
        Assert.True(At(cells[0]).X < At(cells[1]).X && At(cells[1]).X < At(cells[2]).X);
        Assert.Equal("0", cut.Find("g.end-marker.front").GetAttribute("data-index"));
        Assert.Equal("2", cut.Find("g.end-marker.back").GetAttribute("data-index"));
        Assert.Contains("front to back", cut.Find("figure.queue svg").GetAttribute("aria-label"));
        Assert.Contains("overflow-x-auto", cut.Find("figure.queue .sequence-scroll").ClassName);
    }

    [Fact]
    public void A_queue_of_one_marks_it_as_both_front_and_back()
    {
        var cut = Sequence(Visual.Queue, "list", ["1"]);

        Assert.Equal("front · back", cut.Find("g.end-marker.front").TextContent.Trim());
        Assert.Empty(cut.FindAll("g.end-marker.back"));
    }

    [Fact]
    public void A_dequeue_leaves_from_the_front_and_the_rest_slide_up_keeping_their_elements()
    {
        var cut = Sequence(Visual.Queue, "deque", ["1", "2", "3"], ["2", "3"]);
        var two = cut.FindAll("figure.queue g.cell")[1];
        var (id, x) = (two.GetAttribute("data-id"), At(two).X);
        var front = At(cut.FindAll("figure.queue g.cell")[0]).X;

        cut.Find("button.next").Click();

        var ghost = cut.Find("g.cell.gone");
        Assert.Contains("at-front", ghost.ClassName);
        Assert.Equal(("1", front), (ghost.QuerySelector(".cell-text")!.TextContent, At(ghost).X));
        var now = cut.Find($"figure.queue g.cell:not(.gone)[data-id='{id}']");
        Assert.Equal("0", now.GetAttribute("data-index"));
        Assert.True(At(now).X < x);   // it moved to the front by its transform, which is a transition
        Assert.Contains("transition-transform", now.ClassName);
        Assert.Equal("1 dequeued from the front.", cut.Find(".gone-note").TextContent);
    }

    [Fact]
    public void An_enqueue_grows_in_at_the_back_and_appendleft_at_the_front()
    {
        var cut = Sequence(Visual.Queue, "deque", ["1"], ["1", "2"], ["0", "1", "2"]);

        cut.Find("button.next").Click();
        Assert.Equal(["2"], Texts(cut, "figure.queue g.cell.added .cell-text"));
        Assert.Equal("1", cut.Find("g.end-marker.back").GetAttribute("data-index"));

        cut.Find("button.next").Click();
        var added = cut.Find("figure.queue g.cell.added");
        Assert.Equal(("0", "0"), (added.QuerySelector(".cell-text")!.TextContent, added.GetAttribute("data-index")));
    }

    [Fact]
    public void A_pop_off_the_back_of_a_deque_leaves_from_the_back()
    {
        var cut = Sequence(Visual.Queue, "deque", ["1", "2"], ["1"]);

        cut.Find("button.next").Click();

        Assert.Contains("at-back", cut.Find("g.cell.gone").ClassName);
        Assert.Equal("2 taken from the back.", cut.Find(".gone-note").TextContent);
    }

    [Fact]
    public void Pointers_sit_on_stack_and_queue_cells_by_index()
    {
        var run = RunOf(new TraceFrame("line", 1, "<module>", [new("i", "int", "1"), new("j", "int", "9")],
            new Dictionary<string, TrackedArray> { ["s"] = new("list", ["a", "b"], 0), ["q"] = new("deque", ["c", "d"], 0) }));
        var cut = Start(run, [new Visual("s", Visual.Stack, ["i", "j"]), new Visual("q", Visual.Queue, ["i"])], out _);

        var stackPointer = cut.Find("figure.stack g.pointer[data-name='i']");
        Assert.Equal("1", stackPointer.GetAttribute("data-index"));
        Assert.Equal(At(cut.FindAll("figure.stack g.cell")[1]).Y + 18, At(stackPointer).Y);   // level with its cell
        Assert.Equal("1", cut.Find("figure.queue g.pointer[data-name='i']").GetAttribute("data-index"));
        Assert.Contains("j is not on a cell right now.", cut.Find("figure.stack .off-array").TextContent);
    }

    [Fact]
    public void Long_named_pointers_on_one_stack_cell_stand_apart_inside_the_drawing()
    {
        var run = RunOf(new TraceFrame("line", 1, "<module>", [new("read_position", "int", "1"), new("write_position", "int", "1"), new("i", "int", "0")],
            new Dictionary<string, TrackedArray> { ["s"] = new("list", ["a", "b"], 0) }));
        var cut = Start(run, [new Visual("s", Visual.Stack, ["read_position", "write_position", "i"])], out _);

        // A name is drawn 12 units right of its arrow, about .6 of its 15.2-unit size per character.
        static double Right(AngleSharp.Dom.IElement pointer, string name) => At(pointer).X + 12 + name.Length * 15.2 * .6;
        var read = cut.Find("g.pointer[data-name='read_position']");
        var write = cut.Find("g.pointer[data-name='write_position']");
        Assert.True(At(write).X >= Right(read, "read_position"), $"{At(write).X} overlaps the label ending at {Right(read, "read_position")}");
        Assert.Equal(At(read).X, At(cut.Find("g.pointer[data-name='i']")).X);   // the first column, on another cell
        var viewBox = cut.Find("figure.stack svg").GetAttribute("viewBox")!.Split(' ');
        Assert.True(double.Parse(viewBox[2], CultureInfo.InvariantCulture) >= Right(write, "write_position"));   // the scroll box reaches the last name
    }

    [Fact]
    public void Long_named_pointers_on_neighbouring_queue_cells_take_separate_rows_inside_the_drawing()
    {
        var run = RunOf(new TraceFrame("line", 1, "<module>", [new("i", "int", "0"), new("read_position", "int", "1"), new("j", "int", "2")],
            new Dictionary<string, TrackedArray> { ["q"] = new("deque", ["a", "b", "c", "d"], 0) }));
        var cut = Start(run, [new Visual("q", Visual.Queue, ["i", "read_position", "j"])], out _);

        static string Row(IRenderedComponent<TracePlayer> cut, string name) => cut.Find($"g.pointer[data-name='{name}']").GetAttribute("data-row")!;
        Assert.Equal("0", Row(cut, "i"));
        Assert.Equal("1", Row(cut, "read_position"));   // its name is wider than its cell, so it would run into i's and j's
        Assert.Equal("0", Row(cut, "j"));
        var read = At(cut.Find("g.pointer[data-name='read_position']"));
        Assert.True(At(cut.Find("g.pointer[data-name='j']")).Y < read.Y);

        // The name, centred on its arrow, stays inside the drawing on both sides.
        var viewBox = cut.Find("figure.queue svg").GetAttribute("viewBox")!.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var half = "read_position".Length * 15.2 * .6 / 2;
        Assert.True(viewBox[0] <= read.X - half && read.X + half <= viewBox[0] + viewBox[2]);
    }

    [Fact]
    public void A_sequence_is_sized_in_rem_with_its_text_on_the_same_scale()
    {
        foreach (var kind in new[] { Visual.Stack, Visual.Queue })
        {
            var cut = Sequence(kind, "list", ["1234567", "1"]);
            var svg = cut.Find("figure.sequence svg");
            var viewBox = svg.GetAttribute("viewBox")!.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

            // 16 user units to 1rem, both ways, so the boxes grow with the text.
            Assert.Equal($"width: {(viewBox[2] / 16).ToString(CultureInfo.InvariantCulture)}rem; height: {(viewBox[3] / 16).ToString(CultureInfo.InvariantCulture)}rem", svg.GetAttribute("style"));
            Assert.Null(svg.GetAttribute("width"));
            Assert.All(svg.QuerySelectorAll("text"), text =>
            {
                Assert.DoesNotContain("rem", text.ClassName ?? "");
                Assert.NotNull(text.GetAttribute("font-size"));
            });

            // The longest value a cell shows fits inside its 60-unit box.
            var longest = svg.QuerySelectorAll(".cell-text").Single(t => t.TextContent == "1234567");
            Assert.True(7 * .6 * double.Parse(longest.GetAttribute("font-size")!, CultureInfo.InvariantCulture) <= 56);
        }
    }

    [Fact]
    public void Sizes_are_written_the_same_whatever_the_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var cut = Sequence(Visual.Stack, "list", ["1234567"]);
            Assert.DoesNotContain(",", cut.Find("figure.stack svg").GetAttribute("style"));
            Assert.All(cut.FindAll("figure.stack svg text"), t => Assert.DoesNotContain(",", t.GetAttribute("font-size")));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_cut_off_queue_marks_its_back_at_the_count_of_the_rest()
    {
        var run = RunOf(Seq(1, "x", "deque", ["1", "2"], more: 5));
        var cut = Start(run, [new Visual("x", Visual.Queue, [])], out _);

        Assert.Contains("+5 more", cut.Find("figure.queue g.more").TextContent);
        Assert.Equal(At(cut.Find("g.end-marker.back")).X, At(cut.FindAll("figure.queue g.cell")[0]).X + 2 * 64 + 30);   // the middle of the third place
    }

    // ---- Linked lists ----

    private static readonly IReadOnlyList<Visual> HeadVisual = [new Visual("head", Visual.LinkedList, ["p", "q"])];

    private static TrackedList Linked(int? head, int more, (string Name, int? Node)[] pointers, params (int Id, string? Value, int? Next)[] nodes) =>
        new(head, nodes.Select(n => new ListNode(n.Id, n.Value, n.Next)).ToList(), more, pointers.ToDictionary(p => p.Name, p => p.Node));

    private static TrackedList Linked(int? head, params (int Id, string? Value, int? Next)[] nodes) => Linked(head, 0, [], nodes);

    private static TraceFrame ListFrame(int line, TrackedList list) =>
        new("line", line, "<module>", [], new Dictionary<string, TrackedArray>(), null, new Dictionary<string, TrackedList> { ["head"] = list });

    private IRenderedComponent<TracePlayer> Lists(params TrackedList[] steps) =>
        Start(RunOf(steps.Select((list, i) => ListFrame(i + 1, list)).ToArray()), HeadVisual, out _);

    /// <summary>The (x, y) a node, None or pointer is placed at by its CSS transform.</summary>
    private static (double X, double Y) Place(AngleSharp.Dom.IElement element)
    {
        var match = System.Text.RegularExpressions.Regex.Match(element.GetAttribute("style")!, @"translate\((-?[\d.]+)px, (-?[\d.]+)px\)");
        return (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static AngleSharp.Dom.IElement Node(IRenderedComponent<TracePlayer> cut, int id) => cut.Find($"figure.linked-list g.node[data-id='{id}']");

    [Fact]
    public void A_linked_list_is_a_row_of_nodes_joined_by_arrows_that_end_at_none()
    {
        var cut = Lists(Linked(1, (1, "1", 2), (2, "2", 3), (3, "3", null)));

        var nodes = cut.FindAll("figure.linked-list g.node");
        Assert.Equal(["1", "2", "3"], nodes.Select(n => n.QuerySelector(".node-value")!.TextContent));
        Assert.True(Place(nodes[0]).X < Place(nodes[1]).X && Place(nodes[1]).X < Place(nodes[2]).X);
        Assert.All(nodes, n => Assert.Equal(Place(nodes[0]).Y, Place(n).Y));
        Assert.Equal(3, cut.FindAll("figure.linked-list g.node g.link").Count);   // the last one's arrow points at None
        Assert.True(Place(cut.Find("g.list-none")).X > Place(nodes[2]).X);
        Assert.Equal("None", cut.Find("g.list-none").TextContent.Trim());
        Assert.Contains("linked list", cut.Find(".list-kind").TextContent);
        Assert.Contains("1 → 2 → 3 → None", cut.Find("figure.linked-list svg").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".detached-note"));
        Assert.Equal("head", Pointer(cut, "head", "data-name"));
        Assert.Equal("1", Pointer(cut, "head", "data-node"));
    }

    [Fact]
    public void An_empty_list_is_its_variable_at_none()
    {
        var cut = Lists(Linked(null));

        Assert.Empty(cut.FindAll("figure.linked-list g.node"));
        var none = Place(cut.Find("g.list-none"));
        var head = cut.Find("figure.linked-list g.pointer[data-name='head']");
        Assert.Equal("", head.GetAttribute("data-node") ?? "");
        Assert.True(Place(head).X > none.X && Place(head).X < none.X + 40);   // over the middle of the word None
        Assert.Contains("head, a linked list: None", cut.Find("figure.linked-list svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_cycle_is_an_arrow_back_under_the_row_and_has_no_none()
    {
        var cut = Lists(Linked(1, (1, "a", 2), (2, "b", 3), (3, "c", 2)));

        var back = cut.Find("figure.linked-list g.link.back");
        Assert.Equal("3", back.GetAttribute("data-from"));
        Assert.Equal("2", back.GetAttribute("data-to"));
        Assert.Empty(cut.FindAll("g.list-none"));
        Assert.Equal(2, cut.FindAll("figure.linked-list g.node g.link").Count);   // the last node's arrow is the one back
        var nodeBottom = Place(Node(cut, 2)).Y + 40;
        Assert.Matches(@"V(\d+(\.\d+)?)", back.QuerySelector(".link-line")!.GetAttribute("d"));
        var lane = double.Parse(System.Text.RegularExpressions.Regex.Match(back.QuerySelector(".link-line")!.GetAttribute("d")!, @"V([\d.]+)").Groups[1].Value,
            CultureInfo.InvariantCulture);
        Assert.True(lane > nodeBottom);   // it runs under the nodes
        Assert.Contains("a → b → c → back to b", cut.Find("figure.linked-list svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_pointer_holding_none_when_no_row_ends_at_none_stands_on_a_none_of_its_own()
    {
        var cut = Lists(Linked(1, 0, [("q", null)], (1, "a", 2), (2, "b", 1)));

        var none = cut.Find("g.list-none.spare");
        Assert.Equal("None", none.TextContent.Trim());
        Assert.True(Place(none).X > Place(Node(cut, 2)).X + 74);   // past the last node and the turn of the arrow back
        var q = cut.Find("figure.linked-list g.pointer[data-name='q']");
        Assert.True(Place(q).X > Place(none).X && Place(q).X < Place(none).X + 45);
    }

    [Fact]
    public void Nodes_only_a_pointer_reaches_are_drawn_apart_and_dashed()
    {
        var list = Linked(1, 0, [("p", 5)], (1, "1", null), (5, "5", 6), (6, "6", 2), (2, "2", null));
        var cut = Lists(list with { Nodes = [new(1, "1", 2), new(2, "2", null), new(5, "5", 6), new(6, "6", 2)] });

        Assert.DoesNotContain("detached", Node(cut, 1).ClassName);
        Assert.Contains("detached", Node(cut, 5).ClassName);
        Assert.Contains("[stroke-dasharray:4_3]", Node(cut, 5).QuerySelector(".value-box")!.GetAttribute("class"));
        Assert.True(Place(Node(cut, 5)).Y > Place(Node(cut, 1)).Y);
        Assert.Equal(Place(Node(cut, 1)).X, Place(Node(cut, 5)).X);
        var join = cut.Find("figure.linked-list g.link.join");
        Assert.Equal("6", join.GetAttribute("data-from"));
        Assert.Equal("2", join.GetAttribute("data-to"));
        Assert.Equal("5", Pointer(cut, "p", "data-node"));
        Assert.Equal("1", Pointer(cut, "p", "data-row"));
        Assert.Single(cut.FindAll(".detached-note"));
        Assert.Contains("not reachable from it: 5 → 6 → on to 2", cut.Find("figure.linked-list svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void Pointers_stand_over_their_nodes_and_names_on_one_node_do_not_overlap()
    {
        var cut = Lists(Linked(1, 0, [("p", 1), ("q", null)], (1, "1", 2), (2, "2", null)));

        var head = cut.Find("figure.linked-list g.pointer[data-name='head']");
        var p = cut.Find("figure.linked-list g.pointer[data-name='p']");
        var q = cut.Find("figure.linked-list g.pointer[data-name='q']");
        Assert.Equal(Place(head).X, Place(p).X);
        Assert.NotEqual(head.GetAttribute("data-label-row"), p.GetAttribute("data-label-row"));
        Assert.True(Place(head).Y < Place(Node(cut, 1)).Y);
        Assert.True(Place(q).X > Place(cut.Find("g.list-none")).X);   // q holds None, so it stands on None
        Assert.Contains("p at the node holding 1, q at None", cut.Find("figure.linked-list svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_pointer_on_a_node_past_the_limit_is_named_under_the_list()
    {
        var cut = Lists(Linked(1, 4, [("p", 99)], (1, "1", 2), (2, "2", 3)));

        Assert.Contains("+4 more", cut.Find("g.list-more").TextContent);
        Assert.Empty(cut.FindAll("figure.linked-list g.pointer[data-name='p']"));
        Assert.Contains("p is on a node past the ones shown", cut.Find(".off-list").TextContent);
    }

    [Fact]
    public void A_rewired_arrow_draws_itself_in_and_a_changed_value_flashes()
    {
        var cut = Lists(Linked(1, (1, "1", 2), (2, "2", null)), Linked(1, (1, "1", null)), Linked(1, (1, "7", null)));

        Assert.Empty(cut.FindAll("figure.linked-list .animate-link-in"));
        cut.Find("button.next").Click();

        var link = Node(cut, 1).QuerySelector("g.link")!;
        Assert.Contains("relinked", link.ClassName);
        Assert.Contains("animate-link-in", link.QuerySelector(".link-line")!.GetAttribute("class"));
        Assert.Equal("1", link.QuerySelector(".link-line")!.GetAttribute("pathLength"));
        Assert.DoesNotContain("animate-cell-flash", Node(cut, 1).QuerySelector(".value-box")!.GetAttribute("class"));

        cut.Find("button.next").Click();

        Assert.Contains("animate-cell-flash", Node(cut, 1).QuerySelector(".value-box")!.GetAttribute("class"));
        Assert.Empty(cut.FindAll("figure.linked-list .animate-link-in"));
    }

    [Fact]
    public void A_node_keeps_its_element_and_slides_to_its_new_place()
    {
        var cut = Lists(Linked(1, (1, "1", 2), (2, "2", null)), Linked(2, (2, "2", 1), (1, "1", null)));
        var before = Place(Node(cut, 2)).X;

        cut.Find("button.next").Click();

        Assert.True(Place(Node(cut, 2)).X < before);
        Assert.Contains("transition-transform", Node(cut, 2).ClassName);
        Assert.Equal("2", Pointer(cut, "head", "data-node"));
        Assert.Contains("moved", cut.Find("figure.linked-list g.pointer[data-name='head']").ClassName);
    }

    [Fact]
    public void A_new_node_grows_in()
    {
        var cut = Lists(Linked(1, (1, "1", null)), Linked(1, (1, "1", 2), (2, "2", null)));
        cut.Find("button.next").Click();

        Assert.Contains("added", Node(cut, 2).ClassName);
        Assert.Contains("animate-cell-in", Node(cut, 2).QuerySelector(".node-body")!.GetAttribute("class"));
        Assert.DoesNotContain("animate-cell-in", Node(cut, 1).QuerySelector(".node-body")!.GetAttribute("class"));
    }

    [Fact]
    public void A_list_is_sized_in_rem_and_written_the_same_whatever_the_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var cut = Lists(Linked(1, 0, [("q", null)], (1, "1234567", 2), (2, "x", null)));
            var svg = cut.Find("figure.linked-list svg");
            Assert.Matches(@"^width: [\d.]+rem; height: [\d.]+rem$", svg.GetAttribute("style"));
            Assert.All(cut.FindAll("figure.linked-list svg text"), t => Assert.DoesNotContain(",", t.GetAttribute("font-size")));
            Assert.All(cut.FindAll("figure.linked-list svg g[style]"), g => Assert.DoesNotContain(",", g.GetAttribute("style")!.Replace("px, ", "")));
            Assert.Equal("12345…", Node(cut, 1).QuerySelector(".node-value")!.TextContent);
            Assert.Contains("overflow-x-auto", cut.Find("figure.linked-list .list-scroll").ClassName);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_wide_name_on_the_first_node_moves_the_drawing_s_left_edge_rather_than_being_cut_off()
    {
        var run = RunOf(new TraceFrame("line", 1, "<module>", [], new Dictionary<string, TrackedArray>(), null,
            new Dictionary<string, TrackedList> { ["a_very_long_list_name"] = Linked(1, (1, "1", null)) }));
        var cut = Start(run, [new Visual("a_very_long_list_name", Visual.LinkedList, [])], out _);

        var viewBox = cut.Find("figure.linked-list svg").GetAttribute("viewBox")!.Split(' ');
        Assert.True(int.Parse(viewBox[0]) < 0);
    }

    // ---- Trees ----

    private static readonly IReadOnlyList<Visual> RootVisual = [new Visual("root", Visual.Tree, ["node"], Visited: "order")];

    private static TrackedTree Tree(int? root, (string Name, int? Node)[] pointers, TreeVisited? visited, params (int Id, string? Value, int? Left, int? Right)[] nodes) =>
        new(root, nodes.Select(n => new TreeNode(n.Id, n.Value, n.Left, n.Right)).ToList(), 0, pointers.ToDictionary(p => p.Name, p => p.Node), visited);

    private static TrackedTree Tree(int? root, params (int Id, string? Value, int? Left, int? Right)[] nodes) => Tree(root, [], null, nodes);

    private static TraceFrame TreeFrame(int line, TrackedTree tree) =>
        new("line", line, "<module>", [], new Dictionary<string, TrackedArray>(), Trees: new Dictionary<string, TrackedTree> { ["root"] = tree });

    private IRenderedComponent<TracePlayer> Trees(params TrackedTree[] steps) =>
        Start(RunOf(steps.Select((tree, i) => TreeFrame(i + 1, tree)).ToArray()), RootVisual, out _);

    private static AngleSharp.Dom.IElement TreeNodeOf(IRenderedComponent<TracePlayer> cut, int id) => cut.Find($"figure.tree g.node[data-id='{id}']");

    private static readonly (int, string?, int?, int?)[] SmallTree =
        [(4, "4", 2, 6), (2, "2", 1, 3), (6, "6", null, null), (1, "1", null, null), (3, "3", null, null)];

    [Fact]
    public void A_tree_draws_each_level_lower_and_each_node_in_its_in_order_column()
    {
        var cut = Trees(Tree(4, SmallTree));

        var nodes = cut.FindAll("figure.tree g.node");
        Assert.Equal(["1", "2", "3", "4", "6"], nodes.Select(n => n.QuerySelector(".node-value")!.TextContent));
        var places = nodes.Select(Place).ToList();
        Assert.True(places.Zip(places.Skip(1)).All(p => p.First.X < p.Second.X));
        Assert.True(Place(TreeNodeOf(cut, 4)).Y < Place(TreeNodeOf(cut, 2)).Y && Place(TreeNodeOf(cut, 2)).Y < Place(TreeNodeOf(cut, 1)).Y);
        Assert.Equal(Place(TreeNodeOf(cut, 2)).Y, Place(TreeNodeOf(cut, 6)).Y);
        Assert.Equal(4, cut.FindAll("figure.tree g.edge").Count);   // None children are not drawn
        Assert.Equal("6", cut.Find("figure.tree g.edge[data-from='4'][data-side='right']").GetAttribute("data-to"));
        Assert.Contains("binary tree", cut.Find(".tree-kind").TextContent);
        Assert.Contains("level 0: 4 (left 2, right 6); level 1: 2 (left 1, right 3), 6", cut.Find("figure.tree svg").GetAttribute("aria-label"));
        var root = cut.Find("figure.tree g.pointer[data-name='root']");
        Assert.Equal("4", root.GetAttribute("data-node"));
        Assert.Equal(Place(TreeNodeOf(cut, 4)).X, Place(root).X);
        Assert.True(Place(root).Y > Place(TreeNodeOf(cut, 4)).Y);   // under the node
    }

    [Fact]
    public void An_empty_tree_is_its_variable_at_none()
    {
        var cut = Trees(Tree(null));

        Assert.Empty(cut.FindAll("figure.tree g.node"));
        Assert.Equal("None", cut.Find("g.tree-none").TextContent.Trim());
        Assert.Equal("", cut.Find("figure.tree g.pointer[data-name='root']").GetAttribute("data-node") ?? "");
        Assert.Contains("root, a binary tree: empty (None)", cut.Find("figure.tree svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void An_inserted_node_grows_in_the_others_slide_aside_and_a_changed_value_flashes()
    {
        var cut = Trees(Tree(4, (4, "4", 2, null), (2, "2", null, null)),
                        Tree(4, (4, "40", 2, null), (2, "2", null, 3), (3, "3", null, null)));
        var before = Place(TreeNodeOf(cut, 4));
        Assert.Empty(cut.FindAll("figure.tree .animate-cell-in, figure.tree .animate-cell-flash"));

        cut.Find("button.next").Click();

        Assert.Contains("added", TreeNodeOf(cut, 3).ClassList);
        Assert.NotNull(TreeNodeOf(cut, 3).QuerySelector(".node-body.animate-cell-in"));
        Assert.True(Place(TreeNodeOf(cut, 4)).X > before.X);   // 3 is between 2 and 4 in order, so 4 moves right
        Assert.Contains("transition-transform", TreeNodeOf(cut, 4).ClassList);
        Assert.NotNull(TreeNodeOf(cut, 4).QuerySelector(".node-circle.animate-cell-flash"));
        Assert.Null(TreeNodeOf(cut, 2).QuerySelector(".animate-cell-flash"));
    }

    [Fact]
    public void A_visited_list_shades_its_nodes_numbers_them_and_pops_the_new_one()
    {
        TreeVisited Marks(params (int Id, int Order)[] marks) => new(true, marks.Select(m => new VisitedNode(m.Id, m.Order)).ToList(), 1);
        var cut = Trees(Tree(4, [], Marks((1, 1)), SmallTree), Tree(4, [], Marks((1, 1), (2, 2)), SmallTree));
        cut.Find("button.next").Click();

        Assert.Equal(["1", "2"], cut.FindAll("figure.tree g.visit-order").Select(o => o.TextContent.Trim()));
        Assert.Contains("visited", TreeNodeOf(cut, 2).ClassList);
        Assert.Contains("fill-accent-tint", TreeNodeOf(cut, 2).QuerySelector(".node-circle")!.ClassList);
        Assert.NotNull(TreeNodeOf(cut, 2).QuerySelector(".visit-order .animate-pop"));
        Assert.Null(TreeNodeOf(cut, 1).QuerySelector(".visit-order .animate-pop"));
        var notes = cut.FindAll(".tree-note").Select(n => n.TextContent).ToList();
        Assert.Contains("Shaded: the nodes in order, numbered in its order.", notes);
        Assert.Contains("1 member of order match no node shown.", notes);
        Assert.Contains("2 (left 1, right 3), visited 2", cut.Find("figure.tree svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_detached_subtree_stands_to_the_right_dashed_and_a_pointer_at_none_is_noted()
    {
        var cut = Start(RunOf(TreeFrame(1, Tree(1, [("node", 8), ("other", null)], null, (1, "a", null, null), (8, "x", null, null)))),
            [new Visual("root", Visual.Tree, ["node", "other"])], out _);

        var loose = TreeNodeOf(cut, 8);
        Assert.Contains("detached", loose.ClassList);
        Assert.True(Place(loose).X > Place(TreeNodeOf(cut, 1)).X + 46);
        Assert.Equal(Place(loose).X, Place(cut.Find("figure.tree g.pointer[data-name='node']")).X);
        var notes = cut.FindAll(".tree-note").Select(n => n.TextContent).ToList();
        Assert.Contains("The dashed nodes can't be reached from root; only a pointer holds them.", notes);
        Assert.Contains("other is None.", notes);
    }

    [Fact]
    public void A_shared_node_is_a_dashed_link_and_a_child_past_the_ones_shown_is_a_stub()
    {
        var cut = Trees(Tree(1, (1, "a", 2, 3), (2, "b", null, 9), (3, "c", 2, null)) with { More = 4 });

        Assert.Equal("2", cut.Find("figure.tree g.edge.elsewhere").GetAttribute("data-to"));
        Assert.Equal("3", cut.Find("figure.tree g.edge.elsewhere").GetAttribute("data-from"));
        Assert.Equal("2", cut.Find("figure.tree g.edge.more").GetAttribute("data-from"));
        Assert.Contains("+4 more nodes not shown.", cut.FindAll(".tree-note").Select(n => n.TextContent));
    }

    [Fact]
    public void Two_pointers_on_one_node_stack_their_names()
    {
        var cut = Start(RunOf(TreeFrame(1, Tree(1, [("node", 1)], null, (1, "a", null, null)))), RootVisual, out _);

        var root = cut.Find("figure.tree g.pointer[data-name='root']");
        var node = cut.Find("figure.tree g.pointer[data-name='node']");
        Assert.Equal(("0", "1"), (root.GetAttribute("data-row"), node.GetAttribute("data-row")));
        Assert.True(Place(node).Y > Place(root).Y);
    }

    // ---- The call stack ----

    private static readonly IReadOnlyList<Visual> StackVisual = [new Visual("", Visual.CallStack, [])];

    private static RecordedCall Call(int id, int n, string? returning = null) => new(id, "fact", [new CallArg("n", n.ToString())], returning);

    private static TraceFrame CallFrame(string ev, int? line, TrackedCalls calls) =>
        new(ev, line, calls.Frames.Count > 0 ? "fact" : "<module>", [], new Dictionary<string, TrackedArray>(), Calls: calls);

    // fact(2) from the module: it calls fact(1), which returns 1 to it, and it returns 2 to the module.
    private static readonly TraceResult FactRun = RunOf(
        CallFrame("line", 4, new([], 0, null)),
        CallFrame("line", 2, new([Call(1, 2)], 0, null)),
        CallFrame("line", 2, new([Call(1, 2), Call(2, 1)], 0, null)),
        CallFrame("return", 3, new([Call(1, 2), Call(2, 1, "1")], 0, null)),
        CallFrame("line", 3, new([Call(1, 2)], 0, new("fact", "1", 1))),
        CallFrame("return", 3, new([Call(1, 2, "2")], 0, null)),
        CallFrame("return", null, new([], 0, new("fact", "2", null))));

    private IRenderedComponent<TracePlayer> FactAt(int step)
    {
        var cut = Start(FactRun, StackVisual, out _);
        for (var n = 1; n < step; n++) cut.Find("button.next").Click();
        return cut;
    }

    private static AngleSharp.Dom.IElement CallOf(IRenderedComponent<TracePlayer> cut, int id) => cut.Find($"figure.call-stack g.call[data-id='{id}']");

    [Fact]
    public void Before_any_call_the_stack_is_the_module_alone()
    {
        var cut = FactAt(1);

        Assert.Empty(cut.FindAll("figure.call-stack g.call"));
        Assert.Equal("module", cut.Find("figure.call-stack g.call-base").TextContent.Trim());
        Assert.Empty(cut.FindAll("figure.call-stack .running-marker"));
        Assert.Contains("no calls under way", cut.Find("figure.call-stack svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void Calls_stack_up_with_the_running_one_on_top_and_marked()
    {
        var cut = FactAt(3);

        var calls = cut.FindAll("figure.call-stack g.call");
        Assert.Equal(["fact(n=2)", "fact(n=1)"], calls.Select(c => c.QuerySelector(".frame-label")!.TextContent));
        Assert.True(Place(CallOf(cut, 2)).Y < Place(CallOf(cut, 1)).Y);   // the newer call is drawn above
        Assert.Contains("running", CallOf(cut, 2).ClassList);
        Assert.DoesNotContain("running", CallOf(cut, 1).ClassList);
        Assert.Contains("stroke-accent", CallOf(cut, 2).QuerySelector(".frame-box")!.ClassName);
        Assert.Equal("2", cut.Find("figure.call-stack .running-marker").GetAttribute("data-id"));
        Assert.Contains("fact(n=2); fact(n=1) (running)", cut.Find("figure.call-stack svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_new_call_grows_in_and_the_calls_under_it_keep_their_place()
    {
        var cut = FactAt(2);
        var before = Place(CallOf(cut, 1));
        Assert.Contains("animate-cell-in", CallOf(cut, 1).QuerySelector(".frame-body")!.ClassName);

        cut.Find("button.next").Click();

        Assert.Contains("animate-cell-in", CallOf(cut, 2).QuerySelector(".frame-body")!.ClassName);
        Assert.DoesNotContain("animate-cell-in", CallOf(cut, 1).QuerySelector(".frame-body")!.ClassName);
        Assert.Contains("transition-transform", CallOf(cut, 1).ClassName);
        Assert.Equal(before.X, Place(CallOf(cut, 1)).X);
    }

    [Fact]
    public void A_returning_call_says_what_it_returns()
    {
        var cut = FactAt(4);

        Assert.Equal("returns 1", CallOf(cut, 2).QuerySelector(".returning")!.TextContent.Trim());
        Assert.Contains("returning", CallOf(cut, 2).ClassList);
        Assert.Null(CallOf(cut, 1).QuerySelector(".returning"));
        Assert.Contains("fact(n=1) (running), returning 1", cut.Find("figure.call-stack svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void The_value_returned_pops_up_on_the_caller_while_the_returned_call_leaves()
    {
        var cut = FactAt(5);

        var received = CallOf(cut, 1).QuerySelector("g.received")!;
        Assert.Equal("← 1", received.TextContent.Trim());
        Assert.Equal("fact", received.GetAttribute("data-from"));
        Assert.Contains("animate-pop", received.InnerHtml);
        var gone = cut.Find("figure.call-stack g.call.gone");
        Assert.Equal("2", gone.GetAttribute("data-id"));
        Assert.Contains("animate-cell-out", gone.QuerySelector(".frame-body")!.ClassName);
        Assert.True(Place(gone).Y < Place(CallOf(cut, 1)).Y);   // it rises away above the stack
        Assert.Equal("fact(n=1) returned 1 to fact(n=2).", cut.Find(".call-note").TextContent);
        Assert.Contains("got 1 back from fact", cut.Find("figure.call-stack svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void The_last_value_goes_back_to_the_module()
    {
        var cut = FactAt(7);

        Assert.Empty(cut.FindAll("figure.call-stack g.call:not(.gone)"));
        Assert.Equal("← 2", cut.Find("figure.call-stack g.call-base g.received").TextContent.Trim());
        Assert.Equal("fact(n=2) returned 2 to the module.", cut.Find(".call-note").TextContent);
        Assert.Contains("fact returned 2 to the module", cut.Find("figure.call-stack svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void Calls_beyond_those_shown_are_counted_in_place_of_the_module()
    {
        var frames = Enumerable.Range(4, 12).Select(id => Call(id, 20 - id)).ToList();
        var cut = Start(RunOf(CallFrame("line", 2, new(frames, 3, null))), StackVisual, out _);

        Assert.Equal(12, cut.FindAll("figure.call-stack g.call").Count);
        Assert.Equal("+3 more calls below", cut.Find("figure.call-stack g.call-base").TextContent.Trim());
        Assert.Contains("(3 more below)", cut.Find("figure.call-stack svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_call_left_by_an_exception_is_said_to_end_without_returning()
    {
        var cut = Start(RunOf(
            CallFrame("line", 2, new([Call(1, 2)], 0, null)),
            CallFrame("line", 5, new([], 0, null))), StackVisual, out _);
        cut.Find("button.next").Click();

        Assert.Equal("fact(n=2) ended without returning (an exception).", cut.Find(".call-note").TextContent);
    }

    [Fact]
    public void The_drawing_is_sized_in_rem_and_scrolls_sideways_when_it_is_wide()
    {
        var cut = FactAt(3);

        Assert.Matches(@"^width: [\d.]+rem; height: [\d.]+rem$", cut.Find("figure.call-stack svg").GetAttribute("style"));
        Assert.Contains("overflow-auto", cut.Find(".call-stack-scroll").ClassName);
    }

    [Fact]
    public void The_call_stack_is_drawn_alongside_the_other_visuals()
    {
        var frame = Frame(2, 0, "3", "1", "2") with { Calls = new([Call(1, 2)], 0, null) };
        var cut = Start(RunOf(frame), [.. Visuals, new Visual("", Visual.CallStack, [])], out _);

        Assert.Single(cut.FindAll("figure.call-stack"));
        Assert.Equal(3, cut.FindAll("g.cell").Count);
    }

    // ---- Playback ----

    private static string Position(IRenderedComponent<TracePlayer> cut) => cut.Find(".position").TextContent;

    private IRenderedComponent<TracePlayer> Play(out BunitJSModuleInterop module)
    {
        var cut = Open(out module);
        cut.Find("button.play").Click();
        return cut;
    }

    private void Tick(IRenderedComponent<TracePlayer> cut) => cut.InvokeAsync(PlaybackClock.Tick);

    [Fact]
    public void Nothing_plays_until_the_learner_asks()
    {
        var cut = Open(out _);

        Assert.Empty(PlaybackClock.Live);
        Assert.Equal("Play", cut.Find("button.play").TextContent.Trim().TrimStart('▶', ' '));
        Assert.Equal("false", cut.Find("button.play").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Play_steps_forward_on_every_tick_once_a_second()
    {
        var cut = Play(out _);

        var timer = Assert.Single(PlaybackClock.Live);
        Assert.Equal(TimeSpan.FromSeconds(1), timer.Period);
        Assert.Equal("true", cut.Find("button.play").GetAttribute("aria-pressed"));
        Assert.Contains("Pause", cut.Find("button.play").TextContent);

        Tick(cut);
        Assert.Equal("Step 2 of 4", Position(cut));
        Tick(cut);
        Assert.Equal("Step 3 of 4", Position(cut));
    }

    [Fact]
    public void Playback_stops_on_the_last_step_and_the_button_offers_to_replay()
    {
        var cut = Play(out _);

        Tick(cut); Tick(cut); Tick(cut);

        Assert.Equal("Step 4 of 4", Position(cut));
        Assert.Empty(PlaybackClock.Live);
        Assert.Contains("Replay", cut.Find("button.play").TextContent);
        Assert.Equal("false", cut.Find("button.play").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Replay_starts_over_from_the_first_step_and_plays()
    {
        var cut = Play(out _);
        Tick(cut); Tick(cut); Tick(cut);

        cut.Find("button.play").Click();

        Assert.Equal("Step 1 of 4", Position(cut));
        Assert.Single(PlaybackClock.Live);
        Tick(cut);
        Assert.Equal("Step 2 of 4", Position(cut));
    }

    [Fact]
    public void Pause_stops_the_clock_and_keeps_the_place()
    {
        var cut = Play(out _);
        Tick(cut);

        cut.Find("button.play").Click();

        Assert.Empty(PlaybackClock.Live);
        Assert.Equal("Step 2 of 4", Position(cut));
        Assert.Contains("Play", cut.Find("button.play").TextContent);
    }

    [Fact]
    public void Playing_again_after_a_pause_carries_on_from_where_it_was()
    {
        var cut = Play(out _);
        Tick(cut);
        cut.Find("button.play").Click();

        cut.Find("button.play").Click();
        Tick(cut);

        Assert.Equal("Step 3 of 4", Position(cut));
    }

    [Fact]
    public void Stepping_by_hand_pauses_playback()
    {
        var cut = Play(out _);

        cut.Find("button.next").Click();

        Assert.Empty(PlaybackClock.Live);
        Assert.Equal("Step 2 of 4", Position(cut));
        cut.Find("button.play").Click();
        cut.Find("button.prev").Click();
        Assert.Empty(PlaybackClock.Live);
    }

    [Fact]
    public void Speed_defaults_to_normal_and_offers_slower_and_faster()
    {
        var cut = Open(out _);

        Assert.Equal(["0.5×", "1×", "2×"], cut.FindAll("button.speed").Select(b => b.TextContent.Trim()));
        Assert.Equal("true", cut.Find("button.speed[data-speed='1']").GetAttribute("aria-pressed"));
        Assert.Equal("false", cut.Find("button.speed[data-speed='2']").GetAttribute("aria-pressed"));
    }

    [Theory]
    [InlineData("0.5", 2000)]
    [InlineData("1", 1000)]
    [InlineData("2", 500)]
    public void The_speed_sets_the_time_between_steps(string speed, int milliseconds)
    {
        var cut = Open(out _);
        cut.Find($"button.speed[data-speed='{speed}']").Click();

        cut.Find("button.play").Click();

        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), Assert.Single(PlaybackClock.Live).Period);
        Assert.Equal("true", cut.Find($"button.speed[data-speed='{speed}']").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Changing_the_speed_while_playing_takes_effect_without_losing_the_place()
    {
        var cut = Play(out _);
        Tick(cut);

        cut.Find("button.speed[data-speed='2']").Click();

        Assert.Equal(TimeSpan.FromMilliseconds(500), Assert.Single(PlaybackClock.Live).Period);
        Assert.Equal("Step 2 of 4", Position(cut));
        Tick(cut);
        Assert.Equal("Step 3 of 4", Position(cut));
    }

    [Fact]
    public void Changing_the_speed_while_paused_does_not_start_playing()
    {
        var cut = Open(out _);

        cut.Find("button.speed[data-speed='2']").Click();

        Assert.Empty(PlaybackClock.Live);
        Assert.Equal("Step 1 of 4", Position(cut));
    }

    [Fact]
    public void A_new_trace_stops_playback_and_starts_again_at_the_first_step()
    {
        var cut = Play(out _);
        Tick(cut);

        cut.Render(p => p.Add(c => c.Code, Code).Add(c => c.States, TraceModel.Build(Result, Visuals)).Add(c => c.Result, Result));

        Assert.Empty(PlaybackClock.Live);
        Assert.Equal("Step 1 of 4", Position(cut));
        Assert.Contains("Play", cut.Find("button.play").TextContent);
    }

    [Fact]
    public void Rendering_again_with_the_same_trace_keeps_playing()
    {
        var states = TraceModel.Build(Result, Visuals);
        var cut = Show(Editor(), states, Result);
        cut.Find("button.play").Click();

        cut.Render(p => p.Add(c => c.Code, Code).Add(c => c.States, states).Add(c => c.Result, Result));

        Assert.Single(PlaybackClock.Live);
        Tick(cut);
        Assert.Equal("Step 2 of 4", Position(cut));
    }

    [Fact]
    public async Task Leaving_the_step_stops_the_clock_and_the_shortcuts()
    {
        var cut = Play(out _);

        await DisposeComponentsAsync();

        Assert.Empty(PlaybackClock.Live);
        await Press(StepCommand.PlayPause);   // nobody is listening any more
        Assert.Empty(PlaybackClock.Timers.Where(t => !t.Disposed));
    }

    [Fact]
    public void A_tick_that_arrives_after_pausing_does_nothing()
    {
        var cut = Play(out _);
        Tick(cut);
        cut.Find("button.play").Click();

        cut.InvokeAsync(PlaybackClock.TickEverything);

        Assert.Equal("Step 2 of 4", Position(cut));
    }

    [Fact]
    public async Task A_trace_with_one_step_has_nothing_to_play()
    {
        var run = RunOf(Frame(null, 0, "1"));
        var cut = Open(run, out _);

        Assert.True(cut.Find("button.play").HasAttribute("disabled"));
        await Press(StepCommand.PlayPause);
        Assert.Empty(PlaybackClock.Timers);
    }

    // ---- Keyboard ----

    [Fact]
    public async Task The_right_and_left_arrows_step_forward_and_back()
    {
        var cut = Open(out _);

        await Press(StepCommand.Next);
        await Press(StepCommand.Next);
        Assert.Equal("Step 3 of 4", Position(cut));

        await Press(StepCommand.Previous);
        Assert.Equal("Step 2 of 4", Position(cut));
    }

    [Fact]
    public async Task The_arrows_stop_at_both_ends()
    {
        var cut = Open(out _);

        await Press(StepCommand.Previous);
        Assert.Equal("Step 1 of 4", Position(cut));

        for (var n = 0; n < 6; n++) await Press(StepCommand.Next);
        Assert.Equal("Step 4 of 4", Position(cut));
    }

    [Fact]
    public async Task Space_plays_and_pauses()
    {
        var cut = Open(out _);

        await Press(StepCommand.PlayPause);
        Assert.Single(PlaybackClock.Live);
        Tick(cut);
        Assert.Equal("Step 2 of 4", Position(cut));

        await Press(StepCommand.PlayPause);
        Assert.Empty(PlaybackClock.Live);
        Assert.Equal("Step 2 of 4", Position(cut));
    }

    [Fact]
    public async Task Space_on_the_last_step_replays_from_the_start()
    {
        var cut = Open(out _);
        for (var n = 0; n < 3; n++) await Press(StepCommand.Next);

        await Press(StepCommand.PlayPause);

        Assert.Equal("Step 1 of 4", Position(cut));
        Assert.Single(PlaybackClock.Live);
    }

    [Fact]
    public async Task An_arrow_pauses_playback()
    {
        var cut = Open(out _);
        await Press(StepCommand.PlayPause);

        await Press(StepCommand.Next);

        Assert.Empty(PlaybackClock.Live);
        Assert.Equal("Step 2 of 4", Position(cut));
    }

    [Fact]
    public async Task The_other_shortcuts_are_left_alone()
    {
        var cut = Open(out _);

        await Press(StepCommand.Run);
        await Press(StepCommand.Advance);
        await Press(StepCommand.Choose, 2);

        Assert.Equal("Step 1 of 4", Position(cut));
        Assert.Empty(PlaybackClock.Live);
    }

    [Fact]
    public void The_keys_are_listed_under_the_controls()
    {
        var cut = Open(out _);

        var hint = cut.Find(".key-hint").TextContent;
        Assert.Contains("←", hint);
        Assert.Contains("→", hint);
        Assert.Contains("Space", hint);
    }
}
