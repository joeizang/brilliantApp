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
    public void A_cut_off_queue_marks_its_back_at_the_count_of_the_rest()
    {
        var run = RunOf(Seq(1, "x", "deque", ["1", "2"], more: 5));
        var cut = Start(run, [new Visual("x", Visual.Queue, [])], out _);

        Assert.Contains("+5 more", cut.Find("figure.queue g.more").TextContent);
        Assert.Equal(At(cut.Find("g.end-marker.back")).X, At(cut.FindAll("figure.queue g.cell")[0]).X + 2 * 64 + 30);   // the middle of the third place
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
