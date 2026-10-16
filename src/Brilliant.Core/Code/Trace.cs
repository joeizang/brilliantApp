using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Brilliant.Core.Content;

namespace Brilliant.Core.Code;

/// <summary>
/// How a trace ended. <see cref="Error"/>: the code never compiled or raised (the frames up to that point are kept).
/// <see cref="Truncated"/>: it ran past the recorder's step limit and was stopped.
/// </summary>
public enum TraceStatus { Ok, Error, Truncated }

/// <summary>A local variable at one step. <see cref="Repr"/> is the runtime-provided representation, clipped.</summary>
public sealed record TraceLocal(string Name, string Type, string Repr,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Index = null)
{
    /// <summary>Integer value supplied by the runtime for sequence pointers. Missing on legacy Python traces.</summary>
    public int? IntegerValue() => Index ?? (Type == "int" && int.TryParse(Repr, out var value) ? value : null);
}

/// <summary>A recorded sequence: runtime type label, cell representations (front to back), omitted count and supported drawings.
/// The runtime supplies drawing capabilities independently of its display type name.</summary>
public sealed record TrackedArray(string Type, IReadOnlyList<string> Cells, int More,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Drawings = null)
{
    public bool Draws(string kind) => Drawings?.Contains(kind) ?? LegacyPythonDraws(kind);

    // Compatibility for traces recorded before runtimes supplied semantic drawing capabilities.
    // New runtimes must set Drawings; type labels themselves are otherwise opaque.
    private bool LegacyPythonDraws(string kind) => kind == Visual.Array ||
        (kind is Visual.Stack or Visual.Queue && Type is "list" or "deque");
}

/// <summary>
/// One dict entry or set member as recorded: the repr of the key, and of the value (null for a set). <see cref="Key"/> is only a
/// label (clipped, so different keys can share one); <see cref="Id"/> tells the rows apart and is the same for a key at every step.
/// It is null in a trace recorded before ids existed.
/// </summary>
public sealed record TableRow(string Key, string? Value, int? Id = null);

/// <summary>
/// A watched dict or set as recorded at one step. <see cref="Kind"/> is "dict" or "set" (a subclass or a frozenset counts as its base);
/// <see cref="Type"/> is the actual type name. A set's rows are sorted; a dict's are in insertion order.
/// <see cref="Hits"/> maps each local variable that holds a key (or member) of the table, by Python's own equality, to that key's id;
/// the id belongs to no row when the row was cut off. It is null in a trace recorded before lookups were.
/// </summary>
public sealed record TrackedTable(string Kind, string Type, IReadOnlyList<TableRow> Rows, int More, IReadOnlyDictionary<string, int>? Hits = null);

/// <summary>A linked-list node as recorded: its id (its identity, the same at every step), the repr of its value (null when it has none), and the id of the node it links to (null at the end).</summary>
public sealed record ListNode(int Id, string? Value, int? Next);

/// <summary>
/// A watched linked list as recorded at one step. <see cref="Head"/> is the id of the node the variable holds, null when it holds None.
/// <see cref="Nodes"/> are the nodes reached from the head, then those reached only from a pointer (detached), each run of them in link
/// order; a node whose <c>Next</c> is an id not among them links to one beyond the ones recorded, and <see cref="More"/> counts those.
/// <see cref="Pointers"/> maps each pointer that holds a node to its id, or to null when it holds None.
/// </summary>
public sealed record TrackedList(int? Head, IReadOnlyList<ListNode> Nodes, int More, IReadOnlyDictionary<string, int?> Pointers);

/// <summary>A binary tree node as recorded: its id (its identity, the same at every step), the repr of its value (null when it has none), and the ids of its children (null: None).</summary>
public sealed record TreeNode(int Id, string? Value, int? Left, int? Right);

/// <summary>A node the visited list or set marks: its id, and for a list its place in it (1 for the first), null for a set.</summary>
public sealed record VisitedNode(int Id, int? Order);

/// <summary>
/// The nodes a tree's visited list or set marks, as recorded: a member marks the node it is, or else the nodes whose value equals it
/// (in a list, the first such node no earlier member marked). <see cref="Unmatched"/> counts the members that marked none.
/// </summary>
public sealed record TreeVisited(bool Ordered, IReadOnlyList<VisitedNode> Nodes, int Unmatched);

/// <summary>
/// A watched binary tree as recorded at one step. <see cref="Root"/> is the id of the node the variable holds, null when it holds None.
/// <see cref="Nodes"/> are the nodes reached from the root level by level, then those reached only from a pointer (a detached subtree);
/// a child id not among them is a node beyond the ones recorded, and <see cref="More"/> counts those. <see cref="Pointers"/> maps each
/// pointer that holds a node to its id, or to null when it holds None. <see cref="Visited"/> is null when no visited list or set is drawn
/// (or its variable holds neither).
/// </summary>
public sealed record TrackedTree(int? Root, IReadOnlyList<TreeNode> Nodes, int More, IReadOnlyDictionary<string, int?> Pointers,
    TreeVisited? Visited = null);

/// <summary>The cells a grid's highlight marks, as recorded ([row, col] each, sorted), and how many of its members marked no recorded cell.</summary>
public sealed record GridHighlight(IReadOnlyList<IReadOnlyList<int>> Cells, int Unmatched);

/// <summary>
/// A watched grid (a list of lists or strings) as recorded at one step: the first rows' first cells' reprs (a string's characters as they
/// are), each recorded row's full length in <see cref="Lengths"/>, and how many rows were cut off in <see cref="More"/>.
/// <see cref="Pointers"/> maps each pointer recorded (a pair "i,j" or a variable holding a pair) to its [row, col], or to null when it
/// holds None or a variable of the pair isn't an int; the pair can be off the grid. The labels are null when not drawn (or their variable
/// holds neither a string, list nor tuple); likewise <see cref="Highlight"/>.
/// </summary>
public sealed record TrackedGrid(IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<int> Lengths, int More,
    IReadOnlyDictionary<string, IReadOnlyList<int>?> Pointers, IReadOnlyList<string>? RowLabels = null, IReadOnlyList<string>? ColLabels = null,
    GridHighlight? Highlight = null);

/// <summary>A graph node as recorded: its id (by value, as a dict key, the same at every step) and its label (a string as it is, else the repr).</summary>
public sealed record GraphNode(int Id, string Label);

/// <summary>An edge as recorded, from a key of the dict to a neighbour it lists, with the repr of its weight (null: none given).</summary>
public sealed record GraphEdge(int From, int To, string? Weight);

/// <summary>The recorded nodes a graph's visited or frontier variable holds, in the order met, and how many of its members are none of them.</summary>
public sealed record GraphMarks(IReadOnlyList<int> Nodes, int Unmatched);

/// <summary>
/// A watched graph (a dict of each node's neighbours) as recorded at one step. <see cref="Nodes"/> are the keys, then the neighbours that
/// aren't keys; <see cref="More"/> counts the nodes cut off. <see cref="Edges"/> are in the dict's order, between recorded nodes;
/// <see cref="MoreEdges"/> counts the rest. <see cref="Pointers"/> maps each pointer that holds a recorded node to its id, or to null when
/// it holds None. <see cref="Visited"/> and <see cref="Frontier"/> are null when not drawn (or their variable holds none of their kinds).
/// </summary>
public sealed record TrackedGraph(IReadOnlyList<GraphNode> Nodes, int More, IReadOnlyList<GraphEdge> Edges, int MoreEdges,
    IReadOnlyDictionary<string, int?> Pointers, GraphMarks? Visited = null, GraphMarks? Frontier = null);

/// <summary>An argument of a call, as it was when the call was made: its name and the repr of its value, clipped.</summary>
public sealed record CallArg(string Name, string Repr);

/// <summary>
/// A call under way, as recorded. <see cref="Id"/> is the same at every step it is on the stack and is never given to another call of
/// the run. <see cref="Returning"/> is the repr of the value it returns, on the step it returns (and only on that step).
/// </summary>
public sealed record RecordedCall(int Id, string Function, IReadOnlyList<CallArg> Args, string? Returning);

/// <summary>What a call handed back since the step before: the function, the repr of the value, and the id of the call it went to (null: the module, or a call not recorded).</summary>
public sealed record ReturnedValue(string Function, string Value, int? To);

/// <summary>
/// The call stack as recorded at one step: the learner's calls under way, outermost first, so the last is the one running; at most the
/// recorder's limit of them, the innermost (<see cref="More"/> counts the outer ones cut off). <see cref="Returned"/> is what came back since the step before.
/// </summary>
public sealed record TrackedCalls(IReadOnlyList<RecordedCall> Frames, int More, ReturnedValue? Returned);

/// <summary>A heap's cells, front (index 0, its root) first, at most five levels of them; <paramref name="More"/> counts the rest.</summary>
public sealed record TrackedHeap(string Type, IReadOnlyList<string> Cells, int More);

/// <summary>
/// The state just before <see cref="Line"/> ran (<see cref="Event"/> "line"), the state the module finished in
/// (<see cref="Event"/> "return", <see cref="Line"/> null), or, while the call stack is watched, a function's state as it returns
/// (<see cref="Event"/> "return", <see cref="Line"/> the line it returns from).
/// </summary>
public sealed record TraceFrame(
    string Event,
    int? Line,
    string Function,
    IReadOnlyList<TraceLocal> Locals,
    IReadOnlyDictionary<string, TrackedArray> Tracked,
    IReadOnlyDictionary<string, TrackedTable>? Tables = null,
    IReadOnlyDictionary<string, TrackedList>? Lists = null,
    TrackedCalls? Calls = null,
    IReadOnlyDictionary<string, TrackedTree>? Trees = null,
    IReadOnlyDictionary<string, TrackedGrid>? Grids = null,
    IReadOnlyDictionary<string, TrackedGraph>? Graphs = null,
    IReadOnlyDictionary<string, TrackedHeap>? Heaps = null);

/// <summary>What the Python tracer (wwwroot/python/tracer.py) recorded.</summary>
public sealed record TraceResult(
    TraceStatus Status,
    IReadOnlyList<TraceFrame> Frames,
    string Stdout,
    string? Message,
    string? Traceback,
    int? ErrorLine);

/// <summary>Reads and writes the JSON of the Python tracer.</summary>
public static class TraceResultJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions WriteOptions = new(Options)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // readable text; it is JSON data, never HTML
    };

    /// <summary>The recording as JSON, indented and without nulls, in the form <see cref="Parse"/> reads: the same recording is the same text.</summary>
    public static string Serialize(TraceResult result) => JsonSerializer.Serialize(result, WriteOptions);

    public static TraceResult Parse(string json) =>
        JsonSerializer.Deserialize<TraceResult>(json, Options)
        ?? throw new InvalidOperationException("The code runtime returned an empty trace.");

    /// <summary>
    /// The variables the tracer should record: a sequence, dict or set by its name, a linked list as an object that also says how to
    /// follow it and which pointers to look up: <c>["nums",{"variable":"head","as":"linked_list","next":"next","value":"val","pointers":["curr"]}]</c>;
    /// a tree likewise: <c>{"variable":"root","as":"tree","left":"left","right":"right","value":"val","pointers":[],"visited":"order"}</c>
    /// (<c>visited</c> null when none is drawn); a grid with its pointers, pairs of int variables, labels and highlight:
    /// <c>{"variable":"dp","as":"grid","pointers":[],"cells":[["i","j"]],"rowLabels":"a","colLabels":"b","highlight":null}</c>; a graph with the
    /// pointers to look up (the edge's ends among them), whether neighbours carry weights, and its visited and frontier variables:
    /// <c>{"variable":"graph","as":"graph","weights":false,"pointers":["node","nb"],"visited":"seen","frontier":"queue"}</c>; a heap by its
    /// name and kind, its pointers read from the locals: <c>{"variable":"heap","as":"heap"}</c>; the call stack as an object with the functions it keeps (none: all): <c>{"as":"call_stack","functions":["fact"]}</c>.
    /// </summary>
    public static string SerializeWatch(IEnumerable<Visual> visuals)
    {
        static JsonArray Names(IEnumerable<string> names) => new(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());

        var watch = new JsonArray();
        var seen = new HashSet<string>();
        foreach (var v in visuals.Where(v => Visual.Kinds.Contains(v.As) && seen.Add(v.As == Visual.CallStack ? "<call stack>" : v.Variable)))
            watch.Add(v.As switch
            {
                Visual.LinkedList => new JsonObject
                {
                    ["variable"] = v.Variable, ["as"] = v.As, ["next"] = v.Next, ["value"] = v.Value, ["pointers"] = Names(v.Pointers),
                },
                Visual.Tree => new JsonObject
                {
                    ["variable"] = v.Variable, ["as"] = v.As, ["left"] = v.Left, ["right"] = v.Right, ["value"] = v.Value,
                    ["pointers"] = Names(v.Pointers), ["visited"] = v.Visited,
                },
                Visual.Grid => new JsonObject
                {
                    ["variable"] = v.Variable, ["as"] = v.As, ["pointers"] = Names(v.Pointers),
                    ["cells"] = new JsonArray(v.Cells.Select(c => (JsonNode?)Names([c.Row, c.Col])).ToArray()),
                    ["rowLabels"] = v.RowLabels, ["colLabels"] = v.ColLabels, ["highlight"] = v.Highlight,
                },
                Visual.Graph => new JsonObject
                {
                    ["variable"] = v.Variable, ["as"] = v.As, ["weights"] = v.Weights,
                    ["pointers"] = Names(v.Pointers.Concat(v.Edge is { } e ? [e.From, e.To] : []).Distinct()),
                    ["visited"] = v.Visited, ["frontier"] = v.Frontier,
                },
                Visual.Heap => new JsonObject { ["variable"] = v.Variable, ["as"] = v.As },
                Visual.CallStack => new JsonObject { ["as"] = v.As, ["functions"] = Names(v.Functions) },
                _ => JsonValue.Create(v.Variable),
            });
        return watch.ToJsonString();
    }
}

public sealed record LocalView(string Name, string Type, string Value, bool Changed);

/// <summary>
/// A cell. <see cref="Changed"/>: its value differs from the step before. <see cref="Added"/>: a stack or queue cell that came in at
/// one end since the step before (a push, an enqueue). <see cref="Id"/> tells the cells apart: an array cell's is its index; a stack or
/// queue cell keeps its id for as long as it stays in, wherever the cells around it come and go, so the player can slide it.
/// </summary>
public sealed record CellView(string Text, bool Changed, bool Added = false, int Id = 0);

/// <summary>
/// A stack or queue cell that left since the step before (a pop, a dequeue), shown for one step so the learner sees it go.
/// <see cref="AtFront"/>: it left from the front (index 0, a queue's front, a stack's bottom); otherwise from the back (a stack's top).
/// </summary>
public sealed record GoneCell(int Id, string Text, bool AtFront);

/// <summary>A named marker. <see cref="Index"/> is the cell it sits on, null when its value is not a cell of the array (missing, not an int, negative or past the end).</summary>
public sealed record PointerView(string Name, int? Index, bool Moved);

/// <summary>
/// A sequence drawn as cells, front (index 0) first. <see cref="Kind"/> is <see cref="Visual.Array"/>, <see cref="Visual.Stack"/> or
/// <see cref="Visual.Queue"/>; <see cref="Gone"/> the stack's or queue's cells that left since the step before (empty for an array).
/// <paramref name="More"/> counts the cells beyond the ones recorded.
/// </summary>
public sealed record ArrayView(string Variable, IReadOnlyList<CellView> Cells, int More, IReadOnlyList<PointerView> Pointers,
    string Kind = Visual.Array, IReadOnlyList<GoneCell>? Gone = null)
{
    public IReadOnlyList<GoneCell> Gone { get; init; } = Gone ?? [];
}

/// <summary>How a table row differs from the same row in the previous step.</summary>
public enum RowChange { None, Added, Updated, Removed }

/// <summary>
/// A dict entry or set member. <see cref="LookedUpBy"/> names the pointer variables holding this key. A <see cref="RowChange.Removed"/>
/// row is a ghost of what the previous step had, shown for one step so the learner sees it go; it is never looked up.
/// </summary>
public sealed record RowView(string Id, string Key, string? Value, RowChange Change, IReadOnlyList<string> LookedUpBy);

/// <summary>
/// A dict or set drawn as rows. <see cref="Misses"/> names the pointers that exist but hold a key that isn't a row (a failed lookup);
/// <see cref="More"/> counts the rows beyond the ones recorded.
/// </summary>
public sealed record TableView(string Variable, string Kind, IReadOnlyList<RowView> Rows, int More, IReadOnlyList<string> Misses);

/// <summary>
/// A linked-list node. <see cref="Next"/> is the id of the node it links to (null: None). <see cref="Changed"/>: its value differs from
/// the step before; <see cref="Relinked"/>: its link does (the arrow was rewired); <see cref="Added"/>: it wasn't drawn the step before.
/// </summary>
public sealed record NodeView(int Id, string? Value, int? Next, bool Changed = false, bool Relinked = false, bool Added = false);

/// <summary>How a run of linked nodes ends: at None, back on a node of its own run (a cycle), on a node of an earlier run, or past the nodes recorded.</summary>
public enum ChainEnd { None, Back, Join, More }

/// <summary>
/// Nodes in link order, drawn as one row. <see cref="Target"/> is the id of the node the last one links to when the chain ends
/// <see cref="ChainEnd.Back"/> or <see cref="ChainEnd.Join"/>.
/// </summary>
public sealed record ChainView(IReadOnlyList<NodeView> Nodes, ChainEnd End, int? Target = null);

/// <summary>A variable drawn on the node it holds; <see cref="Node"/> is null when it holds None (shown "at None").</summary>
public sealed record NodePointer(string Name, int? Node, bool Moved);

/// <summary>
/// A linked list drawn as rows of nodes. <see cref="Chains"/>[0] runs from the head (empty when the list is); the others are detached,
/// reached only from a pointer. <see cref="Pointers"/> starts with the variable itself, on the head. <see cref="More"/> counts the
/// nodes beyond the ones recorded.
/// </summary>
public sealed record LinkedListView(string Variable, IReadOnlyList<ChainView> Chains, int More, IReadOnlyList<NodePointer> Pointers)
{
    public IEnumerable<NodeView> Nodes => Chains.SelectMany(c => c.Nodes);
}

/// <summary>
/// Where a tree node's link goes: nowhere (None), to a child drawn under it, to a node drawn elsewhere (a node with two parents, or a
/// cycle back up the tree), or to a node beyond the ones recorded.
/// </summary>
public enum TreeLink { None, Child, Elsewhere, More }

/// <summary>
/// A tree node, placed: <see cref="Column"/> is its place left to right (the order an in-order walk meets it, counted across all the
/// trees drawn), <see cref="Depth"/> its level (0: a root) and <see cref="Tree"/> which tree it is in (0: the variable's; the others
/// are detached, reached only from a pointer). <see cref="Left"/> and <see cref="Right"/> are its children's ids (null: None), and
/// <see cref="LeftLink"/> and <see cref="RightLink"/> say how each is drawn. <see cref="Changed"/>: its value differs from the step
/// before; <see cref="Added"/>: it wasn't drawn the step before. <see cref="Order"/> is its place in the visited list (null: not
/// visited, or visited by a set, when <see cref="Visited"/> says so); <see cref="JustVisited"/>: it was marked since the step before.
/// </summary>
public sealed record TreeNodeView(int Id, string? Value, int Tree, int Column, int Depth, int? Left, int? Right, TreeLink LeftLink, TreeLink RightLink,
    bool Changed = false, bool Added = false, bool Visited = false, int? Order = null, bool JustVisited = false);

/// <summary>
/// A binary tree drawn level by level: each node in its own column, in in-order, so a node's left subtree is all to its left and its
/// right subtree to its right. <see cref="Pointers"/> starts with the variable itself, on the root. <see cref="More"/> counts the nodes
/// beyond the ones recorded. <see cref="Visited"/> is null when no visited list or set is drawn; otherwise it says whether it is a list
/// (numbered) and how many of its members marked no node.
/// </summary>
public sealed record TreeView(string Variable, IReadOnlyList<TreeNodeView> Nodes, int More, IReadOnlyList<NodePointer> Pointers, TreeVisitedView? Visited)
{
    public int Columns => Nodes.Count == 0 ? 0 : Nodes.Max(n => n.Column) + 1;
    public int Levels => Nodes.Count == 0 ? 0 : Nodes.Max(n => n.Depth) + 1;
}

/// <summary>A tree's visited list or set: its variable, whether it is ordered (a list), and how many of its members marked no node drawn.</summary>
public sealed record TreeVisitedView(string Variable, bool Ordered, int Unmatched);

/// <summary>
/// A frame of the call stack: one call, with its arguments as they were passed. <see cref="Pushed"/>: the call began since the step before.
/// <see cref="Returning"/>: the value it is returning on this step. <see cref="Received"/>: a value a call it made handed back to it since
/// the step before (shown for that one step).
/// </summary>
public sealed record CallFrameView(int Id, string Function, IReadOnlyList<CallArg> Args, string? Returning = null, bool Pushed = false,
    ReturnedValue? Received = null)
{
    /// <summary>How the call reads in code: <c>fact(n=3)</c>.</summary>
    public string Label => $"{Function}({string.Join(", ", Args.Select(a => $"{a.Name}={a.Repr}"))})";
}

/// <summary>
/// The call stack drawn as frames, outermost first, so the last is the one running. <see cref="Gone"/> are the calls that returned (or
/// were left by an exception) since the step before, shown for one step so the learner sees them go. <see cref="More"/> counts the outer
/// calls cut off. <see cref="ToModule"/> is a value handed back since the step before to the module (or to a call not drawn).
/// </summary>
public sealed record CallStackView(IReadOnlyList<CallFrameView> Frames, int More, IReadOnlyList<CallFrameView> Gone, ReturnedValue? ToModule)
{
    public CallFrameView? Running => Frames.Count > 0 ? Frames[^1] : null;
}

/// <summary>
/// A grid cell. <see cref="Changed"/>: its value differs from the step before; <see cref="Added"/>: there was no cell here the step
/// before (a row or a cell appended). <see cref="Shaded"/>: the highlight marks it.
/// </summary>
public sealed record GridCellView(int Row, int Col, string Text, bool Changed = false, bool Added = false, bool Shaded = false);

/// <summary>A grid row: its cells, and how many more it has beyond the ones recorded.</summary>
public sealed record GridRowView(int Row, IReadOnlyList<GridCellView> Cells, int More);

/// <summary>
/// A grid pointer: a pair of int variables (<see cref="RowName"/>, <see cref="ColName"/>) or one variable holding a (row, col) pair (both
/// names it). <see cref="Defined"/>: its variables exist at this step (every pointer of the visual is listed, so each keeps its place);
/// <see cref="Row"/> and <see cref="Col"/> are null when it holds None or a variable isn't an int. <see cref="OnGrid"/>: it is on a cell
/// drawn. <see cref="Moved"/>: its place differs from the step before.
/// </summary>
public sealed record GridPointerView(string Name, string RowName, string ColName, int? Row, int? Col, bool Defined, bool OnGrid, bool Moved);

/// <summary>A grid's highlight: the variable, and how many of its members marked no cell drawn.</summary>
public sealed record GridHighlightView(string Variable, int Unmatched);

/// <summary>
/// A list of lists or strings drawn as a 2-D table, row 0 on top. <see cref="More"/> counts the rows beyond the ones recorded.
/// <see cref="RowLabels"/> and <see cref="ColLabels"/>, when drawn, have one entry per row and per column (null: no label there),
/// lined up by the visual's label offset. <see cref="Highlight"/> is null when no highlight is drawn.
/// </summary>
public sealed record GridView(string Variable, IReadOnlyList<GridRowView> Rows, int More, IReadOnlyList<GridPointerView> Pointers,
    IReadOnlyList<string?>? RowLabels = null, IReadOnlyList<string?>? ColLabels = null, GridHighlightView? Highlight = null)
{
    public int Columns => Rows.Count == 0 ? 0 : Rows.Max(r => r.Cells.Count);
    public GridCellView? At(int row, int col) => row >= 0 && row < Rows.Count && col >= 0 && col < Rows[row].Cells.Count ? Rows[row].Cells[col] : null;
}

/// <summary>
/// A graph node, placed: <see cref="Slot"/> is its place round the circle, the order the run first met it in, so it stands in the same
/// place at every step. <see cref="Added"/>: it wasn't drawn the step before. <see cref="Visited"/>: the visited variable holds it;
/// <see cref="Queued"/> is its place in the frontier (1: the first), null when it isn't there. <see cref="JustVisited"/> and
/// <see cref="JustQueued"/>: it was marked since the step before.
/// </summary>
public sealed record GraphNodeView(int Id, string Label, int Slot, bool Added = false, bool Visited = false, int? Queued = null,
    bool JustVisited = false, bool JustQueued = false);

/// <summary>
/// A graph edge. An undirected graph draws an edge listed both ways once. <see cref="Both"/>: a directed graph has the edge the other way
/// too, so the two bow apart. <see cref="Added"/>: it wasn't drawn the step before. <see cref="Active"/>: it is the edge being explored,
/// from the node the edge's first variable holds to the second's.
/// </summary>
public sealed record GraphEdgeView(int From, int To, string? Weight, bool Both = false, bool Added = false, bool Active = false);

/// <summary>A graph's visited or frontier variable, and how many of its members are no node drawn.</summary>
public sealed record GraphMarksView(string Variable, int Unmatched);

/// <summary>
/// A graph drawn as its nodes round a circle of <see cref="Slots"/> places (every node the run meets has one) and the edges between them.
/// <see cref="More"/> and <see cref="MoreEdges"/> count the nodes and edges beyond the ones recorded. <see cref="Edge"/> names the variables
/// holding the ends of the edge being explored (null: none drawn). <see cref="Visited"/> and <see cref="Frontier"/> are null when not drawn.
/// </summary>
public sealed record GraphView(string Variable, bool Directed, IReadOnlyList<GraphNodeView> Nodes, int Slots, IReadOnlyList<GraphEdgeView> Edges,
    int More, int MoreEdges, IReadOnlyList<NodePointer> Pointers, EdgeNames? Edge = null, GraphMarksView? Visited = null, GraphMarksView? Frontier = null)
{
    public GraphNodeView? Node(int id) => Nodes.FirstOrDefault(n => n.Id == id);
}

/// <summary>
/// A heap cell, drawn in the array and as a node of the tree. <see cref="Id"/> is the same while its value stays in the heap, so a cell
/// moved by a swap slides to its new place in both. <see cref="Moved"/>: it was at another index the step before; <see cref="Changed"/>:
/// its index held another value that didn't move anywhere else (<c>heap[0] = x</c>); <see cref="Added"/>: a value new to the heap (a push).
/// </summary>
public sealed record HeapCellView(int Id, string Text, bool Changed = false, bool Added = false, bool Moved = false);

/// <summary>A heap cell whose value left since the step before (a pop), shown for one step at the <see cref="Index"/> it left from.</summary>
public sealed record HeapGoneCell(int Id, string Text, int Index);

/// <summary>
/// A heap: its cells by index, drawn as an array and as the implicit tree (the children of cell i at 2i+1 and 2i+2). <see cref="Pointers"/>
/// are int variables, on the cell and on the node they index. <see cref="Max"/> labels it a max-heap rather than a min-heap.
/// </summary>
public sealed record HeapView(string Variable, IReadOnlyList<HeapCellView> Cells, int More, IReadOnlyList<PointerView> Pointers, bool Max,
    IReadOnlyList<HeapGoneCell>? Gone = null)
{
    public IReadOnlyList<HeapGoneCell> Gone { get; init; } = Gone ?? [];
}

/// <summary>One step of the player: what to highlight and draw. <see cref="Line"/> is null on the final state.</summary>
public sealed record TraceState(
    int Index, int? Line, string Function, bool IsFinal,
    IReadOnlyList<LocalView> Locals, IReadOnlyList<ArrayView> Arrays, IReadOnlyList<TableView> Tables,
    IReadOnlyList<LinkedListView>? Lists = null, CallStackView? Calls = null, IReadOnlyList<TreeView>? Trees = null,
    IReadOnlyList<GridView>? Grids = null, IReadOnlyList<GraphView>? Graphs = null, IReadOnlyList<HeapView>? Heaps = null)
{
    public IReadOnlyList<HeapView> Heaps { get; init; } = Heaps ?? [];
    public IReadOnlyList<LinkedListView> Lists { get; init; } = Lists ?? [];
    public IReadOnlyList<TreeView> Trees { get; init; } = Trees ?? [];
    public IReadOnlyList<GridView> Grids { get; init; } = Grids ?? [];
    public IReadOnlyList<GraphView> Graphs { get; init; } = Graphs ?? [];
}

/// <summary>
/// The Trace Model: turns the raw frames of a run into the states the Trace Player renders, with what changed since the
/// previous step marked so the player can animate it. Changes are only marked against the immediately preceding step and
/// only when it ran in the same function; entering or leaving a call marks nothing. The call stack is the exception: it spans
/// the functions, so it is compared with the step before whichever function that ran in.
/// </summary>
public static class TraceModel
{
    public static IReadOnlyList<TraceState> Build(TraceResult result, IReadOnlyList<Visual> visuals)
    {
        var arrayVisuals = visuals.Where(v => Visual.Sequences.Contains(v.As)).ToList();
        var tableVisuals = visuals.Where(v => v.As is Visual.Dict or Visual.Set).ToList();
        var listVisuals = visuals.Where(v => v.As == Visual.LinkedList).ToList();
        var treeVisuals = visuals.Where(v => v.As == Visual.Tree).ToList();
        var gridVisuals = visuals.Where(v => v.As == Visual.Grid).ToList();
        var graphVisuals = visuals.Where(v => v.As == Visual.Graph).ToList();
        var heapVisuals = visuals.Where(v => v.As == Visual.Heap).ToList();
        var slots = graphVisuals.ToDictionary(v => v.Variable, v => Slots(result, v.Variable));
        var drawsCalls = visuals.Any(v => v.As == Visual.CallStack);
        var states = new List<TraceState>(result.Frames.Count);
        TraceFrame? previousFrame = null;
        TraceState? previous = null;
        foreach (var (frame, index) in result.Frames.Select((f, i) => (f, i)))
        {
            var comparable = previous is not null && previousFrame!.Function == frame.Function ? previous : null;
            var state = new TraceState(index, frame.Line, frame.Function, frame.Line is null,
                Locals(frame, comparable), Arrays(frame, arrayVisuals, comparable), Tables(frame, tableVisuals, comparable),
                Lists(frame, listVisuals, comparable), drawsCalls ? Calls(frame, previous) : null, Trees(frame, treeVisuals, comparable),
                Grids(frame, gridVisuals, comparable), Graphs(frame, graphVisuals, slots, comparable), Heaps(frame, heapVisuals, comparable));
            states.Add(state);
            previousFrame = frame;
            previous = state;
        }
        return states;
    }

    private static IReadOnlyList<LocalView> Locals(TraceFrame frame, TraceState? previous) =>
        frame.Locals.Select(l =>
        {
            var before = previous?.Locals.FirstOrDefault(p => p.Name == l.Name);
            var changed = previous is not null && (before is null || before.Value != l.Repr);
            return new LocalView(l.Name, l.Type, l.Repr, changed);
        }).ToList();

    private static IReadOnlyList<ArrayView> Arrays(TraceFrame frame, List<Visual> visuals, TraceState? previous)
    {
        var views = new List<ArrayView>();
        foreach (var visual in visuals)
        {
            if (!frame.Tracked.TryGetValue(visual.Variable, out var tracked) || !tracked.Draws(visual.As)) continue;
            var before = previous?.Arrays.FirstOrDefault(a => a.Variable == visual.Variable);

            var (cells, gone) = visual.As == Visual.Array
                ? (tracked.Cells
                    .Select((text, i) => new CellView(text, before is not null && (i >= before.Cells.Count || before.Cells[i].Text != text), Id: i))
                    .ToList(), [])
                : EndCells(tracked, before, visual.As);
            var pointers = visual.Pointers.Select(name =>
            {
                var index = PointerIndex(frame, name, cells.Count);
                var was = before?.Pointers.FirstOrDefault(p => p.Name == name);
                return new PointerView(name, index, before is not null && was?.Index != index);
            }).ToList();
            views.Add(new ArrayView(visual.Variable, cells, tracked.More, pointers, visual.As, gone));
        }
        return views;
    }

    /// <summary>
    /// A stack's or queue's cells, lined up with the step before's so that what came in or left at either end is told apart from what
    /// stayed: the longest run of cells the two steps share stayed (keeping their ids), what is before it or after it left or came in at
    /// that end. A cell that left and one that came in at the same place pair up as that cell changing (<c>s[-1] = x</c>).
    /// </summary>
    private static (List<CellView> Cells, List<GoneCell> Gone) EndCells(TrackedArray tracked, ArrayView? before, string kind)
    {
        var now = tracked.Cells;
        if (before is null)
            return (now.Select((text, i) => new CellView(text, false, Id: i)).ToList(), []);

        var was = before.Cells;
        var (from, to, length) = Align(was.Select(c => c.Text).ToList(), now, kind);
        var nextId = was.Select(c => c.Id).Concat(before.Gone.Select(g => g.Id)).DefaultIfEmpty(-1).Max() + 1;
        // A cell past the recorded ones moves into view as the cut-off point shifts, which is neither a push nor a pop.
        var backCut = before.More > 0 || tracked.More > 0;

        var cells = new CellView[now.Count];
        var gone = new List<GoneCell>();
        for (var i = 0; i < length; i++)
            cells[to + i] = new CellView(now[to + i], false, Id: was[from + i].Id);

        // The front: was[..from] left, now[..to] came in; the ones next to the run pair up.
        var frontPairs = Math.Min(from, to);
        for (var j = 1; j <= frontPairs; j++)
        {
            var old = was[from - j];
            cells[to - j] = new CellView(now[to - j], old.Text != now[to - j], Id: old.Id);
        }
        for (var i = 0; i < from - frontPairs; i++) gone.Add(new GoneCell(was[i].Id, was[i].Text, AtFront: true));
        for (var i = 0; i < to - frontPairs; i++) cells[i] = new CellView(now[i], false, Added: true, Id: nextId++);

        // The back: was[(from + length)..] left, now[(to + length)..] came in.
        var wasBack = was.Count - from - length;
        var nowBack = now.Count - to - length;
        var backPairs = Math.Min(wasBack, nowBack);
        for (var j = 0; j < backPairs; j++)
        {
            var old = was[from + length + j];
            var text = now[to + length + j];
            cells[to + length + j] = new CellView(text, old.Text != text, Id: old.Id);
        }
        for (var i = from + length + backPairs; i < was.Count; i++)
            if (!backCut) gone.Add(new GoneCell(was[i].Id, was[i].Text, AtFront: false));
        for (var i = to + length + backPairs; i < now.Count; i++)
            cells[i] = new CellView(now[i], false, Added: !backCut, Id: nextId++);

        return (cells.ToList(), gone);
    }

    /// <summary>
    /// Where the longest run of cells the two steps share starts in each (<c>From</c> in the step before, <c>To</c> in this one) and how
    /// long it is. Among runs as long, the one that has the cells come and go where this kind of sequence does is taken: a stack's at the
    /// back (its top), a queue's in at the back and out at the front. So <c>[1, 1] → [1]</c> is a pop off a stack but a dequeue off a queue.
    /// </summary>
    internal static (int From, int To, int Length) Align(IReadOnlyList<string> was, IReadOnlyList<string> now, string kind)
    {
        var queue = kind == Visual.Queue;
        // Nothing shared: everything left and came in at the usual ends.
        var best = (From: queue ? was.Count : 0, To: 0, Length: 0);
        var bestScore = 0;
        for (var from = 0; from < was.Count; from++)
            for (var to = 0; to < now.Count; to++)
            {
                var length = 0;
                while (from + length < was.Count && to + length < now.Count && was[from + length] == now[to + length]) length++;
                if (length == 0) continue;
                var leftAtFront = from > 0;
                var leftAtBack = from + length < was.Count;
                var cameInAtFront = to > 0;
                var unusual = (cameInAtFront ? 1 : 0) + ((queue ? leftAtBack : leftAtFront) ? 1 : 0);
                var score = length * 4 - unusual;
                if (score > bestScore) (best, bestScore) = ((from, to, length), score);
            }
        return best;
    }

    private static IReadOnlyList<HeapView> Heaps(TraceFrame frame, List<Visual> visuals, TraceState? previous)
    {
        var views = new List<HeapView>();
        foreach (var visual in visuals)
        {
            if (frame.Heaps is null || !frame.Heaps.TryGetValue(visual.Variable, out var tracked)) continue;
            var before = previous?.Heaps.FirstOrDefault(h => h.Variable == visual.Variable);
            var (cells, gone) = HeapCells(tracked, before);
            var pointers = visual.Pointers.Select(name =>
            {
                var index = PointerIndex(frame, name, cells.Count);
                var was = before?.Pointers.FirstOrDefault(p => p.Name == name);
                return new PointerView(name, index, before is not null && was?.Index != index);
            }).ToList();
            views.Add(new HeapView(visual.Variable, cells, tracked.More, pointers, visual.Max, gone));
        }
        return views;
    }

    /// <summary>
    /// A heap's cells, matched with the step before's so that a value a swap or a sift moved keeps its id: a cell whose index holds
    /// the same value as before stayed; a value at a new index takes the unmatched cell of the same value nearest it, which moved; a
    /// value still unmatched takes the unmatched cell at its own index, which changed; anything left is a value that came in (a push)
    /// or one that left (a pop). Past five levels, cells move in and out of view as the cut-off shifts, which is neither.
    /// </summary>
    private static (List<HeapCellView> Cells, List<HeapGoneCell> Gone) HeapCells(TrackedHeap tracked, HeapView? before)
    {
        var now = tracked.Cells;
        if (before is null)
            return (now.Select((text, i) => new HeapCellView(i, text)).ToList(), []);

        var was = before.Cells;
        var nextId = was.Select(c => c.Id).Concat(before.Gone.Select(g => g.Id)).DefaultIfEmpty(-1).Max() + 1;
        var cut = before.More > 0 || tracked.More > 0;
        var cells = new HeapCellView?[now.Count];
        var taken = new bool[was.Count];

        for (var i = 0; i < Math.Min(now.Count, was.Count); i++)
            if (was[i].Text == now[i])
            {
                cells[i] = new HeapCellView(was[i].Id, now[i]);
                taken[i] = true;
            }
        for (var i = 0; i < now.Count; i++)
        {
            if (cells[i] is not null) continue;
            var nearest = Enumerable.Range(0, was.Count)
                .Where(j => !taken[j] && was[j].Text == now[i])
                .OrderBy(j => Math.Abs(j - i)).ThenBy(j => j)
                .Select(j => (int?)j).FirstOrDefault();
            if (nearest is { } j2)
            {
                cells[i] = new HeapCellView(was[j2].Id, now[i], Moved: true);
                taken[j2] = true;
            }
        }
        for (var i = 0; i < Math.Min(now.Count, was.Count); i++)
            if (cells[i] is null && !taken[i])
            {
                cells[i] = new HeapCellView(was[i].Id, now[i], Changed: true);
                taken[i] = true;
            }
        for (var i = 0; i < now.Count; i++)
            cells[i] ??= new HeapCellView(nextId++, now[i], Added: !cut);

        var gone = cut ? [] : Enumerable.Range(0, was.Count).Where(j => !taken[j]).Select(j => new HeapGoneCell(was[j].Id, was[j].Text, j)).ToList();
        return (cells.Select(c => c!).ToList(), gone);
    }

    private static int? PointerIndex(TraceFrame frame, string name, int cellCount)
    {
        var local = frame.Locals.FirstOrDefault(l => l.Name == name);
        if (local?.IntegerValue() is not { } value) return null;
        return value >= 0 && value < cellCount ? value : null;
    }

    /// <summary>What tells a row apart from the others: its id, or its label in a trace recorded without ids.</summary>
    private static string Identity(TableRow row) => row.Id is { } id ? $"#{id}" : $"key:{row.Key}";

    private static IReadOnlyList<TableView> Tables(TraceFrame frame, List<Visual> visuals, TraceState? previous)
    {
        var views = new List<TableView>();
        foreach (var visual in visuals)
        {
            if (frame.Tables is null || !frame.Tables.TryGetValue(visual.Variable, out var tracked) || tracked.Kind != visual.As) continue;
            var before = previous?.Tables.FirstOrDefault(t => t.Variable == visual.Variable);
            // What was really in the table before, not the ghosts of what had already gone.
            var wasThere = before?.Rows.Where(r => r.Change != RowChange.Removed).ToList();

            // The recorder says which variables hold a key; this does not guess from reprs, which `1` and `1.0` would fool.
            var lookups = new List<(string Name, int? Hit)>();
            if (tracked.Hits is not null)
                foreach (var name in visual.Pointers.Where(name => frame.Locals.Any(l => l.Name == name)))
                    lookups.Add((name, tracked.Hits.TryGetValue(name, out var hit) ? hit : null));

            var rows = new List<RowView>();
            foreach (var row in tracked.Rows)
            {
                var identity = Identity(row);
                var was = wasThere?.FirstOrDefault(r => r.Id == identity);
                var change = wasThere is null ? RowChange.None
                    : was is null ? RowChange.Added
                    : was.Value != row.Value ? RowChange.Updated
                    : RowChange.None;
                rows.Add(new RowView(identity, row.Key, row.Value, change,
                    lookups.Where(l => row.Id is not null && l.Hit == row.Id).Select(l => l.Name).ToList()));
            }
            foreach (var gone in wasThere?.Where(w => tracked.Rows.All(r => Identity(r) != w.Id)) ?? [])
                rows.Add(new RowView(gone.Id, gone.Key, gone.Value, RowChange.Removed, []));

            // A variable that holds no key at all is a miss; one that holds a key cut off from the rows is not, for it may well be there.
            var misses = lookups.Where(l => l.Hit is null).Select(l => l.Name).ToList();
            views.Add(new TableView(visual.Variable, tracked.Kind, rows, tracked.More, misses));
        }
        return views;
    }

    /// <summary>
    /// The call stack, compared with the step before: a call is new if it began after every call drawn then (ids grow with each call), and
    /// one drawn then is gone if it is no longer on the stack and wasn't merely cut off at the bottom.
    /// </summary>
    internal static CallStackView? Calls(TraceFrame frame, TraceState? previous)
    {
        if (frame.Calls is not { } calls) return null;
        var before = previous?.Calls;
        var newestBefore = before?.Frames.Select(f => f.Id).DefaultIfEmpty(-1).Max();
        var returned = calls.Returned;
        var frames = calls.Frames.Select(c => new CallFrameView(c.Id, c.Function, c.Args, c.Returning,
            Pushed: newestBefore is not null && c.Id > newestBefore,
            Received: returned is { To: { } to } && to == c.Id ? returned : null)).ToList();

        var ids = frames.Select(f => f.Id).ToHashSet();
        var bottom = frames.Count > 0 ? frames[0].Id : int.MaxValue;
        var gone = before?.Frames.Where(f => !ids.Contains(f.Id) && (calls.More == 0 || f.Id > bottom))
            .Select(f => f with { Pushed = false, Received = null }).ToList() ?? [];
        var toModule = returned is not null && (returned.To is null || !ids.Contains(returned.To.Value)) ? returned : null;
        return new CallStackView(frames, calls.More, gone, toModule);
    }

    private static IReadOnlyList<LinkedListView> Lists(TraceFrame frame, List<Visual> visuals, TraceState? previous)
    {
        var views = new List<LinkedListView>();
        foreach (var visual in visuals)
        {
            if (frame.Lists is null || !frame.Lists.TryGetValue(visual.Variable, out var tracked)) continue;
            var before = previous?.Lists.FirstOrDefault(l => l.Variable == visual.Variable);
            var was = before?.Nodes.ToDictionary(n => n.Id);
            NodeView View(ListNode node) =>
                was is null ? new NodeView(node.Id, node.Value, node.Next)
                : was.TryGetValue(node.Id, out var old) ? new NodeView(node.Id, node.Value, node.Next, old.Value != node.Value, old.Next != node.Next)
                : new NodeView(node.Id, node.Value, node.Next, Added: true);

            var pointers = new List<NodePointer>();
            void Point(string name, int? node)
            {
                var moved = before is not null && before.Pointers.FirstOrDefault(p => p.Name == name) is var old && (old is null || old.Node != node);
                pointers.Add(new NodePointer(name, node, moved));
            }
            Point(visual.Variable, tracked.Head);
            foreach (var name in visual.Pointers)
                if (tracked.Pointers.TryGetValue(name, out var node)) Point(name, node);

            views.Add(new LinkedListView(visual.Variable, Chains(tracked, View), tracked.More, pointers));
        }
        return views;
    }

    /// <summary>
    /// The recorded nodes as rows: the first follows the links from the head, each other one from the first node no row has taken yet
    /// (the recorder lists a detached run's first node first). A row stops at None, at a node already in it (a cycle), at a node of an
    /// earlier row, or at a node that wasn't recorded.
    /// </summary>
    internal static IReadOnlyList<ChainView> Chains(TrackedList tracked, Func<ListNode, NodeView> view)
    {
        var byId = new Dictionary<int, ListNode>();
        foreach (var node in tracked.Nodes) byId.TryAdd(node.Id, node);
        var row = new Dictionary<int, int>();   // node id -> the row it is in
        var chains = new List<ChainView>();

        ChainView Follow(int? start)
        {
            var nodes = new List<NodeView>();
            var at = start;
            while (at is { } id && byId.TryGetValue(id, out var node) && !row.ContainsKey(id))
            {
                row[id] = chains.Count;
                nodes.Add(view(node));
                at = node.Next;
            }
            return at switch
            {
                null => new ChainView(nodes, ChainEnd.None),
                { } id when row.TryGetValue(id, out var r) => new ChainView(nodes, r == chains.Count ? ChainEnd.Back : ChainEnd.Join, id),
                _ => new ChainView(nodes, ChainEnd.More),
            };
        }

        chains.Add(Follow(tracked.Head));
        foreach (var node in tracked.Nodes)
            if (!row.ContainsKey(node.Id)) chains.Add(Follow(node.Id));
        return chains;
    }

    private static IReadOnlyList<TreeView> Trees(TraceFrame frame, List<Visual> visuals, TraceState? previous)
    {
        var views = new List<TreeView>();
        foreach (var visual in visuals)
        {
            if (frame.Trees is null || !frame.Trees.TryGetValue(visual.Variable, out var tracked)) continue;
            var before = previous?.Trees.FirstOrDefault(t => t.Variable == visual.Variable);
            var was = before?.Nodes.ToDictionary(n => n.Id);
            var marks = new Dictionary<int, int?>();
            foreach (var mark in tracked.Visited?.Nodes ?? []) marks.TryAdd(mark.Id, mark.Order);

            var nodes = Layout(tracked).Select(node =>
            {
                var visited = marks.TryGetValue(node.Id, out var order);
                node = node with { Visited = visited, Order = order };
                if (was is null) return node;
                if (!was.TryGetValue(node.Id, out var old)) return node with { Added = true, JustVisited = visited };
                return node with { Changed = old.Value != node.Value, JustVisited = visited && (!old.Visited || old.Order != order) };
            }).ToList();

            var pointers = new List<NodePointer>();
            void Point(string name, int? node)
            {
                var moved = before is not null && before.Pointers.FirstOrDefault(p => p.Name == name) is var old && (old is null || old.Node != node);
                pointers.Add(new NodePointer(name, node, moved));
            }
            Point(visual.Variable, tracked.Root);
            foreach (var name in visual.Pointers)
                if (tracked.Pointers.TryGetValue(name, out var node)) Point(name, node);

            var visitedView = tracked.Visited is { } v && visual.Visited is { } list ? new TreeVisitedView(list, v.Ordered, v.Unmatched) : null;
            views.Add(new TreeView(visual.Variable, nodes, tracked.More, pointers, visitedView));
        }
        return views;
    }

    private static IReadOnlyList<GridView> Grids(TraceFrame frame, List<Visual> visuals, TraceState? previous)
    {
        var views = new List<GridView>();
        foreach (var visual in visuals)
        {
            if (frame.Grids is null || !frame.Grids.TryGetValue(visual.Variable, out var tracked)) continue;
            var before = previous?.Grids.FirstOrDefault(g => g.Variable == visual.Variable);
            var shaded = (tracked.Highlight?.Cells ?? []).Where(c => c.Count == 2).Select(c => (c[0], c[1])).ToHashSet();

            var rows = tracked.Rows.Select((cells, r) => new GridRowView(r, cells.Select((text, c) =>
            {
                var cell = new GridCellView(r, c, text, Shaded: shaded.Contains((r, c)));
                if (before is null) return cell;
                return before.At(r, c) is { } old ? cell with { Changed = old.Text != text } : cell with { Added = true };
            }).ToList(), Math.Max(0, (r < tracked.Lengths.Count ? tracked.Lengths[r] : cells.Count) - cells.Count))).ToList();

            var named = visual.Cells.Select(c => (c.Name, c.Row, c.Col)).Concat(visual.Pointers.Select(p => (Name: p, Row: p, Col: p)));
            var pointers = named.Select(p =>
            {
                var defined = tracked.Pointers.TryGetValue(p.Name, out var at);
                int? row = at is [var r, _] ? r : null, col = at is [_, var c] ? c : null;
                var onGrid = row is { } rr && col is { } cc && rr >= 0 && cc >= 0 && rr < rows.Count && cc < rows[rr].Cells.Count;
                var was = before?.Pointers.FirstOrDefault(b => b.Name == p.Name);
                var moved = was is not null && (was.Defined != defined || was.Row != row || was.Col != col);
                return new GridPointerView(p.Name, p.Row, p.Col, row, col, defined, onGrid, moved);
            }).ToList();

            var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Cells.Count);
            // Labels not recorded (their variable holds no string, list or tuple yet) leave the strip blank.
            IReadOnlyList<string?> Align(IReadOnlyList<string>? labels, int count) =>
                Enumerable.Range(0, count).Select(i => i - visual.LabelOffset is var at && at >= 0 && at < (labels?.Count ?? 0) ? labels![at] : null).ToList();

            var highlight = tracked.Highlight is { } h && visual.Highlight is { } variable ? new GridHighlightView(variable, h.Unmatched) : null;
            views.Add(new GridView(visual.Variable, rows, tracked.More, pointers,
                visual.RowLabels is null ? null : Align(tracked.RowLabels, rows.Count),
                visual.ColLabels is null ? null : Align(tracked.ColLabels, columns), highlight));
        }
        return views;
    }

    /// <summary>Each node of the graph the run ever records, in the order first met: its place round the circle, the same at every step.</summary>
    internal static IReadOnlyDictionary<int, int> Slots(TraceResult result, string variable)
    {
        var slots = new Dictionary<int, int>();
        foreach (var frame in result.Frames)
            if (frame.Graphs is not null && frame.Graphs.TryGetValue(variable, out var graph))
                foreach (var node in graph.Nodes)
                    slots.TryAdd(node.Id, slots.Count);
        return slots;
    }

    private static IReadOnlyList<GraphView> Graphs(TraceFrame frame, List<Visual> visuals, Dictionary<string, IReadOnlyDictionary<int, int>> slots,
        TraceState? previous)
    {
        var views = new List<GraphView>();
        foreach (var visual in visuals)
        {
            if (frame.Graphs is null || !frame.Graphs.TryGetValue(visual.Variable, out var tracked)) continue;
            var before = previous?.Graphs.FirstOrDefault(g => g.Variable == visual.Variable);
            var place = slots[visual.Variable];

            // An undirected graph lists an edge from both ends; it is one edge, keyed the same from either.
            (int, int) Key(int from, int to) => visual.Directed || from <= to ? (from, to) : (to, from);
            var keys = tracked.Edges.Select(e => Key(e.From, e.To)).ToHashSet();
            var was = before?.Edges.Select(e => Key(e.From, e.To)).ToHashSet();
            (int, int)? active = visual.Edge is { } edge && tracked.Pointers.GetValueOrDefault(edge.From) is { } a
                && tracked.Pointers.GetValueOrDefault(edge.To) is { } b ? Key(a, b) : null;
            var drawn = new HashSet<(int, int)>();
            var edges = new List<GraphEdgeView>();
            foreach (var e in tracked.Edges)
            {
                var key = Key(e.From, e.To);
                if (!drawn.Add(key)) continue;
                edges.Add(new GraphEdgeView(e.From, e.To, e.Weight,
                    Both: visual.Directed && e.From != e.To && keys.Contains((e.To, e.From)),
                    Added: was is not null && !was.Contains(key), Active: key == active));
            }

            var visited = (tracked.Visited?.Nodes ?? []).ToHashSet();
            var queued = new Dictionary<int, int>();
            foreach (var id in tracked.Frontier?.Nodes ?? []) queued.TryAdd(id, queued.Count + 1);
            var old = before?.Nodes.ToDictionary(n => n.Id);
            var nodes = tracked.Nodes.Select(n =>
            {
                var view = new GraphNodeView(n.Id, n.Label, place[n.Id], Visited: visited.Contains(n.Id), Queued: queued.GetValueOrDefault(n.Id) is > 0 and var q ? q : null);
                if (old is null) return view;
                var prior = old.GetValueOrDefault(n.Id);
                return view with
                {
                    Added = prior is null, JustVisited = view.Visited && prior?.Visited != true, JustQueued = view.Queued is not null && prior?.Queued is null,
                };
            }).ToList();

            var pointers = new List<NodePointer>();
            foreach (var name in visual.Pointers)
                if (tracked.Pointers.TryGetValue(name, out var node))
                {
                    var moved = before is not null && before.Pointers.FirstOrDefault(p => p.Name == name) is var prior && (prior is null || prior.Node != node);
                    pointers.Add(new NodePointer(name, node, moved));
                }

            views.Add(new GraphView(visual.Variable, visual.Directed, nodes, place.Count, edges, tracked.More, tracked.MoreEdges, pointers, visual.Edge,
                tracked.Visited is { } v && visual.Visited is { } vn ? new GraphMarksView(vn, v.Unmatched) : null,
                tracked.Frontier is { } f && visual.Frontier is { } fn ? new GraphMarksView(fn, f.Unmatched) : null));
        }
        return views;
    }

    /// <summary>
    /// Places the recorded nodes: the tree from the root, then one from each node no tree has taken yet (the recorder lists a detached
    /// subtree's root first). A tree takes its nodes level by level, so a node two parents share (or one a link cycles back to) is
    /// drawn under the first parent met, and the other link points to it elsewhere. Within a tree the columns follow an in-order walk;
    /// the trees follow one another left to right.
    /// </summary>
    internal static IReadOnlyList<TreeNodeView> Layout(TrackedTree tracked)
    {
        var byId = new Dictionary<int, TreeNode>();
        foreach (var node in tracked.Nodes) byId.TryAdd(node.Id, node);
        var taken = new Dictionary<int, int>();   // node id -> its depth
        var placed = new List<TreeNodeView>();
        var column = 0;

        TreeLink Take(int? child, int depth, Queue<int> level)
        {
            if (child is not { } id) return TreeLink.None;
            if (!byId.ContainsKey(id)) return TreeLink.More;
            if (taken.ContainsKey(id)) return TreeLink.Elsewhere;
            taken[id] = depth;
            level.Enqueue(id);
            return TreeLink.Child;
        }

        void Grow(int root, int tree)
        {
            var links = new Dictionary<int, (TreeLink Left, TreeLink Right)>();
            var level = new Queue<int>();
            taken[root] = 0;
            level.Enqueue(root);
            while (level.TryDequeue(out var id))
            {
                var node = byId[id];
                var left = Take(node.Left, taken[id] + 1, level);
                links[id] = (left, Take(node.Right, taken[id] + 1, level));
            }

            void InOrder(int id)
            {
                var node = byId[id];
                var (left, right) = links[id];
                if (left == TreeLink.Child) InOrder(node.Left!.Value);
                placed.Add(new TreeNodeView(id, node.Value, tree, column++, taken[id], node.Left, node.Right, left, right));
                if (right == TreeLink.Child) InOrder(node.Right!.Value);
            }
            InOrder(root);
        }

        // Tree 0 is the root's alone, even when the root is None: nodes only a pointer reaches are detached, numbered from 1.
        if (tracked.Root is { } start && byId.ContainsKey(start)) Grow(start, 0);
        var detached = 0;
        foreach (var node in tracked.Nodes)
            if (!taken.ContainsKey(node.Id)) Grow(node.Id, ++detached);
        return placed;
    }
}
