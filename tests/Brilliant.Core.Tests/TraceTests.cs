using Brilliant.Core.Content;
using Brilliant.Core.Python;

namespace Brilliant.Core.Tests;

public class TraceTests
{
    private static TraceLocal Int(string name, int value) => new(name, "int", value.ToString());

    private static TraceFrame Frame(int? line, string function = "<module>", TraceLocal[]? locals = null, params (string Name, string[] Cells)[] arrays) =>
        new(line is null ? "return" : "line", line, function, locals ?? [],
            arrays.ToDictionary(a => a.Name, a => new TrackedArray("list", a.Cells, 0)));

    private static TrackedTable Dict(params (string Key, string Value)[] rows) =>
        new("dict", "dict", rows.Select(r => new TableRow(r.Key, r.Value)).ToList(), 0);

    private static TrackedTable Set(params string[] members) => new("set", "set", members.Select(m => new TableRow(m, null)).ToList(), 0);

    private static TraceFrame TableFrame(int? line, string name, TrackedTable table, params TraceLocal[] locals) =>
        new(line is null ? "return" : "line", line, "<module>", locals, new Dictionary<string, TrackedArray>(),
            new Dictionary<string, TrackedTable> { [name] = table });

    private static TraceResult Run(params TraceFrame[] frames) => new(TraceStatus.Ok, frames, "", null, null, null);

    private static readonly Visual Nums = new("nums", Visual.Array, ["i"]);

    private static IReadOnlyList<TraceState> Build(TraceResult result, params Visual[] visuals) => TraceModel.Build(result, visuals);

    // ---- JSON from tracer.py ----

    [Fact]
    public void The_tracer_json_parses_into_a_result()
    {
        var json = """
            {"status": "truncated", "frames": [{"event": "line", "line": 2, "function": "<module>",
              "locals": [{"name": "i", "type": "int", "repr": "0"}],
              "tracked": {"nums": {"type": "list", "cells": ["3", "8"], "more": 4}}}],
             "stdout": "hi\n", "message": "too long", "traceback": null, "errorLine": null}
            """;

        var result = TraceResultJson.Parse(json);

        Assert.Equal(TraceStatus.Truncated, result.Status);
        var frame = Assert.Single(result.Frames);
        Assert.Equal((2, "<module>"), (frame.Line, frame.Function));
        Assert.Equal(new TraceLocal("i", "int", "0"), Assert.Single(frame.Locals));
        var tracked = frame.Tracked["nums"];
        Assert.Equal(("list", 4), (tracked.Type, tracked.More));
        Assert.Equal(["3", "8"], tracked.Cells);
        Assert.Equal(("hi\n", "too long"), (result.Stdout, result.Message));
    }

    [Fact]
    public void The_watch_list_is_sent_as_a_json_array_of_variable_names()
    {
        Assert.Equal("""["nums","grid"]""", TraceResultJson.SerializeWatch([Nums, new Visual("grid", Visual.Array, [])]));
    }

    [Fact]
    public void The_watch_list_names_each_variable_once_and_only_for_drawings_the_player_knows()
    {
        var watch = TraceResultJson.SerializeWatch([Nums, new Visual("nums", Visual.Array, ["j"]), new Visual("tree", "graph", [])]);

        Assert.Equal("""["nums"]""", watch);
    }

    [Fact]
    public void The_watch_list_includes_dicts_and_sets()
    {
        var watch = TraceResultJson.SerializeWatch([Nums, new Visual("counts", Visual.Dict, ["word"]), new Visual("seen", Visual.Set, [])]);

        Assert.Equal("""["nums","counts","seen"]""", watch);
    }

    [Fact]
    public void Tables_in_the_tracer_json_parse_with_their_rows()
    {
        var json = """
            {"status": "ok", "frames": [{"event": "line", "line": 1, "function": "<module>", "locals": [], "tracked": {},
              "tables": {"d": {"kind": "dict", "type": "OrderedDict", "rows": [{"key": "'a'", "value": "1"}], "more": 3},
                         "s": {"kind": "set", "type": "frozenset", "rows": [{"key": "2"}], "more": 0}}}],
             "stdout": "", "message": null, "traceback": null, "errorLine": null}
            """;

        var tables = Assert.Single(TraceResultJson.Parse(json).Frames).Tables!;

        Assert.Equal(("dict", "OrderedDict", 3), (tables["d"].Kind, tables["d"].Type, tables["d"].More));
        Assert.Equal(new TableRow("'a'", "1"), Assert.Single(tables["d"].Rows));
        Assert.Equal(new TableRow("2", null), Assert.Single(tables["s"].Rows));
    }

    [Fact]
    public void A_trace_recorded_before_tables_existed_still_parses()
    {
        var json = """{"status": "ok", "frames": [{"event": "line", "line": 1, "function": "f", "locals": [], "tracked": {}}], "stdout": "", "message": null, "traceback": null, "errorLine": null}""";

        Assert.Empty(Build(TraceResultJson.Parse(json), new Visual("d", Visual.Dict, []))[0].Tables);
    }

    // ---- States ----

    [Fact]
    public void Each_frame_becomes_a_numbered_state_and_the_last_one_is_final()
    {
        var states = Build(Run(Frame(1), Frame(2), Frame(null)));

        Assert.Equal([0, 1, 2], states.Select(s => s.Index));
        Assert.Equal([1, 2, null], states.Select(s => s.Line));
        Assert.Equal([false, false, true], states.Select(s => s.IsFinal));
    }

    [Fact]
    public void No_frames_means_no_states()
    {
        Assert.Empty(Build(Run()));
    }

    // ---- Locals ----

    [Fact]
    public void A_local_is_marked_changed_when_its_value_differs_from_the_previous_step()
    {
        var states = Build(Run(
            Frame(1, locals: [Int("i", 0), Int("n", 5)]),
            Frame(2, locals: [Int("i", 1), Int("n", 5)])));

        Assert.All(states[0].Locals, l => Assert.False(l.Changed));              // nothing to compare the first step with
        Assert.Equal([("i", true), ("n", false)], states[1].Locals.Select(l => (l.Name, l.Changed)));
    }

    [Fact]
    public void A_new_local_counts_as_changed()
    {
        var states = Build(Run(Frame(1, locals: [Int("i", 0)]), Frame(2, locals: [Int("i", 0), Int("total", 0)])));

        Assert.Equal([("i", false), ("total", true)], states[1].Locals.Select(l => (l.Name, l.Changed)));
    }

    [Fact]
    public void Locals_keep_their_type_and_value_text()
    {
        var states = Build(Run(Frame(1, locals: [new TraceLocal("name", "str", "'ada'")])));

        Assert.Equal(new LocalView("name", "str", "'ada'", false), Assert.Single(states[0].Locals));
    }

    [Fact]
    public void Nothing_is_marked_changed_when_the_previous_step_ran_in_another_function()
    {
        var states = Build(Run(
            Frame(4, locals: [Int("x", 1)]),
            Frame(2, "double", [Int("x", 2)])));

        Assert.All(states[1].Locals, l => Assert.False(l.Changed));
    }

    // ---- Arrays ----

    [Fact]
    public void A_visual_makes_an_array_with_one_cell_per_item()
    {
        var states = Build(Run(Frame(1, arrays: ("nums", ["3", "8", "2"]))), Nums);

        var array = Assert.Single(states[0].Arrays);
        Assert.Equal("nums", array.Variable);
        Assert.Equal(["3", "8", "2"], array.Cells.Select(c => c.Text));
    }

    [Fact]
    public void An_array_is_left_out_while_the_variable_is_not_a_list()
    {
        var states = Build(Run(Frame(1), Frame(2, arrays: ("nums", ["1"]))), Nums);

        Assert.Empty(states[0].Arrays);
        Assert.Single(states[1].Arrays);
    }

    [Fact]
    public void Variables_without_a_visual_are_not_drawn()
    {
        var states = Build(Run(Frame(1, arrays: [("nums", ["1"]), ("other", ["2"])])), Nums);

        Assert.Equal(["nums"], states[0].Arrays.Select(a => a.Variable));
    }

    [Fact]
    public void Arrays_follow_the_order_of_the_visuals()
    {
        var states = Build(Run(Frame(1, arrays: [("a", ["1"]), ("b", ["2"])])),
            new Visual("b", Visual.Array, []), new Visual("a", Visual.Array, []));

        Assert.Equal(["b", "a"], states[0].Arrays.Select(a => a.Variable));
    }

    [Fact]
    public void A_cell_is_marked_changed_when_its_value_differs_from_the_previous_step()
    {
        var states = Build(Run(
            Frame(1, arrays: ("nums", ["3", "8", "2"])),
            Frame(2, arrays: ("nums", ["3", "9", "2"]))), Nums);

        Assert.All(states[0].Arrays[0].Cells, c => Assert.False(c.Changed));
        Assert.Equal([false, true, false], states[1].Arrays[0].Cells.Select(c => c.Changed));
    }

    [Fact]
    public void A_cell_added_by_append_counts_as_changed()
    {
        var states = Build(Run(Frame(1, arrays: ("nums", ["3"])), Frame(2, arrays: ("nums", ["3", "5"]))), Nums);

        Assert.Equal([false, true], states[1].Arrays[0].Cells.Select(c => c.Changed));
    }

    [Fact]
    public void A_cell_is_not_compared_across_functions()
    {
        var states = Build(Run(
            Frame(5, arrays: ("nums", ["1"])),
            Frame(2, "f", arrays: ("nums", ["9"]))), Nums);

        Assert.False(states[1].Arrays[0].Cells[0].Changed);
    }

    [Fact]
    public void The_count_of_cells_beyond_the_recorded_ones_is_kept()
    {
        var frame = new TraceFrame("line", 1, "<module>", [], new Dictionary<string, TrackedArray> { ["nums"] = new("list", ["1", "2"], 7) });

        Assert.Equal(7, Build(Run(frame), Nums)[0].Arrays[0].More);
    }

    // ---- Pointers ----

    [Fact]
    public void A_named_pointer_sits_on_the_cell_its_int_variable_names()
    {
        var states = Build(Run(Frame(1, locals: [Int("i", 2)], arrays: ("nums", ["3", "8", "2"]))), Nums);

        Assert.Equal(new PointerView("i", 2, false), Assert.Single(states[0].Arrays[0].Pointers));
    }

    [Fact]
    public void A_pointer_is_moved_when_its_index_differs_from_the_previous_step()
    {
        var states = Build(Run(
            Frame(1, locals: [Int("i", 0)], arrays: ("nums", ["3", "8"])),
            Frame(2, locals: [Int("i", 1)], arrays: ("nums", ["3", "8"])),
            Frame(3, locals: [Int("i", 1)], arrays: ("nums", ["3", "8"]))), Nums);

        Assert.Equal([false, true, false], states.Select(s => s.Arrays[0].Pointers[0].Moved));
    }

    [Theory]
    [InlineData(3)]    // one past the end: the loop is done
    [InlineData(-1)]   // negative indexes aren't drawn as positions
    public void A_pointer_off_the_array_has_no_cell(int value)
    {
        var states = Build(Run(Frame(1, locals: [Int("i", value)], arrays: ("nums", ["3", "8", "2"]))), Nums);

        Assert.Null(Assert.Single(states[0].Arrays[0].Pointers).Index);
    }

    [Fact]
    public void The_first_and_last_cells_are_valid_positions()
    {
        var visual = new Visual("nums", Visual.Array, ["first", "last"]);
        var states = Build(Run(Frame(1, locals: [Int("first", 0), Int("last", 2)], arrays: ("nums", ["3", "8", "2"]))), visual);

        Assert.Equal([0, 2], states[0].Arrays[0].Pointers.Select(p => p.Index!.Value));
    }

    [Fact]
    public void A_pointer_whose_variable_is_missing_or_not_an_int_is_listed_without_a_cell()
    {
        var visual = new Visual("nums", Visual.Array, ["i", "j"]);
        var states = Build(Run(Frame(1, locals: [new TraceLocal("j", "str", "'x'")], arrays: ("nums", ["1"]))), visual);

        Assert.Equal([("i", (int?)null), ("j", null)], states[0].Arrays[0].Pointers.Select(p => (p.Name, p.Index)));
    }

    [Fact]
    public void A_pointer_must_be_an_int_even_when_another_type_prints_like_one()
    {
        var states = Build(Run(Frame(1, locals: [new TraceLocal("i", "Index", "1")], arrays: ("nums", ["3", "8"]))), Nums);

        Assert.Null(states[0].Arrays[0].Pointers[0].Index);
    }

    [Fact]
    public void A_pointer_beyond_the_recorded_cells_has_no_cell()
    {
        var frame = new TraceFrame("line", 1, "<module>", [Int("i", 3)], new Dictionary<string, TrackedArray> { ["nums"] = new("list", ["1", "2"], 5) });

        Assert.Null(Build(Run(frame), Nums)[0].Arrays[0].Pointers[0].Index);
    }

    [Fact]
    public void A_pointer_coming_back_onto_the_array_counts_as_moved()
    {
        var states = Build(Run(
            Frame(1, locals: [Int("i", 9)], arrays: ("nums", ["1"])),
            Frame(2, locals: [Int("i", 0)], arrays: ("nums", ["1"]))), Nums);

        Assert.True(states[1].Arrays[0].Pointers[0].Moved);
    }

    [Fact]
    public void Two_pointers_move_independently()
    {
        var visual = new Visual("nums", Visual.Array, ["left", "right"]);
        var states = Build(Run(
            Frame(1, locals: [Int("left", 0), Int("right", 3)], arrays: ("nums", ["1", "2", "3", "4"])),
            Frame(2, locals: [Int("left", 1), Int("right", 3)], arrays: ("nums", ["1", "2", "3", "4"]))), visual);

        Assert.Equal([("left", 1, true), ("right", 3, false)], states[1].Arrays[0].Pointers.Select(p => (p.Name, p.Index!.Value, p.Moved)));
    }


    // ---- Tables (dicts and sets) ----

    private static readonly Visual Counts = new("counts", Visual.Dict, ["word"]);

    [Fact]
    public void A_dict_visual_makes_a_table_with_one_row_per_entry()
    {
        var states = Build(Run(TableFrame(1, "counts", Dict(("'a'", "1"), ("'b'", "2")))), Counts);

        var table = Assert.Single(states[0].Tables);
        Assert.Equal(("counts", "dict"), (table.Variable, table.Kind));
        Assert.Equal([("'a'", "1"), ("'b'", "2")], table.Rows.Select(r => (r.Key, r.Value!)));
        Assert.Empty(states[0].Arrays);
    }

    [Fact]
    public void A_set_visual_makes_a_table_of_members_with_no_values()
    {
        var states = Build(Run(TableFrame(1, "seen", Set("1", "2"))), new Visual("seen", Visual.Set, []));

        var table = Assert.Single(states[0].Tables);
        Assert.Equal("set", table.Kind);
        Assert.Equal(["1", "2"], table.Rows.Select(r => r.Key));
        Assert.All(table.Rows, r => Assert.Null(r.Value));
    }

    [Fact]
    public void A_table_is_left_out_while_the_variable_is_not_that_kind_of_collection()
    {
        var asSet = Build(Run(TableFrame(1, "counts", Dict(("1", "1")))), new Visual("counts", Visual.Set, []));
        var asDict = Build(Run(TableFrame(1, "seen", Set("1"))), new Visual("seen", Visual.Dict, []));
        var absent = Build(Run(Frame(1)), Counts);

        Assert.Empty(asSet[0].Tables);
        Assert.Empty(asDict[0].Tables);
        Assert.Empty(absent[0].Tables);
    }

    [Fact]
    public void An_array_visual_does_not_draw_a_dict_and_a_dict_visual_does_not_draw_a_list()
    {
        var onDict = Build(Run(TableFrame(1, "nums", Dict(("1", "1")))), Nums);
        var onList = Build(Run(Frame(1, arrays: ("counts", ["1"]))), Counts);

        Assert.Empty(onDict[0].Arrays);
        Assert.Empty(onList[0].Tables);
    }

    [Fact]
    public void Tables_and_arrays_each_follow_the_order_of_their_visuals()
    {
        var run = Run(new TraceFrame("line", 1, "<module>", [], new Dictionary<string, TrackedArray>(),
            new Dictionary<string, TrackedTable> { ["b"] = Set("1"), ["a"] = Dict(("1", "1")) }));

        var states = Build(run, new Visual("a", Visual.Dict, []), new Visual("b", Visual.Set, []));

        Assert.Equal(["a", "b"], states[0].Tables.Select(t => t.Variable));
    }

    [Fact]
    public void A_row_added_since_the_previous_step_is_marked_added()
    {
        var states = Build(Run(
            TableFrame(1, "counts", Dict(("'a'", "1"))),
            TableFrame(2, "counts", Dict(("'a'", "1"), ("'b'", "1")))), Counts);

        Assert.All(states[0].Tables[0].Rows, r => Assert.Equal(RowChange.None, r.Change));
        Assert.Equal([RowChange.None, RowChange.Added], states[1].Tables[0].Rows.Select(r => r.Change));
    }

    [Fact]
    public void A_row_whose_value_differs_is_marked_updated()
    {
        var states = Build(Run(
            TableFrame(1, "counts", Dict(("'a'", "1"), ("'b'", "1"))),
            TableFrame(2, "counts", Dict(("'a'", "2"), ("'b'", "1")))), Counts);

        Assert.Equal([RowChange.Updated, RowChange.None], states[1].Tables[0].Rows.Select(r => r.Change));
    }

    [Fact]
    public void A_row_that_has_gone_is_kept_for_one_step_marked_removed()
    {
        var states = Build(Run(
            TableFrame(1, "counts", Dict(("'a'", "1"), ("'b'", "2"))),
            TableFrame(2, "counts", Dict(("'b'", "2"))),
            TableFrame(3, "counts", Dict(("'b'", "2")))), Counts);

        Assert.Equal([("'b'", RowChange.None), ("'a'", RowChange.Removed)], states[1].Tables[0].Rows.Select(r => (r.Key, r.Change)));
        Assert.Equal("1", states[1].Tables[0].Rows[1].Value);
        Assert.Equal(["'b'"], states[2].Tables[0].Rows.Select(r => r.Key));      // the ghost lasts a single step
    }

    [Fact]
    public void A_removed_set_member_is_marked_removed_too()
    {
        var states = Build(Run(TableFrame(1, "s", Set("1", "2")), TableFrame(2, "s", Set("2"))), new Visual("s", Visual.Set, []));

        Assert.Equal([RowChange.None, RowChange.Removed], states[1].Tables[0].Rows.Select(r => r.Change));
    }

    [Fact]
    public void A_key_that_returns_after_being_removed_is_added_again()
    {
        var states = Build(Run(
            TableFrame(1, "counts", Dict(("'a'", "1"))),
            TableFrame(2, "counts", Dict()),
            TableFrame(3, "counts", Dict(("'a'", "1")))), Counts);

        Assert.Equal([RowChange.Added], states[2].Tables[0].Rows.Select(r => r.Change));
    }

    [Fact]
    public void Nothing_in_a_table_is_marked_the_first_time_it_appears_or_across_functions()
    {
        var first = Build(Run(Frame(1), TableFrame(2, "counts", Dict(("'a'", "1")))), Counts);
        var otherFunction = Build(Run(
            TableFrame(1, "counts", Dict(("'a'", "1"))),
            new TraceFrame("line", 5, "f", [], new Dictionary<string, TrackedArray>(),
                new Dictionary<string, TrackedTable> { ["counts"] = Dict(("'z'", "9")) })), Counts);

        Assert.All(first[1].Tables[0].Rows, r => Assert.Equal(RowChange.None, r.Change));
        Assert.All(otherFunction[1].Tables[0].Rows, r => Assert.Equal(RowChange.None, r.Change));
    }

    [Fact]
    public void A_pointer_marks_the_row_whose_key_its_variable_holds()
    {
        var states = Build(Run(TableFrame(1, "counts", Dict(("'a'", "1"), ("'b'", "2")), new TraceLocal("word", "str", "'b'"))), Counts);

        var table = states[0].Tables[0];
        Assert.Empty(table.Rows[0].LookedUpBy);
        Assert.Equal(["word"], table.Rows[1].LookedUpBy);
        Assert.Empty(table.Misses);
    }

    [Fact]
    public void A_pointer_whose_key_is_not_in_the_table_is_a_miss()
    {
        var states = Build(Run(TableFrame(1, "counts", Dict(("'a'", "1")), new TraceLocal("word", "str", "'zzz'"))), Counts);

        Assert.All(states[0].Tables[0].Rows, r => Assert.Empty(r.LookedUpBy));
        Assert.Equal(["word"], states[0].Tables[0].Misses);
    }

    [Fact]
    public void A_pointer_whose_variable_does_not_exist_yet_is_neither_a_lookup_nor_a_miss()
    {
        var states = Build(Run(TableFrame(1, "counts", Dict(("'a'", "1")))), Counts);

        Assert.Empty(states[0].Tables[0].Misses);
        Assert.All(states[0].Tables[0].Rows, r => Assert.Empty(r.LookedUpBy));
    }

    [Fact]
    public void Two_pointers_can_look_at_the_same_row_and_a_set_can_be_looked_up_too()
    {
        var visual = new Visual("seen", Visual.Set, ["x", "y"]);
        var states = Build(Run(TableFrame(1, "seen", Set("1", "2"), new TraceLocal("x", "int", "2"), new TraceLocal("y", "int", "2"))), visual);

        Assert.Equal(["x", "y"], states[0].Tables[0].Rows[1].LookedUpBy);
    }

    [Fact]
    public void A_ghost_row_is_never_looked_up()
    {
        var states = Build(Run(
            TableFrame(1, "counts", Dict(("'a'", "1"))),
            TableFrame(2, "counts", Dict(), new TraceLocal("word", "str", "'a'"))), Counts);

        var table = states[1].Tables[0];
        Assert.Equal(RowChange.Removed, table.Rows[0].Change);
        Assert.Empty(table.Rows[0].LookedUpBy);
        Assert.Equal(["word"], table.Misses);
    }

    [Fact]
    public void The_count_of_rows_beyond_the_recorded_ones_is_kept()
    {
        var frame = TableFrame(1, "counts", Dict(("'a'", "1")) with { More = 7 });

        Assert.Equal(7, Build(Run(frame), Counts)[0].Tables[0].More);
    }

    // ---- Content ----

    [Fact]
    public void A_trace_step_survives_a_pack_round_trip()
    {
        var step = new TraceStep("step.t", "Walk the list", "Watch **i** move.", "nums = [3, 8]\nfor i in range(2):\n    pass\n",
            [new Visual("nums", Visual.Array, ["i"])]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "A", ["lesson.one"])], [new Lesson("lesson.one", "One", "track.a", [step])]);
        ms.Position = 0;

        var loaded = Assert.IsType<TraceStep>(ContentPackFormat.Load(ms).Get<Lesson>("lesson.one").Steps[0]);

        Assert.Equal((step.Body, step.Code), (loaded.Body, loaded.Code));
        var visual = Assert.Single(loaded.Visuals);
        Assert.Equal(("nums", Visual.Array), (visual.Variable, visual.As));
        Assert.Equal(["i"], visual.Pointers);
    }
}
