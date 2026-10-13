using Brilliant.Core.Content;
using Brilliant.Core.Python;

namespace Brilliant.Core.Tests;

public class TraceTests
{
    private static TraceLocal Int(string name, int value) => new(name, "int", value.ToString());

    private static TraceFrame Frame(int? line, string function = "<module>", TraceLocal[]? locals = null, params (string Name, string[] Cells)[] arrays) =>
        new(line is null ? "return" : "line", line, function, locals ?? [],
            arrays.ToDictionary(a => a.Name, a => new TrackedArray("list", a.Cells, 0)));

    /// <summary>The id the recorder would give a key: a stand-in that is the same for the same label.</summary>
    private static int IdOf(string key) => key.Aggregate(17, (hash, c) => hash * 31 + c);

    private static TrackedTable Dict(params (string Key, string Value)[] rows) =>
        new("dict", "dict", rows.Select(r => new TableRow(r.Key, r.Value, IdOf(r.Key))).ToList(), 0, new Dictionary<string, int>());

    private static TrackedTable Set(params string[] members) =>
        new("set", "set", members.Select(m => new TableRow(m, null, IdOf(m))).ToList(), 0, new Dictionary<string, int>());

    /// <summary>The table as the recorder reports it when each named variable holds the key with that label.</summary>
    private static TrackedTable Holding(TrackedTable table, params (string Variable, string Key)[] holds) =>
        table with { Hits = holds.ToDictionary(h => h.Variable, h => IdOf(h.Key)) };

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
        var watch = TraceResultJson.SerializeWatch([Nums, new Visual("nums", Visual.Array, ["j"]), new Visual("words", "trie", [])]);

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
        Assert.Null(tables["d"].Hits);                  // a trace from before lookups were recorded
    }

    [Fact]
    public void Row_ids_and_hits_in_the_tracer_json_parse()
    {
        var json = """
            {"status": "ok", "frames": [{"event": "line", "line": 1, "function": "<module>", "locals": [], "tracked": {},
              "tables": {"d": {"kind": "dict", "type": "dict", "rows": [{"id": 4, "key": "'a'", "value": "1"}], "more": 0, "hits": {"word": 4, "other": 9}}}}],
             "stdout": "", "message": null, "traceback": null, "errorLine": null}
            """;

        var table = Assert.Single(TraceResultJson.Parse(json).Frames).Tables!["d"];

        Assert.Equal(new TableRow("'a'", "1", 4), Assert.Single(table.Rows));
        Assert.Equal(4, table.Hits!["word"]);
        Assert.Equal(9, table.Hits["other"]);
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


    // ---- Stacks and queues ----

    private static readonly Visual Stack = new("s", Visual.Stack, ["i"]);
    private static readonly Visual Queue = new("q", Visual.Queue, []);

    private static TraceFrame Seq(int? line, string name, string[] cells, string type = "list", string function = "<module>", int more = 0, params TraceLocal[] locals) =>
        new(line is null ? "return" : "line", line, function, locals, new Dictionary<string, TrackedArray> { [name] = new(type, cells, more) });

    private static ArrayView Last(IReadOnlyList<TraceState> states) => states[^1].Arrays.Single();

    private static IEnumerable<(string, bool, bool)> Marks(ArrayView view) => view.Cells.Select(c => (c.Text, c.Changed, c.Added));

    [Fact]
    public void The_watch_list_includes_stacks_and_queues()
    {
        Assert.Equal("""["s","q"]""", TraceResultJson.SerializeWatch([Stack, Queue]));
    }

    [Fact]
    public void A_stack_or_queue_is_drawn_with_its_kind_and_nothing_marked_the_first_time()
    {
        var states = Build(Run(Seq(1, "s", ["1", "2"]), Seq(1, "q", ["3"], "deque")), Stack, Queue);

        Assert.Equal(Visual.Stack, states[0].Arrays[0].Kind);
        Assert.Equal(["1", "2"], states[0].Arrays[0].Cells.Select(c => c.Text));
        Assert.Equal(Visual.Queue, states[1].Arrays[0].Kind);
        Assert.All(states.SelectMany(s => s.Arrays).SelectMany(a => a.Cells), c => Assert.False(c.Changed || c.Added));
        Assert.All(states.SelectMany(s => s.Arrays), a => Assert.Empty(a.Gone));
    }

    [Fact]
    public void An_array_keeps_index_ids_and_no_ghosts()
    {
        var states = Build(Run(Frame(1, arrays: ("nums", ["1", "2"])), Frame(2, arrays: ("nums", ["2"]))), Nums);

        Assert.Equal(Visual.Array, Last(states).Kind);
        Assert.Equal([0], Last(states).Cells.Select(c => c.Id));
        Assert.Empty(Last(states).Gone);
    }

    [Theory]
    [InlineData(Visual.Stack, "list", true)]
    [InlineData(Visual.Stack, "deque", true)]
    [InlineData(Visual.Stack, "tuple", false)]
    [InlineData(Visual.Queue, "list", true)]
    [InlineData(Visual.Queue, "deque", true)]
    [InlineData(Visual.Queue, "tuple", false)]
    [InlineData(Visual.Array, "deque", true)]
    [InlineData(Visual.Array, "tuple", true)]
    public void A_stack_or_queue_draws_a_list_or_a_deque(string kind, string type, bool drawn)
    {
        var states = Build(Run(Seq(1, "x", ["1"], type)), new Visual("x", kind, []));

        Assert.Equal(drawn, states[0].Arrays.Count == 1);
    }

    [Fact]
    public void A_push_comes_in_at_the_top_and_the_cells_below_keep_their_ids()
    {
        var states = Build(Run(Seq(1, "s", ["1", "2"]), Seq(2, "s", ["1", "2", "3"])), Stack);

        Assert.Equal([("1", false, false), ("2", false, false), ("3", false, true)], Marks(Last(states)));
        Assert.Equal(states[0].Arrays[0].Cells.Select(c => c.Id), Last(states).Cells.Take(2).Select(c => c.Id));
        Assert.Empty(Last(states).Gone);
    }

    [Fact]
    public void A_pop_leaves_from_the_top_as_a_ghost_for_one_step()
    {
        var states = Build(Run(Seq(1, "s", ["1", "2"]), Seq(2, "s", ["1"]), Seq(3, "s", ["1"])), Stack);

        var popped = states[0].Arrays[0].Cells[1];
        Assert.Equal([("1", false, false)], Marks(states[1].Arrays[0]));
        Assert.Equal(new GoneCell(popped.Id, "2", AtFront: false), Assert.Single(states[1].Arrays[0].Gone));
        Assert.Empty(states[2].Arrays[0].Gone);
    }

    [Fact]
    public void Popping_the_last_item_leaves_an_empty_stack_with_its_ghost()
    {
        var states = Build(Run(Seq(1, "s", ["7"]), Seq(2, "s", [])), Stack);

        Assert.Empty(Last(states).Cells);
        Assert.Equal(("7", false), (Last(states).Gone.Single().Text, Last(states).Gone.Single().AtFront));
    }

    [Fact]
    public void An_enqueue_comes_in_at_the_back()
    {
        var states = Build(Run(Seq(1, "q", ["1"], "deque"), Seq(2, "q", ["1", "2"], "deque")), Queue);

        Assert.Equal([("1", false, false), ("2", false, true)], Marks(Last(states)));
    }

    [Fact]
    public void A_dequeue_leaves_from_the_front_and_the_rest_keep_their_ids_so_they_slide_up()
    {
        var states = Build(Run(Seq(1, "q", ["1", "2", "3"], "deque"), Seq(2, "q", ["2", "3"], "deque")), Queue);

        var before = states[0].Arrays[0].Cells;
        Assert.Equal([("2", false, false), ("3", false, false)], Marks(Last(states)));
        Assert.Equal([before[1].Id, before[2].Id], Last(states).Cells.Select(c => c.Id));
        Assert.Equal(new GoneCell(before[0].Id, "1", AtFront: true), Assert.Single(Last(states).Gone));
    }

    [Fact]
    public void Appendleft_comes_in_at_the_front_and_pop_leaves_from_the_back()
    {
        var states = Build(Run(
            Seq(1, "q", ["1", "2"], "deque"),
            Seq(2, "q", ["0", "1", "2"], "deque"),
            Seq(3, "q", ["0", "1"], "deque")), Queue);

        Assert.Equal([("0", false, true), ("1", false, false), ("2", false, false)], Marks(states[1].Arrays[0]));
        Assert.Equal(("2", false), (states[2].Arrays[0].Gone.Single().Text, states[2].Arrays[0].Gone.Single().AtFront));
    }

    [Fact]
    public void A_popleft_on_a_list_queue_is_a_dequeue_too()
    {
        var states = Build(Run(Seq(1, "q", ["a", "b"]), Seq(2, "q", ["b"])), Queue);

        Assert.True(Last(states).Gone.Single().AtFront);
    }

    [Fact]
    public void Turning_a_queue_round_in_one_step_dequeues_at_the_front_and_enqueues_at_the_back()
    {
        // q.append(q.popleft())
        var states = Build(Run(Seq(1, "q", ["1", "2", "3"], "deque"), Seq(2, "q", ["2", "3", "1"], "deque")), Queue);

        Assert.Equal([("2", false, false), ("3", false, false), ("1", false, true)], Marks(Last(states)));
        Assert.Equal(("1", true), (Last(states).Gone.Single().Text, Last(states).Gone.Single().AtFront));
    }

    [Fact]
    public void When_the_values_repeat_a_stack_loses_its_top_and_a_queue_its_front()
    {
        var stack = Build(Run(Seq(1, "s", ["1", "1"]), Seq(2, "s", ["1"])), Stack);
        var queue = Build(Run(Seq(1, "q", ["1", "1"]), Seq(2, "q", ["1"])), Queue);

        Assert.False(Last(stack).Gone.Single().AtFront);
        Assert.Equal(stack[0].Arrays[0].Cells[0].Id, Last(stack).Cells[0].Id);
        Assert.True(Last(queue).Gone.Single().AtFront);
        Assert.Equal(queue[0].Arrays[0].Cells[1].Id, Last(queue).Cells[0].Id);
    }

    [Fact]
    public void When_the_values_repeat_both_grow_at_the_back()
    {
        foreach (var visual in new[] { new Visual("x", Visual.Stack, []), new Visual("x", Visual.Queue, []) })
        {
            var states = Build(Run(Seq(1, "x", ["1", "1"]), Seq(2, "x", ["1", "1", "1"])), visual);

            Assert.Equal([false, false, true], Last(states).Cells.Select(c => c.Added));
        }
    }

    [Fact]
    public void Setting_the_top_in_place_is_a_change_not_a_pop_and_a_push()
    {
        var states = Build(Run(Seq(1, "s", ["1", "2"]), Seq(2, "s", ["1", "5"])), Stack);

        Assert.Equal([("1", false, false), ("5", true, false)], Marks(Last(states)));
        Assert.Equal(states[0].Arrays[0].Cells[1].Id, Last(states).Cells[1].Id);
        Assert.Empty(Last(states).Gone);
    }

    [Fact]
    public void Setting_the_front_in_place_is_a_change_too()
    {
        var states = Build(Run(Seq(1, "q", ["1", "2"]), Seq(2, "q", ["9", "2"])), Queue);

        Assert.Equal([("9", true, false), ("2", false, false)], Marks(Last(states)));
        Assert.Empty(Last(states).Gone);
    }

    [Fact]
    public void A_cell_that_comes_in_has_an_id_no_cell_of_the_step_before_had()
    {
        var states = Build(Run(Seq(1, "q", ["1", "2"]), Seq(2, "q", ["2"]), Seq(3, "q", ["2", "3"])), Queue);

        var earlier = states[0].Arrays[0].Cells.Select(c => c.Id).Concat(states[1].Arrays[0].Gone.Select(g => g.Id));
        Assert.DoesNotContain(Last(states).Cells[1].Id, earlier);
    }

    [Fact]
    public void A_stack_or_queue_is_not_compared_across_functions()
    {
        var states = Build(Run(Seq(1, "s", ["1", "2"]), Seq(5, "s", ["1"], function: "f")), Stack);

        Assert.Empty(Last(states).Gone);
        Assert.All(Last(states).Cells, c => Assert.False(c.Changed || c.Added));
    }

    [Fact]
    public void Cells_moving_into_view_past_the_cut_off_are_not_enqueued()
    {
        string[] Cells(int from) => Enumerable.Range(from, 3).Select(n => n.ToString()).ToArray();
        var states = Build(Run(Seq(1, "q", Cells(0), "deque", more: 5), Seq(2, "q", Cells(1), "deque", more: 4)), Queue);

        Assert.All(Last(states).Cells, c => Assert.False(c.Added));
        Assert.True(Last(states).Gone.Single().AtFront);
    }

    [Fact]
    public void Pointers_index_a_stack_as_they_do_an_array()
    {
        var states = Build(Run(
            Seq(1, "s", ["1", "2", "3"], locals: Int("i", 2)),
            Seq(2, "s", ["1", "2"], locals: Int("i", 2))), Stack);

        Assert.Equal(("i", 2, false), (states[0].Arrays[0].Pointers[0].Name, states[0].Arrays[0].Pointers[0].Index!.Value, states[0].Arrays[0].Pointers[0].Moved));
        Assert.Equal((null, true), (Last(states).Pointers[0].Index, Last(states).Pointers[0].Moved));
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
        var states = Build(Run(TableFrame(1, "counts", Holding(Dict(("'a'", "1"), ("'b'", "2")), ("word", "'b'")), new TraceLocal("word", "str", "'b'"))), Counts);

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
        var states = Build(Run(TableFrame(1, "seen", Holding(Set("1", "2"), ("x", "2"), ("y", "2")), new TraceLocal("x", "int", "2"), new TraceLocal("y", "int", "2"))), visual);

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
    public void A_pointer_finds_a_key_python_treats_as_equal_though_the_reprs_differ()
    {
        var table = Holding(Dict(("1", "'found'")), ("key", "1"));
        var states = Build(Run(TableFrame(1, "counts", table, new TraceLocal("key", "float", "1.0"))), new Visual("counts", Visual.Dict, ["key"]));

        Assert.Equal(["key"], states[0].Tables[0].Rows[0].LookedUpBy);
        Assert.Empty(states[0].Tables[0].Misses);
    }

    [Fact]
    public void A_pointer_the_recorder_did_not_find_is_a_miss_even_when_its_repr_matches_a_label()
    {
        // The label reads 'a' but the recorder says the variable holds no key: trust the recorder.
        var states = Build(Run(TableFrame(1, "counts", Dict(("'a'", "1")), new TraceLocal("word", "str", "'a'"))), Counts);

        Assert.All(states[0].Tables[0].Rows, r => Assert.Empty(r.LookedUpBy));
        Assert.Equal(["word"], states[0].Tables[0].Misses);
    }

    [Fact]
    public void A_pointer_that_found_a_row_cut_off_by_the_row_limit_is_neither_a_lookup_nor_a_miss()
    {
        var table = (Dict(("'a'", "1")) with { More = 5 }) with { Hits = new Dictionary<string, int> { ["word"] = IdOf("'far away'") } };
        var states = Build(Run(TableFrame(1, "counts", table, new TraceLocal("word", "str", "'far away'"))), Counts);

        Assert.All(states[0].Tables[0].Rows, r => Assert.Empty(r.LookedUpBy));
        Assert.Empty(states[0].Tables[0].Misses);
    }

    [Fact]
    public void Without_recorded_lookups_nothing_is_marked_or_missed()
    {
        var table = Dict(("'a'", "1")) with { Hits = null };
        var states = Build(Run(TableFrame(1, "counts", table, new TraceLocal("word", "str", "'a'"))), Counts);

        Assert.All(states[0].Tables[0].Rows, r => Assert.Empty(r.LookedUpBy));
        Assert.Empty(states[0].Tables[0].Misses);
    }

    // Two long keys clip to the same label; the recorder still gives them different ids.
    private static TrackedTable Clashing(string firstValue, string secondValue) =>
        new("dict", "dict", [new TableRow("'aaaa…", firstValue, 1), new TableRow("'aaaa…", secondValue, 2)], 0, new Dictionary<string, int>());

    [Fact]
    public void Rows_whose_labels_read_the_same_are_told_apart_by_their_ids()
    {
        var states = Build(Run(TableFrame(1, "counts", Clashing("1", "2"))), Counts);

        var rows = states[0].Tables[0].Rows;
        Assert.Equal(2, rows.Select(r => r.Id).Distinct().Count());
        Assert.Equal(["1", "2"], rows.Select(r => r.Value!));
    }

    [Fact]
    public void A_change_is_matched_to_the_row_with_the_same_id_not_the_same_label()
    {
        var states = Build(Run(TableFrame(1, "counts", Clashing("1", "2")), TableFrame(2, "counts", Clashing("1", "3"))), Counts);

        Assert.Equal([RowChange.None, RowChange.Updated], states[1].Tables[0].Rows.Select(r => r.Change));
    }

    [Fact]
    public void A_row_with_a_clashing_label_that_goes_is_the_one_marked_removed()
    {
        var after = new TrackedTable("dict", "dict", [new TableRow("'aaaa…", "1", 1)], 0, new Dictionary<string, int>());
        var states = Build(Run(TableFrame(1, "counts", Clashing("1", "2")), TableFrame(2, "counts", after)), Counts);

        var rows = states[1].Tables[0].Rows;
        Assert.Equal([RowChange.None, RowChange.Removed], rows.Select(r => r.Change));
        Assert.Equal("2", rows[1].Value);
    }

    [Fact]
    public void A_row_that_replaces_another_with_the_same_label_is_added_and_the_old_one_removed()
    {
        var replaced = new TrackedTable("dict", "dict", [new TableRow("'aaaa…", "1", 3)], 0, new Dictionary<string, int>());
        var original = new TrackedTable("dict", "dict", [new TableRow("'aaaa…", "1", 1)], 0, new Dictionary<string, int>());
        var states = Build(Run(TableFrame(1, "counts", original), TableFrame(2, "counts", replaced)), Counts);

        Assert.Equal([RowChange.Added, RowChange.Removed], states[1].Tables[0].Rows.Select(r => r.Change));
    }

    [Fact]
    public void Rows_from_a_trace_without_ids_are_told_apart_by_their_labels()
    {
        var old = new TrackedTable("dict", "dict", [new TableRow("'a'", "1"), new TableRow("'b'", "1")], 0);
        var next = new TrackedTable("dict", "dict", [new TableRow("'a'", "2"), new TableRow("'b'", "1")], 0);
        var states = Build(Run(TableFrame(1, "counts", old), TableFrame(2, "counts", next)), Counts);

        Assert.Equal([RowChange.Updated, RowChange.None], states[1].Tables[0].Rows.Select(r => r.Change));
        Assert.Equal(2, states[1].Tables[0].Rows.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void A_row_recorded_without_an_id_is_never_taken_for_a_missed_lookup()
    {
        var table = new TrackedTable("dict", "dict", [new TableRow("'a'", "1")], 0, new Dictionary<string, int>());
        var states = Build(Run(TableFrame(1, "counts", table, new TraceLocal("word", "str", "'zzz'"))), Counts);

        Assert.Empty(states[0].Tables[0].Rows[0].LookedUpBy);
        Assert.Equal(["word"], states[0].Tables[0].Misses);
    }

    [Fact]
    public void The_count_of_rows_beyond_the_recorded_ones_is_kept()
    {
        var frame = TableFrame(1, "counts", Dict(("'a'", "1")) with { More = 7 });

        Assert.Equal(7, Build(Run(frame), Counts)[0].Tables[0].More);
    }

    // ---- Linked lists ----

    private static readonly Visual Head = new("head", Visual.LinkedList, ["curr", "prev"]);

    /// <summary>A linked list as recorded: (id, value, next) per node, the head's id, and the pointers' nodes.</summary>
    private static TrackedList Linked(int? head, (int Id, string Value, int? Next)[] nodes, int more = 0, params (string Name, int? Node)[] pointers) =>
        new(head, nodes.Select(n => new ListNode(n.Id, n.Value, n.Next)).ToList(), more, pointers.ToDictionary(p => p.Name, p => p.Node));

    private static TraceFrame ListFrame(int? line, TrackedList list, string function = "<module>") =>
        new(line is null ? "return" : "line", line, function, [], new Dictionary<string, TrackedArray>(), null,
            new Dictionary<string, TrackedList> { ["head"] = list });

    private static LinkedListView ListAt(IReadOnlyList<TraceState> states, int step) => Assert.Single(states[step].Lists);

    [Fact]
    public void The_watch_list_sends_a_linked_list_with_its_options_and_pointers()
    {
        var watch = TraceResultJson.SerializeWatch([Nums, new Visual("head", Visual.LinkedList, ["curr"], Next: "link", Value: "data")]);

        Assert.Equal("""["nums",{"variable":"head","as":"linked_list","next":"link","value":"data","pointers":["curr"]}]""", watch);
    }

    [Fact]
    public void Linked_lists_in_the_tracer_json_parse_with_their_nodes_and_pointers()
    {
        var json = """
            {"status": "ok", "frames": [{"event": "line", "line": 1, "function": "<module>", "locals": [], "tracked": {}, "tables": {},
              "lists": {"head": {"head": 0, "more": 2, "nodes": [{"id": 0, "value": "1", "next": 1}, {"id": 1, "value": null, "next": null}],
                                 "pointers": {"curr": 1, "prev": null}}}}],
             "stdout": "", "message": null, "traceback": null, "errorLine": null}
            """;

        var list = Assert.Single(TraceResultJson.Parse(json).Frames).Lists!["head"];

        Assert.Equal((0, 2), (list.Head, list.More));
        Assert.Equal([new ListNode(0, "1", 1), new ListNode(1, null, null)], list.Nodes);
        Assert.Equal(1, list.Pointers["curr"]);
        Assert.Null(list.Pointers["prev"]);
    }

    [Fact]
    public void A_linked_list_is_one_row_from_the_head_ending_at_none()
    {
        var view = ListAt(Build(Run(ListFrame(1, Linked(5, [(5, "1", 7), (7, "2", null)]))), Head), 0);

        var chain = Assert.Single(view.Chains);
        Assert.Equal([5, 7], chain.Nodes.Select(n => n.Id));
        Assert.Equal(["1", "2"], chain.Nodes.Select(n => n.Value));
        Assert.Equal(ChainEnd.None, chain.End);
        Assert.Equal(new NodePointer("head", 5, false), Assert.Single(view.Pointers));
    }

    [Fact]
    public void An_empty_list_is_an_empty_row_with_its_variable_at_none()
    {
        var view = ListAt(Build(Run(ListFrame(1, Linked(null, []))), Head), 0);

        var chain = Assert.Single(view.Chains);
        Assert.Empty(chain.Nodes);
        Assert.Equal(ChainEnd.None, chain.End);
        Assert.Null(Assert.Single(view.Pointers).Node);
    }

    [Fact]
    public void A_cycle_ends_the_row_with_a_link_back_to_the_node_it_loops_to()
    {
        var view = ListAt(Build(Run(ListFrame(1, Linked(1, [(1, "a", 2), (2, "b", 3), (3, "c", 2)]))), Head), 0);

        var chain = Assert.Single(view.Chains);
        Assert.Equal(3, chain.Nodes.Count);
        Assert.Equal((ChainEnd.Back, (int?)2), (chain.End, chain.Target));
    }

    [Fact]
    public void A_list_cut_off_by_the_node_limit_ends_in_more()
    {
        var view = ListAt(Build(Run(ListFrame(1, Linked(1, [(1, "a", 2)], more: 9))), Head), 0);

        Assert.Equal(ChainEnd.More, Assert.Single(view.Chains).End);
        Assert.Equal(9, view.More);
    }

    [Fact]
    public void Nodes_only_a_pointer_reaches_are_a_row_of_their_own_which_can_join_the_list()
    {
        var list = Linked(1, [(1, "a", null), (8, "x", 9), (9, "y", 1)], 0, ("curr", 8), ("prev", null));

        var view = ListAt(Build(Run(ListFrame(1, list)), Head), 0);

        Assert.Equal(2, view.Chains.Count);
        Assert.Equal([1], view.Chains[0].Nodes.Select(n => n.Id));
        Assert.Equal([8, 9], view.Chains[1].Nodes.Select(n => n.Id));
        Assert.Equal((ChainEnd.Join, (int?)1), (view.Chains[1].End, view.Chains[1].Target));
        Assert.Equal([new NodePointer("head", 1, false), new NodePointer("curr", 8, false), new NodePointer("prev", null, false)], view.Pointers);
    }

    [Fact]
    public void A_pointer_the_recorder_left_out_is_not_drawn()
    {
        var view = ListAt(Build(Run(ListFrame(1, Linked(1, [(1, "a", null)], 0, ("curr", 1)))), Head), 0);

        Assert.Equal(["head", "curr"], view.Pointers.Select(p => p.Name));
    }

    [Fact]
    public void A_rewired_link_and_a_changed_value_are_marked_and_a_node_keeps_its_id()
    {
        var states = Build(Run(
            ListFrame(1, Linked(1, [(1, "a", 2), (2, "b", null)])),
            ListFrame(2, Linked(2, [(2, "b", 1), (1, "A", null)]))), Head);

        var nodes = ListAt(states, 1).Chains[0].Nodes;
        Assert.Equal([2, 1], nodes.Select(n => n.Id));
        Assert.Equal((true, false), (nodes[0].Relinked, nodes[0].Changed));
        Assert.Equal((true, true), (nodes[1].Relinked, nodes[1].Changed));
        Assert.DoesNotContain(nodes, n => n.Added);
    }

    [Fact]
    public void A_node_new_since_the_step_before_is_marked_added_and_moved_pointers_moved()
    {
        var states = Build(Run(
            ListFrame(1, Linked(1, [(1, "a", null)], 0, ("curr", 1))),
            ListFrame(2, Linked(1, [(1, "a", 4), (4, "b", null)], 0, ("curr", 4), ("prev", 1)))), Head);

        var view = ListAt(states, 1);
        Assert.Equal([false, true], view.Nodes.Select(n => n.Added));
        Assert.True(view.Nodes.First().Relinked);
        Assert.Equal([false, true, true], view.Pointers.Select(p => p.Moved));
    }

    [Fact]
    public void Nothing_in_a_linked_list_is_marked_the_first_time_or_across_functions()
    {
        var states = Build(Run(
            ListFrame(1, Linked(1, [(1, "a", null)])),
            ListFrame(5, Linked(2, [(2, "z", null)]), function: "reverse")), Head);

        foreach (var view in states.Select(s => Assert.Single(s.Lists)))
        {
            Assert.DoesNotContain(view.Nodes, n => n.Added || n.Changed || n.Relinked);
            Assert.DoesNotContain(view.Pointers, p => p.Moved);
        }
    }

    [Fact]
    public void A_linked_list_is_left_out_while_its_variable_holds_no_node()
    {
        var frame = new TraceFrame("line", 1, "<module>", [], new Dictionary<string, TrackedArray>());

        Assert.Empty(Build(Run(frame), Head)[0].Lists);
    }

    [Fact]
    public void A_linked_list_visual_survives_a_pack_round_trip_with_its_options()
    {
        var step = new TraceStep("step.t", "Reverse", "Watch.", "head = None\n", [new Visual("head", Visual.LinkedList, ["curr"], "link", "data")]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "A", ["lesson.one"])], [new Lesson("lesson.one", "One", "track.a", [step])]);
        ms.Position = 0;

        var loaded = Assert.IsType<TraceStep>(ContentPackFormat.Load(ms).Get<Lesson>("lesson.one").Steps[0]);

        Assert.Equal(step.Visuals[0], loaded.Visuals[0] with { Pointers = step.Visuals[0].Pointers, Functions = step.Visuals[0].Functions });
        Assert.Equal(["curr"], loaded.Visuals[0].Pointers);
    }

    [Fact]
    public void A_call_stack_visual_survives_a_pack_round_trip_with_its_functions()
    {
        var step = new TraceStep("step.t", "Calls", "Watch.", "f()\n", [new Visual("", Visual.CallStack, [], Functions: ["fact"])]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "A", ["lesson.one"])], [new Lesson("lesson.one", "One", "track.a", [step])]);
        ms.Position = 0;

        var visual = Assert.IsType<TraceStep>(ContentPackFormat.Load(ms).Get<Lesson>("lesson.one").Steps[0]).Visuals.Single();

        Assert.Equal(Visual.CallStack, visual.As);
        Assert.Equal(["fact"], visual.Functions);
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

    // ---- Call stack ----

    private static readonly Visual CallStack = new("", Visual.CallStack, []);

    private static RecordedCall Call(int id, string n, string? returning = null) => new(id, "fact", [new CallArg("n", n)], returning);

    private static TraceFrame CallFrame(int? line, string function, TrackedCalls calls, params TraceLocal[] locals) =>
        new(line is null || calls.Frames.Any(c => c.Returning is not null) ? "return" : "line", line, function, locals,
            new Dictionary<string, TrackedArray>(), Calls: calls);

    private static TrackedCalls Calls(params RecordedCall[] calls) => new(calls, 0, null);

    [Fact]
    public void The_watch_list_includes_the_call_stack_with_its_functions()
    {
        Assert.Equal("""["nums",{"as":"call_stack","functions":["fact"]}]""",
            TraceResultJson.SerializeWatch([Nums, new Visual("", Visual.CallStack, [], Functions: ["fact"]), CallStack]));
        Assert.Equal("""[{"as":"call_stack","functions":[]}]""", TraceResultJson.SerializeWatch([CallStack]));
    }

    [Fact]
    public void Calls_in_the_tracer_json_parse()
    {
        const string json = """
            {"status":"ok","stdout":"","message":null,"traceback":null,"errorLine":null,"frames":[
              {"event":"return","line":3,"function":"fact","locals":[],"tracked":{},"tables":{},"lists":{},
               "calls":{"frames":[{"id":0,"function":"fact","args":[{"name":"n","repr":"2"}],"returning":null},
                                  {"id":1,"function":"fact","args":[{"name":"n","repr":"1"}],"returning":"1"}],
                        "more":0,"returned":{"function":"g","value":"5","to":0}}}]}
            """;
        var calls = TraceResultJson.Parse(json).Frames.Single().Calls!;
        Assert.Equal([0, 1], calls.Frames.Select(c => c.Id));
        Assert.Equal(new CallArg("n", "1"), calls.Frames[1].Args.Single());
        Assert.Equal("1", calls.Frames[1].Returning);
        Assert.Equal(new ReturnedValue("g", "5", 0), calls.Returned);
    }

    [Fact]
    public void The_call_stack_is_drawn_only_when_a_visual_asks_for_it()
    {
        var run = Run(CallFrame(2, "fact", Calls(Call(0, "3"))));
        Assert.Null(Build(run).Single().Calls);
        var view = Build(run, CallStack).Single().Calls!;
        Assert.Equal("fact(n=3)", view.Running!.Label);
        Assert.False(view.Running.Pushed);
    }

    [Fact]
    public void A_call_that_began_since_the_step_before_is_pushed_even_in_another_function()
    {
        var states = Build(Run(
            CallFrame(6, "<module>", Calls()),
            CallFrame(2, "fact", Calls(Call(0, "2"))),
            CallFrame(4, "fact", Calls(Call(0, "2"))),
            CallFrame(2, "fact", Calls(Call(0, "2"), Call(1, "1")))), CallStack);
        Assert.Equal([true], states[1].Calls!.Frames.Select(f => f.Pushed));
        Assert.Equal([false], states[2].Calls!.Frames.Select(f => f.Pushed));
        Assert.Equal([false, true], states[3].Calls!.Frames.Select(f => f.Pushed));
    }

    [Fact]
    public void A_return_shows_on_the_returning_call_then_on_the_caller_and_the_call_is_gone()
    {
        var states = Build(Run(
            CallFrame(3, "fact", Calls(Call(0, "2"), Call(1, "1", returning: "1"))),
            CallFrame(4, "fact", new TrackedCalls([Call(0, "2", returning: "2")], 0, new ReturnedValue("fact", "1", 0))),
            CallFrame(null, "<module>", new TrackedCalls([], 0, new ReturnedValue("fact", "2", null)))), CallStack);

        Assert.Equal("1", states[0].Calls!.Running!.Returning);
        var caller = states[1].Calls!.Running!;
        Assert.Equal(new ReturnedValue("fact", "1", 0), caller.Received);
        Assert.Equal("2", caller.Returning);
        Assert.Equal([1], states[1].Calls!.Gone.Select(g => g.Id));
        Assert.Null(states[1].Calls!.ToModule);

        var last = states[2].Calls!;
        Assert.Empty(last.Frames);
        Assert.Equal([0], last.Gone.Select(g => g.Id));
        Assert.Equal(new ReturnedValue("fact", "2", null), last.ToModule);
    }

    [Fact]
    public void A_value_received_is_shown_for_one_step_only()
    {
        var states = Build(Run(
            CallFrame(4, "fact", new TrackedCalls([Call(0, "2")], 0, new ReturnedValue("fact", "1", 0))),
            CallFrame(5, "fact", Calls(Call(0, "2")))), CallStack);
        Assert.NotNull(states[0].Calls!.Running!.Received);
        Assert.Null(states[1].Calls!.Running!.Received);
        Assert.Empty(states[1].Calls!.Gone);
    }

    [Fact]
    public void A_call_returned_to_and_a_new_call_in_one_step_are_told_apart()
    {
        // fib(n - 1) returned to fib(2), which at once called fib(n - 2).
        var states = Build(Run(
            CallFrame(3, "fib", Calls(Call(0, "2"), Call(1, "1", returning: "1"))),
            CallFrame(2, "fib", new TrackedCalls([Call(0, "2"), Call(2, "0")], 0, new ReturnedValue("fib", "1", 0)))), CallStack);
        var view = states[1].Calls!;
        Assert.Equal([1], view.Gone.Select(g => g.Id));
        Assert.Equal([false, true], view.Frames.Select(f => f.Pushed));
        Assert.NotNull(view.Frames[0].Received);
        Assert.Null(view.Frames[1].Received);
    }

    [Fact]
    public void A_call_cut_off_at_the_bottom_is_not_gone_and_one_coming_back_into_view_is_not_pushed()
    {
        var states = Build(Run(
            CallFrame(2, "f", new TrackedCalls([Call(0, "3"), Call(1, "2")], 0, null)),
            CallFrame(2, "f", new TrackedCalls([Call(1, "2"), Call(2, "1")], 1, null)),
            CallFrame(2, "f", new TrackedCalls([Call(0, "3"), Call(1, "2")], 0, null))), CallStack);
        Assert.Empty(states[1].Calls!.Gone);
        Assert.Equal([false, true], states[1].Calls!.Frames.Select(f => f.Pushed));
        Assert.Equal(1, states[1].Calls!.More);
        Assert.Equal([2], states[2].Calls!.Gone.Select(g => g.Id));
        Assert.All(states[2].Calls!.Frames, f => Assert.False(f.Pushed));
    }

    [Fact]
    public void Locals_are_still_compared_only_within_one_function_when_the_call_stack_is_drawn()
    {
        var states = Build(Run(
            CallFrame(1, "<module>", Calls(), Int("n", 1)),
            CallFrame(2, "fact", Calls(Call(0, "1")), Int("n", 1))), CallStack);
        Assert.False(states[1].Locals.Single().Changed);
    }

    [Fact]
    public void A_visual_kind_without_a_variable_can_sit_beside_other_visuals()
    {
        var frame = CallFrame(2, "f", Calls(Call(0, "1"))) with
        {
            Tables = new Dictionary<string, TrackedTable> { ["memo"] = Dict(("1", "1")) },
        };
        var state = Build(Run(frame), new Visual("memo", Visual.Dict, []), CallStack).Single();
        Assert.Single(state.Tables);
        Assert.Single(state.Calls!.Frames);
    }

    // ---- Trees ----

    private static readonly Visual Root = new("root", Visual.Tree, ["node"]);
    private static readonly Visual Walked = Root with { Visited = "order" };

    private static TrackedTree Tree(int? root, (int Id, string Value, int? Left, int? Right)[] nodes, int more = 0,
        TreeVisited? visited = null, params (string Name, int? Node)[] pointers) =>
        new(root, nodes.Select(n => new TreeNode(n.Id, n.Value, n.Left, n.Right)).ToList(), more,
            pointers.ToDictionary(p => p.Name, p => p.Node), visited);

    private static TraceFrame TreeFrame(int? line, TrackedTree tree, string function = "<module>") =>
        new(line is null ? "return" : "line", line, function, [], new Dictionary<string, TrackedArray>(),
            Trees: new Dictionary<string, TrackedTree> { ["root"] = tree });

    private static TreeView TreeAt(IReadOnlyList<TraceState> states, int step) => Assert.Single(states[step].Trees);

    // 4 -> (2 -> 1, 3), (6 -> -, 7), level by level as the recorder lists it.
    private static readonly (int, string, int?, int?)[] Small =
        [(4, "4", 2, 6), (2, "2", 1, 3), (6, "6", null, 7), (1, "1", null, null), (3, "3", null, null), (7, "7", null, null)];

    [Fact]
    public void The_watch_list_sends_a_tree_with_its_options_pointers_and_visited_list()
    {
        var watch = TraceResultJson.SerializeWatch([new Visual("root", Visual.Tree, ["node"], Value: "key", Left: "lo", Right: "hi", Visited: "order"),
            new Visual("t", Visual.Tree, [])]);

        Assert.Equal("""[{"variable":"root","as":"tree","left":"lo","right":"hi","value":"key","pointers":["node"],"visited":"order"},""" +
            """{"variable":"t","as":"tree","left":"left","right":"right","value":"val","pointers":[],"visited":null}]""", watch);
    }

    [Fact]
    public void Trees_in_the_tracer_json_parse_with_their_nodes_pointers_and_visited_marks()
    {
        var json = """
            {"status": "ok", "frames": [{"event": "line", "line": 1, "function": "<module>", "locals": [], "tracked": {},
              "trees": {"root": {"root": 0, "more": 3, "nodes": [{"id": 0, "value": "5", "left": 1, "right": null}, {"id": 1, "value": null, "left": null, "right": null}],
                                 "pointers": {"node": 1, "gone": null}, "visited": {"ordered": true, "nodes": [{"id": 1, "order": 1}], "unmatched": 2}}}}],
             "stdout": "", "message": null, "traceback": null, "errorLine": null}
            """;

        var tree = Assert.Single(TraceResultJson.Parse(json).Frames).Trees!["root"];

        Assert.Equal((0, 3), (tree.Root, tree.More));
        Assert.Equal([new TreeNode(0, "5", 1, null), new TreeNode(1, null, null, null)], tree.Nodes);
        Assert.Equal(1, tree.Pointers["node"]);
        Assert.Null(tree.Pointers["gone"]);
        Assert.Equal((true, 2), (tree.Visited!.Ordered, tree.Visited.Unmatched));
        Assert.Equal([new VisitedNode(1, 1)], tree.Visited.Nodes);
    }

    [Fact]
    public void A_trace_recorded_before_trees_existed_has_none()
    {
        var states = Build(Run(Frame(1)), Root);
        Assert.Empty(states[0].Trees);
    }

    [Fact]
    public void A_tree_is_laid_out_in_order_left_to_right_with_each_level_below_the_last()
    {
        var view = TreeAt(Build(Run(TreeFrame(1, Tree(4, Small))), Root), 0);

        Assert.Equal([1, 2, 3, 4, 6, 7], view.Nodes.Select(n => n.Id));
        Assert.Equal([0, 1, 2, 3, 4, 5], view.Nodes.Select(n => n.Column));
        Assert.Equal([2, 1, 2, 0, 1, 2], view.Nodes.Select(n => n.Depth));
        Assert.Equal((6, 3), (view.Columns, view.Levels));
        var six = view.Nodes.Single(n => n.Id == 6);
        Assert.Equal((TreeLink.None, TreeLink.Child, (int?)7), (six.LeftLink, six.RightLink, six.Right));
        Assert.All(view.Nodes, n => Assert.Equal(0, n.Tree));
        Assert.Equal([new NodePointer("root", 4, false)], view.Pointers);
        Assert.Null(view.Visited);
    }

    [Fact]
    public void An_empty_tree_has_no_nodes_and_its_variable_is_at_none()
    {
        var view = TreeAt(Build(Run(TreeFrame(1, Tree(null, []))), Root), 0);

        Assert.Empty(view.Nodes);
        Assert.Equal((0, 0), (view.Columns, view.Levels));
        Assert.Equal([new NodePointer("root", null, false)], view.Pointers);
    }

    [Fact]
    public void A_child_beyond_the_recorded_nodes_is_a_link_to_more()
    {
        var view = TreeAt(Build(Run(TreeFrame(1, Tree(1, [(1, "a", 2, 9), (2, "b", null, null)], more: 5))), Root), 0);

        var root = view.Nodes.Single(n => n.Id == 1);
        Assert.Equal((TreeLink.Child, TreeLink.More), (root.LeftLink, root.RightLink));
        Assert.Equal(5, view.More);
    }

    [Fact]
    public void A_shared_node_is_drawn_once_under_its_first_parent_and_a_cycle_points_back()
    {
        // 1 -> (2, 3); 3's left is 2 as well; 2's right is the root.
        var view = TreeAt(Build(Run(TreeFrame(1, Tree(1, [(1, "a", 2, 3), (2, "b", null, 1), (3, "c", 2, null)]))), Root), 0);

        Assert.Equal([2, 1, 3], view.Nodes.Select(n => n.Id));
        Assert.Equal(TreeLink.Elsewhere, view.Nodes.Single(n => n.Id == 3).LeftLink);
        Assert.Equal(TreeLink.Elsewhere, view.Nodes.Single(n => n.Id == 2).RightLink);
    }

    [Fact]
    public void A_detached_subtree_from_a_pointer_is_drawn_to_the_right_as_a_tree_of_its_own()
    {
        var tree = Tree(1, [(1, "a", null, null), (8, "x", 9, null), (9, "y", null, null)], 0, null, ("node", 8));
        var view = TreeAt(Build(Run(TreeFrame(1, tree)), Root), 0);

        Assert.Equal([(1, 0, 0, 0), (9, 1, 1, 1), (8, 1, 2, 0)], view.Nodes.Select(n => (n.Id, n.Tree, n.Column, n.Depth)));
        Assert.Equal([new NodePointer("root", 1, false), new NodePointer("node", 8, false)], view.Pointers);
    }

    [Fact]
    public void With_the_root_at_none_a_subtree_only_a_pointer_holds_is_detached_not_the_roots_tree()
    {
        // curr = root; root = None: the recorder lists curr's nodes, but none of them is the root's.
        var tree = Tree(null, [(7, "7", 8, null), (8, "8", null, null), (5, "5", null, null)], 0, null, ("node", 7), ("other", 5));
        var view = TreeAt(Build(Run(TreeFrame(1, tree)), Root with { Pointers = ["node", "other"] }), 0);

        Assert.Equal([(8, 1, 0, 1), (7, 1, 1, 0), (5, 2, 2, 0)], view.Nodes.Select(n => (n.Id, n.Tree, n.Column, n.Depth)));
        Assert.Equal([new NodePointer("root", null, false), new NodePointer("node", 7, false), new NodePointer("other", 5, false)], view.Pointers);
    }

    [Fact]
    public void A_pointer_the_recorder_left_out_is_not_drawn_and_one_at_none_is()
    {
        var view = TreeAt(Build(Run(TreeFrame(1, Tree(1, [(1, "a", null, null)], 0, null, ("node", null)))),
            new Visual("root", Visual.Tree, ["node", "other"])), 0);

        Assert.Equal(["root", "node"], view.Pointers.Select(p => p.Name));
        Assert.Null(view.Pointers[1].Node);
    }

    [Fact]
    public void An_inserted_node_is_added_a_changed_value_is_marked_and_a_moved_pointer_is_moved()
    {
        var states = Build(Run(
            TreeFrame(1, Tree(4, [(4, "4", 2, null), (2, "2", null, null)], 0, null, ("node", 4))),
            TreeFrame(2, Tree(4, [(4, "40", 2, 5), (2, "2", null, null), (5, "5", null, null)], 0, null, ("node", 5)))), Root);

        Assert.All(TreeAt(states, 0).Nodes, n => Assert.False(n.Added || n.Changed));
        var view = TreeAt(states, 1);
        Assert.Equal([(2, false, false), (4, false, true), (5, true, false)], view.Nodes.Select(n => (n.Id, n.Added, n.Changed)));
        Assert.Equal([false, true], view.Pointers.Select(p => p.Moved));
    }

    [Fact]
    public void A_tree_is_not_compared_across_functions()
    {
        var states = Build(Run(
            TreeFrame(1, Tree(4, [(4, "4", null, null)])),
            TreeFrame(2, Tree(4, [(4, "5", null, 6), (6, "6", null, null)]), function: "insert")), Root);

        Assert.All(TreeAt(states, 1).Nodes, n => Assert.False(n.Added || n.Changed));
    }

    [Fact]
    public void A_visited_list_numbers_its_nodes_and_marks_the_newly_visited_one()
    {
        TreeVisited Marks(params (int Id, int Order)[] marks) => new(true, marks.Select(m => new VisitedNode(m.Id, m.Order)).ToList(), 0);
        var states = Build(Run(
            TreeFrame(1, Tree(4, Small, visited: Marks((1, 1)))),
            TreeFrame(2, Tree(4, Small, visited: Marks((1, 1), (2, 2))))), Walked);

        var view = TreeAt(states, 1);
        Assert.Equal(new TreeVisitedView("order", true, 0), view.Visited);
        Assert.Equal([(1, true, (int?)1, false), (2, true, 2, true), (3, false, null, false)],
            view.Nodes.Take(3).Select(n => (n.Id, n.Visited, n.Order, n.JustVisited)));
    }

    [Fact]
    public void A_visited_set_marks_its_nodes_with_no_numbers()
    {
        var visited = new TreeVisited(false, [new VisitedNode(4, null)], 3);
        var view = TreeAt(Build(Run(TreeFrame(1, Tree(4, Small, visited: visited))), Root with { Visited = "seen" }), 0);

        Assert.Equal(new TreeVisitedView("seen", false, 3), view.Visited);
        var root = view.Nodes.Single(n => n.Id == 4);
        Assert.Equal((true, (int?)null), (root.Visited, root.Order));
        Assert.Single(view.Nodes, n => n.Visited);
    }

    [Fact]
    public void A_tree_visual_survives_a_pack_round_trip_with_its_options()
    {
        var step = new TraceStep("step.t", "Walk", "Watch.", "root = None\n",
            [new Visual("root", Visual.Tree, ["node"], Value: "key", Left: "lo", Right: "hi", Visited: "order")]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "A", ["lesson.one"])], [new Lesson("lesson.one", "One", "track.a", [step])]);
        ms.Position = 0;

        var visual = Assert.IsType<TraceStep>(ContentPackFormat.Load(ms).Get<Lesson>("lesson.one").Steps[0]).Visuals.Single();

        Assert.Equal(("lo", "hi", "key", "order"), (visual.Left, visual.Right, visual.Value, visual.Visited));
        Assert.Equal(["node"], visual.Pointers);
    }

    private static readonly Visual Dp = new("dp", Visual.Grid, []);

    private static TrackedGrid Grid(string[][] rows, params (string Name, int[]? At)[] pointers) =>
        new(rows, rows.Select(r => r.Length).ToList(), 0,
            pointers.ToDictionary(p => p.Name, p => p.At is null ? null : (IReadOnlyList<int>?)p.At));

    private static TraceFrame GridFrame(int? line, TrackedGrid grid, string function = "<module>") =>
        new(line is null ? "return" : "line", line, function, [], new Dictionary<string, TrackedArray>(),
            Grids: new Dictionary<string, TrackedGrid> { ["dp"] = grid });

    private static GridView GridAt(IReadOnlyList<TraceState> states, int index) => states[index].Grids.Single();

    [Fact]
    public void A_grid_is_watched_with_its_pairs_labels_and_highlight()
    {
        var visual = Dp with { Pointers = ["cell"], Cells = [new CellPair("i", "j")], RowLabels = "a", ColLabels = "b", Highlight = "seen" };

        Assert.Equal("""[{"variable":"dp","as":"grid","pointers":["cell"],"cells":[["i","j"]],"rowLabels":"a","colLabels":"b","highlight":"seen"}]""",
            TraceResultJson.SerializeWatch([visual]));
    }

    [Fact]
    public void A_grid_is_read_from_the_recorder_json()
    {
        var result = TraceResultJson.Parse("""
            {"status":"ok","stdout":"","message":null,"traceback":null,"errorLine":null,"frames":[{"event":"line","line":1,"function":"<module>",
             "locals":[],"tracked":{},"grids":{"dp":{"rows":[["0","1"]],"lengths":[4],"more":2,"pointers":{"i,j":[0,1],"cell":null},
             "rowLabels":["a"],"colLabels":null,"highlight":{"cells":[[0,0]],"unmatched":1}}}}]}
            """);

        var grid = result.Frames[0].Grids!["dp"];
        Assert.Equal((4, 2, "1"), (grid.Lengths[0], grid.More, grid.Rows[0][1]));
        Assert.Equal([0, 1], grid.Pointers["i,j"]);
        Assert.Null(grid.Pointers["cell"]);
        Assert.Equal((1, 0), (grid.Highlight!.Unmatched, grid.Highlight.Cells[0][1]));
    }

    [Fact]
    public void A_grid_is_drawn_row_by_row_with_the_cells_cut_off_counted()
    {
        var tracked = new TrackedGrid([["0", "1"], ["2"]], [5, 1], 3, new Dictionary<string, IReadOnlyList<int>?>());
        var view = GridAt(Build(Run(GridFrame(1, tracked)), Dp), 0);

        Assert.Equal([["0", "1"], ["2"]], view.Rows.Select(r => r.Cells.Select(c => c.Text)));
        Assert.Equal([3, 0], view.Rows.Select(r => r.More));
        Assert.Equal((3, 2), (view.More, view.Columns));
        Assert.DoesNotContain(view.Rows.SelectMany(r => r.Cells), c => c.Changed || c.Added || c.Shaded);
    }

    [Fact]
    public void A_cell_that_changed_flashes_and_one_that_came_in_grows()
    {
        var states = Build(Run(GridFrame(1, Grid([["0", "0"]])), GridFrame(2, Grid([["0", "5"], ["7"]]))), Dp);
        var cells = GridAt(states, 1).Rows.SelectMany(r => r.Cells).Select(c => (c.Text, c.Changed, c.Added));

        Assert.Equal([("0", false, false), ("5", true, false), ("7", false, true)], cells);
    }

    [Fact]
    public void A_grid_marks_nothing_against_another_function()
    {
        var states = Build(Run(GridFrame(1, Grid([["0"]])), GridFrame(2, Grid([["9"], ["1"]]), "f")), Dp);

        Assert.DoesNotContain(GridAt(states, 1).Rows.SelectMany(r => r.Cells), c => c.Changed || c.Added);
    }

    [Fact]
    public void Grid_pointers_are_listed_in_order_and_say_where_they_are()
    {
        var visual = Dp with { Pointers = ["cell", "gone"], Cells = [new CellPair("i", "j"), new CellPair("r", "c")] };
        var tracked = Grid([["0", "0"], ["0", "0"]], ("i,j", [1, 0]), ("r,c", [5, 0]), ("cell", null));
        var view = GridAt(Build(Run(GridFrame(1, tracked)), visual), 0);

        Assert.Equal([
            new GridPointerView("i,j", "i", "j", 1, 0, true, true, false),
            new GridPointerView("r,c", "r", "c", 5, 0, true, false, false),
            new GridPointerView("cell", "cell", "cell", null, null, true, false, false),
            new GridPointerView("gone", "gone", "gone", null, null, false, false, false),
        ], view.Pointers);
    }

    [Fact]
    public void A_grid_pointer_at_a_negative_index_is_off_the_grid()
    {
        var visual = Dp with { Cells = [new CellPair("i", "j"), new CellPair("r", "c")] };
        var tracked = Grid([["1"]], ("i,j", [-1, 0]), ("r,c", [0, -1]));
        var view = GridAt(Build(Run(GridFrame(1, tracked)), visual), 0);

        Assert.Equal([(-1, 0, false), (0, -1, false)], view.Pointers.Select(p => (p.Row!.Value, p.Col!.Value, p.OnGrid)));
    }

    [Fact]
    public void A_grid_pointer_that_moved_is_marked()
    {
        var visual = Dp with { Cells = [new CellPair("i", "j")] };
        var states = Build(Run(
            GridFrame(1, Grid([["0", "0"]], ("i,j", [0, 0]))),
            GridFrame(2, Grid([["0", "0"]], ("i,j", [0, 1]))),
            GridFrame(3, Grid([["0", "0"]], ("i,j", [0, 1])))), visual);

        Assert.Equal([false, true, false], states.Select(s => s.Grids.Single().Pointers.Single().Moved));
    }

    [Fact]
    public void Labels_line_up_with_the_rows_and_columns_from_the_offset()
    {
        var visual = Dp with { RowLabels = "a", ColLabels = "b", LabelOffset = 1 };
        var tracked = Grid([["0", "0", "0"], ["0", "0", "0"]]) with { RowLabels = ["x"], ColLabels = ["p", "q", "r"] };
        var view = GridAt(Build(Run(GridFrame(1, tracked)), visual), 0);

        Assert.Equal([null, "x"], view.RowLabels!);
        Assert.Equal([null, "p", "q"], view.ColLabels!);
    }

    [Fact]
    public void Labels_not_drawn_are_null_and_ones_not_recorded_are_blank()
    {
        var tracked = Grid([["0"]]);

        Assert.Null(GridAt(Build(Run(GridFrame(1, tracked)), Dp), 0).RowLabels);
        Assert.Equal([null], GridAt(Build(Run(GridFrame(1, tracked)), Dp with { RowLabels = "a" }), 0).RowLabels!);
    }

    [Fact]
    public void A_highlight_shades_its_cells_and_counts_the_unmatched()
    {
        var tracked = Grid([["0", "0"], ["0", "0"]]) with { Highlight = new GridHighlight([[0, 1], [1, 0]], 2) };
        var view = GridAt(Build(Run(GridFrame(1, tracked)), Dp with { Highlight = "seen" }), 0);

        Assert.Equal([(0, 1), (1, 0)], view.Rows.SelectMany(r => r.Cells).Where(c => c.Shaded).Select(c => (c.Row, c.Col)));
        Assert.Equal(new GridHighlightView("seen", 2), view.Highlight);
    }

    [Fact]
    public void A_grid_visual_survives_a_pack_round_trip_with_its_options()
    {
        var step = new TraceStep("step.g", "Fill", "Watch.", "dp = []\n",
            [Dp with { Pointers = ["cell"], Cells = [new CellPair("i", "j")], RowLabels = "a", ColLabels = "b", LabelOffset = 1, Highlight = "seen" }]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "A", ["lesson.one"])], [new Lesson("lesson.one", "One", "track.a", [step])]);
        ms.Position = 0;

        var visual = Assert.IsType<TraceStep>(ContentPackFormat.Load(ms).Get<Lesson>("lesson.one").Steps[0]).Visuals.Single();

        Assert.Equal(("a", "b", 1, "seen"), (visual.RowLabels, visual.ColLabels, visual.LabelOffset, visual.Highlight));
        Assert.Equal([new CellPair("i", "j")], visual.Cells);
        Assert.Equal(["cell"], visual.Pointers);
    }

    // ---- Graphs ----

    private static readonly Visual G = new("g", Visual.Graph, []);

    private static TrackedGraph Graph(string[] labels, (int From, int To)[] edges, params (string Name, int? Node)[] pointers) =>
        new(labels.Select((l, i) => new GraphNode(i, l)).ToList(), 0, edges.Select(e => new GraphEdge(e.From, e.To, null)).ToList(), 0,
            pointers.ToDictionary(p => p.Name, p => p.Node));

    private static TraceFrame GraphFrame(int? line, TrackedGraph graph, string function = "<module>") =>
        new(line is null ? "return" : "line", line, function, [], new Dictionary<string, TrackedArray>(),
            Graphs: new Dictionary<string, TrackedGraph> { ["g"] = graph });

    private static GraphView GraphAt(IReadOnlyList<TraceState> states, int index) => states[index].Graphs.Single();

    [Fact]
    public void A_graph_is_watched_with_its_weights_pointers_edge_and_marks()
    {
        var visual = G with { Pointers = ["node"], Weights = true, Edge = new EdgeNames("node", "nb"), Visited = "seen", Frontier = "queue", Directed = true };

        Assert.Equal("""[{"variable":"g","as":"graph","weights":true,"pointers":["node","nb"],"visited":"seen","frontier":"queue"}]""",
            TraceResultJson.SerializeWatch([visual]));
    }

    [Fact]
    public void A_graph_is_read_from_the_recorder_json()
    {
        var result = TraceResultJson.Parse("""
            {"status":"ok","stdout":"","message":null,"traceback":null,"errorLine":null,"frames":[{"event":"line","line":1,"function":"<module>",
             "locals":[],"tracked":{},"graphs":{"g":{"nodes":[{"id":3,"label":"A"},{"id":5,"label":"B"}],"more":2,
             "edges":[{"from":3,"to":5,"weight":"4"}],"moreEdges":1,"pointers":{"node":3,"gone":null},
             "visited":{"nodes":[3],"unmatched":1},"frontier":null}}}]}
            """);

        var graph = result.Frames[0].Graphs!["g"];
        Assert.Equal((2, 1, "B"), (graph.More, graph.MoreEdges, graph.Nodes[1].Label));
        Assert.Equal(new GraphEdge(3, 5, "4"), graph.Edges.Single());
        Assert.Equal((3, (int?)null), (graph.Pointers["node"], graph.Pointers["gone"]));
        Assert.Equal([3], graph.Visited!.Nodes);
        Assert.Equal(1, graph.Visited.Unmatched);
        Assert.Null(graph.Frontier);
    }

    [Fact]
    public void A_node_keeps_its_place_round_the_circle_through_the_run()
    {
        var states = Build(Run(
            GraphFrame(1, new TrackedGraph([new GraphNode(7, "x")], 0, [], 0, new Dictionary<string, int?>())),
            GraphFrame(2, new TrackedGraph([new GraphNode(9, "y"), new GraphNode(7, "x")], 0, [], 0, new Dictionary<string, int?>()))), G);

        Assert.Equal([(7, 0)], GraphAt(states, 0).Nodes.Select(n => (n.Id, n.Slot)));
        Assert.Equal([(9, 1), (7, 0)], GraphAt(states, 1).Nodes.Select(n => (n.Id, n.Slot)));
        Assert.Equal([2, 2], states.Select(s => s.Graphs.Single().Slots));
    }

    [Fact]
    public void An_undirected_graph_draws_an_edge_listed_both_ways_once()
    {
        var view = GraphAt(Build(Run(GraphFrame(1, Graph(["A", "B", "C"], [(0, 1), (1, 0), (1, 2)]))), G), 0);

        Assert.Equal([(0, 1, false), (1, 2, false)], view.Edges.Select(e => (e.From, e.To, e.Both)));
    }

    [Fact]
    public void A_directed_graph_draws_both_ways_as_two_edges_that_say_so()
    {
        var view = GraphAt(Build(Run(GraphFrame(1, Graph(["A", "B", "C"], [(0, 1), (1, 0), (1, 2), (2, 2)]))), G with { Directed = true }), 0);

        Assert.Equal([(0, 1, true), (1, 0, true), (1, 2, false), (2, 2, false)], view.Edges.Select(e => (e.From, e.To, e.Both)));
        Assert.True(view.Directed);
    }

    [Fact]
    public void Nodes_and_edges_that_came_in_are_marked_against_the_same_function_only()
    {
        var first = Graph(["A"], []);
        var second = Graph(["A", "B"], [(0, 1)]);
        var states = Build(Run(GraphFrame(1, first), GraphFrame(2, second), GraphFrame(3, second with { Edges = [] }, "f"), GraphFrame(4, second, "f")), G);

        Assert.Equal([(0, false), (1, true)], GraphAt(states, 1).Nodes.Select(n => (n.Id, n.Added)));
        Assert.True(GraphAt(states, 1).Edges.Single().Added);
        Assert.DoesNotContain(GraphAt(states, 2).Nodes, n => n.Added);
        Assert.True(GraphAt(states, 3).Edges.Single().Added);
        Assert.DoesNotContain(GraphAt(states, 0).Nodes, n => n.Added);
    }

    [Theory]
    [InlineData(false, 1, 0, true)]
    [InlineData(true, 1, 0, false)]
    [InlineData(true, 0, 1, true)]
    public void The_edge_being_explored_is_active(bool directed, int from, int to, bool active)
    {
        var visual = G with { Directed = directed, Edge = new EdgeNames("node", "nb") };
        var view = GraphAt(Build(Run(GraphFrame(1, Graph(["A", "B"], [(0, 1)], ("node", from), ("nb", to)))), visual), 0);

        Assert.Equal(active, view.Edges.Single().Active);
        Assert.Empty(view.Pointers);
    }

    [Fact]
    public void No_edge_is_active_while_an_end_is_none()
    {
        var visual = G with { Edge = new EdgeNames("node", "nb") };
        var view = GraphAt(Build(Run(GraphFrame(1, Graph(["A", "B"], [(0, 1)], ("node", 0), ("nb", null)))), visual), 0);

        Assert.False(view.Edges.Single().Active);
    }

    [Fact]
    public void Graph_pointers_are_listed_in_order_and_marked_when_they_move()
    {
        var visual = G with { Pointers = ["node", "nb", "unset"] };
        var states = Build(Run(
            GraphFrame(1, Graph(["A", "B"], [], ("node", 0), ("nb", null))),
            GraphFrame(2, Graph(["A", "B"], [], ("node", 1), ("nb", null)))), visual);

        Assert.Equal([new NodePointer("node", 0, false), new NodePointer("nb", null, false)], GraphAt(states, 0).Pointers);
        Assert.Equal([new NodePointer("node", 1, true), new NodePointer("nb", null, false)], GraphAt(states, 1).Pointers);
    }

    [Fact]
    public void Visited_and_frontier_mark_their_nodes_and_what_was_just_marked()
    {
        var visual = G with { Visited = "seen", Frontier = "queue" };
        var first = Graph(["A", "B", "C"], []) with { Visited = new GraphMarks([0], 0), Frontier = new GraphMarks([0], 0) };
        var second = first with { Visited = new GraphMarks([0, 1], 2), Frontier = new GraphMarks([2, 1], 1) };
        var states = Build(Run(GraphFrame(1, first), GraphFrame(2, second)), visual);
        var view = GraphAt(states, 1);

        Assert.Equal([(true, (int?)null, false, false), (true, 2, true, true), (false, 1, false, true)],
            view.Nodes.Select(n => (n.Visited, n.Queued, n.JustVisited, n.JustQueued)));
        Assert.Equal((new GraphMarksView("seen", 2), new GraphMarksView("queue", 1)), (view.Visited!, view.Frontier!));
        Assert.DoesNotContain(GraphAt(states, 0).Nodes, n => n.JustVisited || n.JustQueued);
    }

    [Fact]
    public void Marks_not_drawn_are_null()
    {
        var tracked = Graph(["A"], []) with { Visited = new GraphMarks([0], 0) };
        var view = GraphAt(Build(Run(GraphFrame(1, tracked)), G), 0);

        Assert.Null(view.Visited);
    }

    [Fact]
    public void A_graph_visual_survives_a_pack_round_trip_with_its_options()
    {
        var step = new TraceStep("step.g", "Search", "Watch.", "g = {}\n",
            [G with { Pointers = ["node"], Directed = true, Weights = true, Edge = new EdgeNames("node", "nb"), Visited = "seen", Frontier = "queue" }]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion),
            [new Track("track.a", "A", ["lesson.one"])], [new Lesson("lesson.one", "One", "track.a", [step])]);
        ms.Position = 0;

        var visual = Assert.IsType<TraceStep>(ContentPackFormat.Load(ms).Get<Lesson>("lesson.one").Steps[0]).Visuals.Single();

        Assert.Equal((true, true, new EdgeNames("node", "nb"), "seen", "queue"), (visual.Directed, visual.Weights, visual.Edge, visual.Visited, visual.Frontier));
        Assert.Equal(["node"], visual.Pointers);
    }

    // ---- Heaps ----

    private static readonly Visual Heap = new("heap", Visual.Heap, ["i"]);

    private static TraceFrame HeapFrame(int? line, string[] cells, string function = "<module>", int more = 0, params TraceLocal[] locals) =>
        new(line is null ? "return" : "line", line, function, locals, new Dictionary<string, TrackedArray>(),
            Heaps: new Dictionary<string, TrackedHeap> { ["heap"] = new("list", cells, more) });

    private static HeapView HeapAt(IReadOnlyList<TraceState> states, int index) => states[index].Heaps.Single();

    private static IEnumerable<(int, string, bool, bool, bool)> Marks(HeapView view) =>
        view.Cells.Select(c => (c.Id, c.Text, c.Changed, c.Added, c.Moved));

    [Fact]
    public void The_watch_list_sends_a_heap_by_name_and_kind()
    {
        Assert.Equal("""[{"variable":"heap","as":"heap"}]""", TraceResultJson.SerializeWatch([Heap with { Max = true }]));
    }

    [Fact]
    public void Heaps_in_the_tracer_json_parse_with_their_cells()
    {
        var json = """
            {"status": "ok", "frames": [{"event": "line", "line": 1, "function": "<module>", "locals": [], "tracked": {},
              "heaps": {"heap": {"type": "list", "cells": ["1", "5"], "more": 2}}}],
             "stdout": "", "message": null, "traceback": null, "errorLine": null}
            """;

        var heap = Assert.Single(TraceResultJson.Parse(json).Frames).Heaps!["heap"];

        Assert.Equal(("list", 2), (heap.Type, heap.More));
        Assert.Equal(["1", "5"], heap.Cells);
    }

    [Fact]
    public void The_first_step_has_ids_by_index_and_marks_nothing()
    {
        var view = HeapAt(Build(Run(HeapFrame(1, ["1", "5", "2"])), Heap with { Max = true }), 0);

        Assert.Equal([(0, "1", false, false, false), (1, "5", false, false, false), (2, "2", false, false, false)], Marks(view));
        Assert.True(view.Max);
        Assert.Empty(view.Gone);
    }

    [Fact]
    public void A_swap_moves_both_cells_and_they_keep_their_ids()
    {
        var states = Build(Run(HeapFrame(1, ["5", "1", "2"]), HeapFrame(2, ["1", "5", "2"])), Heap);

        Assert.Equal([(1, "1", false, false, true), (0, "5", false, false, true), (2, "2", false, false, false)], Marks(HeapAt(states, 1)));
    }

    [Fact]
    public void A_push_adds_the_new_value_and_moves_the_parent_it_passed()
    {
        // heapq.heappush([2, 5], 1) in one step: 1 sifts up past 2.
        var states = Build(Run(HeapFrame(1, ["2", "5"]), HeapFrame(2, ["1", "5", "2"])), Heap);

        Assert.Equal([(2, "1", false, true, false), (1, "5", false, false, false), (0, "2", false, false, true)], Marks(HeapAt(states, 1)));
        Assert.Empty(HeapAt(states, 1).Gone);
    }

    [Fact]
    public void A_pop_leaves_a_ghost_where_the_root_was_and_moves_the_last_cell_up()
    {
        var states = Build(Run(HeapFrame(1, ["1", "5", "2"]), HeapFrame(2, ["2", "5"])), Heap);

        var view = HeapAt(states, 1);
        Assert.Equal([(2, "2", false, false, true), (1, "5", false, false, false)], Marks(view));
        Assert.Equal(new HeapGoneCell(0, "1", 0), Assert.Single(view.Gone));
    }

    [Fact]
    public void A_value_written_over_another_changes_that_cell()
    {
        var states = Build(Run(HeapFrame(1, ["1", "5"]), HeapFrame(2, ["9", "5"])), Heap);

        Assert.Equal([(0, "9", true, false, false), (1, "5", false, false, false)], Marks(HeapAt(states, 1)));
    }

    [Fact]
    public void A_repeated_value_moves_from_the_nearest_cell_holding_it()
    {
        var states = Build(Run(HeapFrame(1, ["3", "3", "1", "3"]), HeapFrame(2, ["1", "3", "3", "3"])), Heap);

        // Index 1 and 3 stay; the 1 moved up from 2; index 2's 3 is the nearest unmatched 3, the one at 0.
        Assert.Equal([(2, "1", false, false, true), (1, "3", false, false, false), (0, "3", false, false, true), (3, "3", false, false, false)],
            Marks(HeapAt(states, 1)));
    }

    [Fact]
    public void New_ids_never_reuse_a_ghost_s()
    {
        var states = Build(Run(HeapFrame(1, ["1", "2"]), HeapFrame(2, ["2"]), HeapFrame(3, ["2", "7"])), Heap);

        Assert.Equal(new HeapGoneCell(0, "1", 0), Assert.Single(HeapAt(states, 1).Gone));
        Assert.Equal([(1, "2", false, false, false), (2, "7", false, true, false)], Marks(HeapAt(states, 2)));
    }

    [Fact]
    public void Past_five_levels_cells_moving_in_and_out_of_view_are_neither_added_nor_gone()
    {
        var states = Build(Run(HeapFrame(1, ["1", "2"], more: 3), HeapFrame(2, ["2", "3"], more: 2)), Heap);

        var view = HeapAt(states, 1);
        Assert.Equal([(1, "2", false, false, true), (2, "3", false, false, false)], Marks(view));
        Assert.Empty(view.Gone);
        Assert.Equal(2, view.More);
    }

    [Fact]
    public void Pointers_are_int_locals_on_a_cell_and_moving_one_marks_it()
    {
        var states = Build(Run(HeapFrame(1, ["1", "5", "2"], locals: Int("i", 0)), HeapFrame(2, ["1", "5", "2"], locals: Int("i", 2)),
            HeapFrame(3, ["1", "5", "2"], locals: Int("i", 3))), Heap);

        Assert.Equal(new PointerView("i", 0, false), HeapAt(states, 0).Pointers.Single());
        Assert.Equal(new PointerView("i", 2, true), HeapAt(states, 1).Pointers.Single());
        Assert.Equal(new PointerView("i", null, true), HeapAt(states, 2).Pointers.Single());
    }

    [Fact]
    public void A_heap_is_only_compared_with_the_step_before_in_the_same_function()
    {
        var states = Build(Run(HeapFrame(1, ["5", "1"]), HeapFrame(2, ["1", "5"], function: "sift")), Heap);

        Assert.Equal([(0, "1", false, false, false), (1, "5", false, false, false)], Marks(HeapAt(states, 1)));
    }

    [Fact]
    public void A_step_where_the_variable_is_not_a_heap_draws_none()
    {
        var states = Build(Run(Frame(1), HeapFrame(2, [])), Heap);

        Assert.Empty(states[0].Heaps);
        Assert.Empty(HeapAt(states, 1).Cells);
    }
}
