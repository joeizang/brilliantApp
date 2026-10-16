using System.Runtime.CompilerServices;
using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Brilliant.Runtime.CSharp;

namespace Brilliant.Runtime.CSharp.Tests;

public sealed class TraceRecorderTests
{
    private static string TracesDirectory([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Traces");

    private static readonly IReadOnlyDictionary<string, Visual[]> Watched = new Dictionary<string, Visual[]>
    {
        ["array-scan"] = [new("nums", Visual.Array, ["i"])],
        ["stack-and-queue"] = [new("stack", Visual.Stack, []), new("queue", Visual.Queue, [])],
        ["list-of-doubles"] = [new("values", Visual.Array, [])],
        ["dict-and-set"] = [new("counts", Visual.Dict, ["key"]), new("seen", Visual.Set, [])],
        ["linked-list"] = [new("head", Visual.LinkedList, ["prev", "cur"])],
        ["circular-list"] = [new("a", Visual.LinkedList, ["walker"], Next: "After", Value: "Item")],
        ["tree"] = [new("root", Visual.Tree, [], Value: "Key", Visited: "seen")],
        ["graph-bfs"] = [new("graph", Visual.Graph, ["node"], Visited: "seen") { Frontier = "queue" }],
        ["graph-weighted"] = [new("roads", Visual.Graph, ["here"]), new("tuples", Visual.Graph, []) { Weights = true }],
        ["call-stack-recursion"] = [new("", Visual.CallStack, [])],
        ["heap"] = [new("heap", Visual.Heap, ["i"])],
        ["grid"] = [new("grid", Visual.Grid, []) { Cells = [new("row", "col")], RowLabels = "names", Highlight = "path" }, new("square", Visual.Grid, []), new("rows", Visual.Grid, [])],
    };

    public static TheoryData<string> Fixtures() =>
        new(Directory.GetFiles(TracesDirectory(), "*.snippet").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).ToArray());

    private static TraceResult Record(string name)
    {
        var code = File.ReadAllText(Path.Combine(TracesDirectory(), name + ".snippet"));
        return CSharpTraceRecorder.Record(code, Watched.GetValueOrDefault(name, []));
    }

    // Golden files: a change to what is recorded is a change to every pack, so it shows up as a diff here.
    // Rewrite them with UPDATE_GOLDENS=1 after reading the diff.
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A_recording_matches_its_golden_file(string name)
    {
        var json = TraceResultJson.Serialize(Record(name)).ReplaceLineEndings("\n") + "\n";
        var golden = Path.Combine(TracesDirectory(), name + ".trace.json");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1") File.WriteAllText(golden, json);
        Assert.True(File.Exists(golden), $"No golden file for {name}; run with UPDATE_GOLDENS=1 to write it.");
        Assert.Equal(File.ReadAllText(golden).ReplaceLineEndings("\n"), json);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Recording_the_same_source_again_gives_the_same_text(string name)
    {
        Assert.Equal(TraceResultJson.Serialize(Record(name)), TraceResultJson.Serialize(Record(name)));
    }

    [Fact]
    public void Strings_and_chars_are_shown_as_c_literals_without_json_escapes()
    {
        var last = Record("text").Frames[^1];
        var reprs = last.Locals.ToDictionary(l => l.Name, l => l.Repr);
        Assert.Equal("\"it's <b> a+b & café\"", reprs["word"]);
        Assert.Equal("'é'", reprs["letter"]);
        Assert.Equal("'\\''", reprs["quote"]);
        Assert.Equal("[\"<li>\", \"naïve\", \"tab\\there\"]", reprs["words"]);
    }

    [Fact]
    public void A_stack_deeper_than_the_cap_keeps_its_bottom_items()
    {
        var result = CSharpTraceRecorder.Record("var stack = new Stack<int>();\nfor (var i = 0; i < 200; i++) stack.Push(i);\nvar done = true;", [new("stack", Visual.Stack, [])]);
        var cells = result.Frames[^1].Tracked!["stack"].Cells;
        Assert.Equal("0", cells[0]);
        Assert.Equal(200, result.Frames[^1].Tracked!["stack"].Cells.Count + result.Frames[^1].Tracked!["stack"].More);
    }

    [Fact]
    public void A_recording_survives_the_json_a_pack_stores()
    {
        var result = Record("array-scan");
        Assert.Equal(TraceResultJson.Serialize(result), TraceResultJson.Serialize(TraceResultJson.Parse(TraceResultJson.Serialize(result))));
    }

    [Fact]
    public void The_run_ends_with_a_return_frame_holding_the_final_variables()
    {
        var result = Record("counter-loop");
        Assert.True(result.Status == TraceStatus.Ok, result.Message);
        var last = result.Frames[^1];
        Assert.Equal("return", last.Event);
        Assert.Null(last.Line);
        Assert.Equal("3", last.Locals.Single(l => l.Name == "total").Repr);
        Assert.Equal("3", result.Stdout.Trim());
    }

    [Fact]
    public void A_line_frame_is_the_state_before_that_line_with_the_learners_line_number()
    {
        var result = Record("counter-loop");
        var first = result.Frames[0];
        Assert.Equal("line", first.Event);
        Assert.Equal(1, first.Line);
        Assert.Empty(first.Locals);
        Assert.Equal(2, result.Frames[1].Line);
        Assert.Equal("0", result.Frames[1].Locals.Single().Repr);
    }

    [Fact]
    public void Integer_variables_carry_their_value_so_pointers_can_be_drawn()
    {
        var result = Record("array-scan");
        var frame = result.Frames.First(f => f.Locals.Any(l => l.Name == "i"));
        var i = frame.Locals.Single(l => l.Name == "i");
        Assert.Equal("int", i.Type);
        Assert.Equal(1, i.IntegerValue());
    }

    [Fact]
    public void A_watched_array_is_tracked_with_its_cells_and_csharp_type_name()
    {
        var result = Record("array-scan");
        var tracked = result.Frames[^1].Tracked["nums"];
        Assert.Equal("int[]", tracked.Type);
        Assert.Equal(["4", "1", "7"], tracked.Cells);
        Assert.True(tracked.Draws(Visual.Array));
        Assert.False(tracked.Draws(Visual.Stack));
        Assert.Equal("int[]", result.Frames[^1].Locals.Single(l => l.Name == "nums").Type);
    }

    [Fact]
    public void A_stack_is_drawn_bottom_to_top_and_a_queue_front_to_back()
    {
        var last = Record("stack-and-queue").Frames[^1];
        Assert.Equal(["1"], last.Tracked["stack"].Cells);
        Assert.Equal(["\"b\""], last.Tracked["queue"].Cells);
        var middle = Record("stack-and-queue").Frames.First(f => f.Line == 7);
        Assert.Equal(["1", "2"], middle.Tracked["stack"].Cells);
        Assert.Equal(["\"a\"", "\"b\""], middle.Tracked["queue"].Cells);
        Assert.True(middle.Tracked["stack"].Draws(Visual.Stack));
        Assert.True(middle.Tracked["queue"].Draws(Visual.Queue));
        Assert.Equal("Stack<int>", middle.Tracked["stack"].Type);
    }

    [Fact]
    public void A_list_is_drawn_as_an_array_and_values_read_as_csharp_literals()
    {
        var last = Record("list-of-doubles").Frames[^1];
        Assert.Equal(["1.5", "2.25", "3"], last.Tracked["values"].Cells);
        Assert.Equal("List<double>", last.Tracked["values"].Type);
        Assert.Equal("\"hi\"", last.Locals.Single(l => l.Name == "text").Repr);
        Assert.Equal("string", last.Locals.Single(l => l.Name == "text").Type);
    }

    [Fact]
    public void A_variable_that_is_not_watched_is_not_tracked()
    {
        var result = CSharpTraceRecorder.Record("int[] a = { 1 };\nvar b = a.Length;", []);
        Assert.Empty(result.Frames[^1].Tracked);
    }

    [Fact]
    public void Each_frame_names_the_method_it_is_in_and_locals_come_with_their_values()
    {
        var result = Record("recursion");
        Assert.Equal("Main", result.Frames[0].Function);
        var inFact = result.Frames.Where(f => f.Function == "Fact").ToArray();
        Assert.NotEmpty(inFact);
        Assert.Contains(inFact, f => f.Locals.Any(l => l is { Name: "n", Repr: "1" }));
        Assert.Equal("Main", result.Frames[^1].Function);
        Assert.Equal("6", result.Frames[^1].Locals.Single(l => l.Name == "answer").Repr);
    }

    [Fact]
    public void A_user_type_shows_its_fields()
    {
        var last = Record("objects").Frames[^1];
        Assert.Equal("Point", last.Locals.Single(l => l.Name == "p").Type);
        Assert.Equal("Point { X = 5, Y = 2 }", last.Locals.Single(l => l.Name == "p").Repr);
    }

    [Fact]
    public void An_exception_is_an_error_that_keeps_the_frames_up_to_it_and_the_line()
    {
        var result = Record("fails");
        Assert.Equal(TraceStatus.Error, result.Status);
        Assert.Contains("IndexOutOfRangeException", result.Message);
        Assert.Equal(4, result.ErrorLine);
        Assert.Equal("before", result.Stdout.Trim());
        Assert.Equal(4, result.Frames[^1].Line);
        Assert.DoesNotContain(result.Frames, f => f.Event == "return");
    }

    [Fact]
    public void Code_that_does_not_compile_is_an_error_on_its_line()
    {
        var result = CSharpTraceRecorder.Record("int a = 1;\nint b = \"two\";", []);
        Assert.Equal(TraceStatus.Error, result.Status);
        Assert.Equal(2, result.ErrorLine);
        Assert.Contains("CS0029", result.Message);
        Assert.Empty(result.Frames);
    }

    [Fact]
    public void A_run_longer_than_the_step_limit_is_truncated_not_hung()
    {
        var result = CSharpTraceRecorder.Record("int n = 0;\nwhile (true) { n++; }", []);
        Assert.Equal(TraceStatus.Truncated, result.Status);
        Assert.NotEmpty(result.Frames);
    }

    [Fact]
    public void A_run_with_more_steps_than_the_player_shows_is_truncated_at_the_frame_limit()
    {
        var result = CSharpTraceRecorder.Record("int n = 0;\nfor (int i = 0; i < 1000; i++) { n += i; }", []);
        Assert.Equal(TraceStatus.Truncated, result.Status);
        Assert.Equal(CSharpTraceRecorder.MaxFrames, result.Frames.Count);
    }

    [Fact]
    public void Unsupported_syntax_is_refused_with_a_reason()
    {
        var result = CSharpTraceRecorder.Record("async System.Threading.Tasks.Task F() { await System.Threading.Tasks.Task.Yield(); }", []);
        Assert.Equal(TraceStatus.Error, result.Status);
        Assert.Contains("async", result.Message);
    }

    [Fact]
    public void A_dictionary_key_keeps_its_id_through_the_run_and_a_variable_holding_it_is_a_hit()
    {
        var frames = Record("dict-and-set").Frames;
        var withB = frames.Select(f => f.Tables?.GetValueOrDefault("counts")).OfType<TrackedTable>()
            .Select(t => t.Rows.FirstOrDefault(r => r.Key == "\"b\"")).Where(r => r is not null).Select(r => r!.Id).Distinct().ToArray();
        Assert.Equal([0], withB);
        Assert.Equal(0, frames[^1].Tables!["counts"].Hits!["key"]);
    }

    [Fact]
    public void A_set_is_listed_in_sorted_order_and_a_dictionary_in_its_own()
    {
        var result = CSharpTraceRecorder.Record("var set = new HashSet<int> { 30, 4, 12 };\nvar map = new Dictionary<int, string> { [9] = \"x\", [2] = \"y\" };\nvar done = 1;",
            [new("set", Visual.Set, []), new("map", Visual.Dict, [])]);
        var last = result.Frames[^1].Tables!;
        Assert.Equal(["4", "12", "30"], last["set"].Rows.Select(r => r.Key));
        Assert.Equal(["9", "2"], last["map"].Rows.Select(r => r.Key));
        Assert.All(last["set"].Rows, row => Assert.Null(row.Value));
    }

    [Fact]
    public void A_big_dictionary_records_twenty_rows_and_counts_the_rest()
    {
        var result = CSharpTraceRecorder.Record("var map = new Dictionary<int, int>();\nfor (var i = 0; i < 50; i++) map[i] = i * i;\nvar done = 1;", [new("map", Visual.Dict, [])]);
        var table = result.Frames[^1].Tables!["map"];
        Assert.Equal(20, table.Rows.Count);
        Assert.Equal(30, table.More);
    }

    [Fact]
    public void A_heap_over_thirty_one_cells_and_a_grid_over_fifteen_rows_are_cut_off()
    {
        var result = CSharpTraceRecorder.Record("var heap = Enumerable.Range(0, 40).ToList();\nvar grid = new int[20][];\nfor (var r = 0; r < 20; r++) grid[r] = new int[18];\nvar done = 1;",
            [new("heap", Visual.Heap, []), new("grid", Visual.Grid, [])]);
        var last = result.Frames[^1];
        Assert.Equal(31, last.Heaps!["heap"].Cells.Count);
        Assert.Equal(9, last.Heaps["heap"].More);
        var grid = last.Grids!["grid"];
        Assert.Equal(15, grid.Rows.Count);
        Assert.Equal(5, grid.More);
        Assert.Equal(15, grid.Rows[0].Count);
        Assert.Equal(18, grid.Lengths[0]);
    }

    [Fact]
    public void A_visual_survives_the_json_the_cli_sends_the_worker()
    {
        var visual = new Visual("grid", Visual.Grid, ["p"]) { Cells = [new("r", "c")], RowLabels = "names", Highlight = "path" };
        var back = System.Text.Json.JsonSerializer.Deserialize<Visual>(System.Text.Json.JsonSerializer.Serialize(visual, ContentPackFormat.JsonOptions), ContentPackFormat.JsonOptions)!;
        Assert.Equal("path", back.Highlight);
        Assert.Equal("names", back.RowLabels);
        Assert.Equal("r,c", back.Cells.Single().Name);
    }

    [Fact]
    public void A_node_keeps_its_id_at_every_step_and_a_cycle_ends_at_the_node_it_returns_to()
    {
        var frames = Record("circular-list").Frames;
        var ids = frames.Select(f => f.Lists?.GetValueOrDefault("a")).OfType<TrackedList>().Select(l => l.Head).Distinct().ToArray();
        Assert.Equal([0], ids);
        var last = frames[^1].Lists!["a"];
        Assert.Equal(2, last.Nodes.Count);
        Assert.Equal(last.Head, last.Nodes[1].Next);
        Assert.Equal(0, last.More);
    }

    [Fact]
    public void A_list_reversed_in_place_changes_its_links_but_not_its_ids()
    {
        var lists = Record("linked-list").Frames.Select(f => f.Lists?.GetValueOrDefault("head")).OfType<TrackedList>().ToArray();
        Assert.Equal(lists[0].Nodes.Select(n => n.Id).Order(), lists[^1].Nodes.Select(n => n.Id).Order());
        Assert.NotEqual(lists[0].Nodes.First().Next, lists[^1].Nodes.First(n => n.Id == lists[0].Nodes[0].Id).Next);
    }

    [Fact]
    public void A_list_longer_than_thirty_nodes_records_thirty_and_counts_the_rest()
    {
        var result = CSharpTraceRecorder.Record("var head = new Node(0);\nvar tail = head;\nfor (var i = 1; i < 50; i++) { tail.Next = new Node(i); tail = tail.Next; }\nvar done = 1;\nclass Node { public int Val; public Node? Next; public Node(int v) { Val = v; } }",
            [new("head", Visual.LinkedList, ["tail"])]);
        var list = result.Frames[^1].Lists!["head"];
        Assert.Equal(30, list.Nodes.Count);
        Assert.Equal(20, list.More);
    }

    [Fact]
    public void A_tree_marks_the_nodes_its_visited_list_holds_in_order()
    {
        var tree = Record("tree").Frames[^1].Trees!["root"];
        Assert.True(tree.Visited!.Ordered);
        Assert.Equal([1, 2, 3, 4, 5], tree.Visited.Nodes.Select(n => n.Order!.Value).Order());
        Assert.Equal(0, tree.Visited.Unmatched);
        var byValue = tree.Nodes.ToDictionary(n => n.Value!, n => n.Id);
        Assert.Equal(1, tree.Visited.Nodes.Single(n => n.Id == byValue["1"]).Order);
    }

    [Fact]
    public void A_cycle_still_reads_as_a_cycle_in_the_locals()
    {
        Assert.StartsWith("Link { Item = \"a\", After = Link { Item = \"b\", After = <cycl", Record("circular-list").Frames[^1].Locals.First(l => l.Name == "a").Repr);
    }

    [Fact]
    public void A_graph_marks_the_nodes_visited_and_queued_in_the_order_met()
    {
        var graph = Record("graph-bfs").Frames[^1].Graphs!["graph"];
        Assert.Equal(["A", "B", "C", "D"], graph.Nodes.Select(n => n.Label));
        Assert.Equal([0, 1, 2, 3], graph.Visited!.Nodes);
        Assert.Empty(graph.Frontier!.Nodes);
        Assert.Equal(3, graph.Pointers["node"]);
        Assert.Equal(4, graph.Edges.Count);
    }

    [Fact]
    public void A_graph_of_more_than_twenty_nodes_records_twenty_and_counts_the_rest()
    {
        var result = CSharpTraceRecorder.Record("var g = new Dictionary<int, int[]>();\nfor (var i = 0; i < 25; i++) g[i] = new[] { (i + 1) % 25 };\nvar done = 1;",
            [new("g", Visual.Graph, [])]);
        var graph = result.Frames[^1].Graphs!["g"];
        Assert.Equal(20, graph.Nodes.Count);
        Assert.Equal(5, graph.More);
        Assert.Equal(19, graph.Edges.Count);   // the edge out of node 19 goes to a node cut off
        Assert.Equal(6, graph.MoreEdges);
    }

    [Fact]
    public void A_graph_over_objects_gives_each_object_the_same_id_at_every_step()
    {
        var result = CSharpTraceRecorder.Record("var a = new City(\"A\");\nvar b = new City(\"B\");\nvar roads = new Dictionary<City, List<City>>();\nroads[a] = new() { b };\nroads[b] = new() { a };\nvar done = 1;\nrecord City(string Name);",
            [new("roads", Visual.Graph, [])]);
        var last = result.Frames[^1].Graphs!["roads"];
        Assert.Equal(2, last.Nodes.Count);
        Assert.Equal(2, last.Edges.Count);
        var first = result.Frames.First(f => f.Graphs?.ContainsKey("roads") == true && f.Graphs["roads"].Nodes.Count > 0).Graphs!["roads"];
        Assert.All(first.Nodes, node => Assert.Contains(node.Id, last.Nodes.Select(n => n.Id)));
    }

    [Fact]
    public void A_weighted_graph_reads_weights_from_nested_dictionaries_and_from_tuples()
    {
        var graphs = Record("graph-weighted").Frames[^1].Graphs!;
        Assert.Equal(["7", "9", "10"], graphs["roads"].Edges.Select(e => e.Weight));
        Assert.Equal(["4", "4"], graphs["tuples"].Edges.Select(e => e.Weight));
        Assert.Equal(2, graphs["roads"].Pointers["here"]);
    }

    [Fact]
    public void Each_call_has_its_own_id_and_the_stack_grows_and_shrinks_with_them()
    {
        var frames = Record("call-stack-recursion").Frames;
        Assert.Equal(3, frames.Max(f => f.Calls!.Frames.Count));
        var deepest = frames.First(f => f.Calls!.Frames.Count == 3).Calls!.Frames;
        Assert.Equal([0, 1, 2], deepest.Select(c => c.Id));
        Assert.Equal(["3", "2", "1"], deepest.Select(c => c.Args.Single().Repr));
        Assert.All(frames, frame => Assert.NotNull(frame.Calls));
        Assert.Empty(frames[^1].Calls!.Frames);
    }

    [Fact]
    public void A_return_is_a_step_of_its_own_with_the_value_and_the_next_step_carries_what_came_back()
    {
        var frames = Record("call-stack-recursion").Frames.ToList();
        var returns = frames.Where(f => f.Event == "return" && f.Line is not null).ToArray();
        Assert.Equal(["1", "2", "6", "void"], returns.Select(f => f.Calls!.Frames[^1].Returning));
        var after = frames[frames.IndexOf(returns[0]) + 1];
        Assert.Equal(new ReturnedValue("Fact", "1", 1), after.Calls!.Returned);
        Assert.Null(frames[frames.IndexOf(after) + 1].Calls!.Returned);
        Assert.Equal(new ReturnedValue("Fact", "6", null), frames[frames.IndexOf(returns[2]) + 1].Calls!.Returned);
    }

    [Fact]
    public void The_functions_filter_keeps_only_the_named_calls_and_a_return_of_the_rest_is_not_a_step()
    {
        var code = File.ReadAllText(Path.Combine(TracesDirectory(), "call-stack-recursion.snippet"));
        var result = CSharpTraceRecorder.Record(code, [new("", Visual.CallStack, []) { Functions = ["Print"] }]);
        Assert.All(result.Frames.SelectMany(f => f.Calls!.Frames), call => Assert.Equal("Print", call.Function));
        Assert.DoesNotContain(result.Frames, f => f.Event == "return" && f.Function == "Fact");
    }

    [Fact]
    public void A_stack_deeper_than_twelve_calls_shows_the_innermost_and_counts_the_rest()
    {
        var result = CSharpTraceRecorder.Record("var r = Down(20);\nint Down(int n) => n == 0 ? 0 : Down(n - 1);", [new("", Visual.CallStack, [])]);
        var deepest = result.Frames.OrderByDescending(f => f.Calls!.Frames.Count + f.Calls.More).First().Calls!;
        Assert.Equal(12, deepest.Frames.Count);
        Assert.Equal(9, deepest.More);
        Assert.Equal(20, deepest.Frames[^1].Id);
    }

    [Fact]
    public void A_call_that_throws_leaves_the_stack_and_a_catch_carries_on()
    {
        var result = CSharpTraceRecorder.Record("var ok = 0;\ntry { Boom(1); } catch (InvalidOperationException) { ok = 1; }\nvar after = Quiet(2);\nint Boom(int n) { throw new InvalidOperationException(); }\nint Quiet(int n) => n;",
            [new("", Visual.CallStack, [])]);
        Assert.True(result.Status == TraceStatus.Ok, result.Message);
        var quiet = result.Frames.First(f => f.Function == "Quiet").Calls!.Frames;
        Assert.Equal(["Quiet"], quiet.Select(c => c.Function));
        Assert.Equal(1, quiet.Single().Id);
    }

    [Fact]
    public void Without_a_call_stack_nothing_about_calls_is_recorded_and_no_return_is_a_step()
    {
        var result = CSharpTraceRecorder.Record("var x = F(2);\nint F(int n) { return n + 1; }", []);
        Assert.All(result.Frames, frame => Assert.Null(frame.Calls));
        Assert.DoesNotContain(result.Frames, f => f.Event == "return" && f.Line is not null);
    }
}
