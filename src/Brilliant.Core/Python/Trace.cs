using System.Text.Json;
using System.Text.Json.Serialization;
using Brilliant.Core.Content;

namespace Brilliant.Core.Python;

/// <summary>
/// How a trace ended. <see cref="Error"/>: the code never compiled or raised (the frames up to that point are kept).
/// <see cref="Truncated"/>: it ran past the recorder's step limit and was stopped.
/// </summary>
public enum TraceStatus { Ok, Error, Truncated }

/// <summary>A local variable at one step. <see cref="Repr"/> is the Python repr, clipped.</summary>
public sealed record TraceLocal(string Name, string Type, string Repr);

/// <summary>A watched list, tuple or deque as recorded at one step: the first cells' reprs (front to back), and how many more there were.</summary>
public sealed record TrackedArray(string Type, IReadOnlyList<string> Cells, int More);

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

/// <summary>
/// The state just before <see cref="Line"/> ran (<see cref="Event"/> "line"), or the state the module finished in
/// (<see cref="Event"/> "return", <see cref="Line"/> null).
/// </summary>
public sealed record TraceFrame(
    string Event,
    int? Line,
    string Function,
    IReadOnlyList<TraceLocal> Locals,
    IReadOnlyDictionary<string, TrackedArray> Tracked,
    IReadOnlyDictionary<string, TrackedTable>? Tables = null);

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

    public static TraceResult Parse(string json) =>
        JsonSerializer.Deserialize<TraceResult>(json, Options)
        ?? throw new InvalidOperationException("The Python runtime returned an empty trace.");

    /// <summary>The variables the tracer should record (sequences, dicts and sets): <c>["nums","counts"]</c>.</summary>
    public static string SerializeWatch(IEnumerable<Visual> visuals) =>
        JsonSerializer.Serialize(visuals.Where(v => Visual.Kinds.Contains(v.As)).Select(v => v.Variable).Distinct());
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

/// <summary>One step of the player: what to highlight and draw. <see cref="Line"/> is null on the final state.</summary>
public sealed record TraceState(
    int Index, int? Line, string Function, bool IsFinal,
    IReadOnlyList<LocalView> Locals, IReadOnlyList<ArrayView> Arrays, IReadOnlyList<TableView> Tables);

/// <summary>
/// The Trace Model: turns the raw frames of a run into the states the Trace Player renders, with what changed since the
/// previous step marked so the player can animate it. Changes are only marked against the immediately preceding step and
/// only when it ran in the same function; entering or leaving a call marks nothing.
/// </summary>
public static class TraceModel
{
    public static IReadOnlyList<TraceState> Build(TraceResult result, IReadOnlyList<Visual> visuals)
    {
        var arrayVisuals = visuals.Where(v => Visual.Sequences.Contains(v.As)).ToList();
        var tableVisuals = visuals.Where(v => v.As is Visual.Dict or Visual.Set).ToList();
        var states = new List<TraceState>(result.Frames.Count);
        TraceFrame? previousFrame = null;
        TraceState? previous = null;
        foreach (var (frame, index) in result.Frames.Select((f, i) => (f, i)))
        {
            var comparable = previous is not null && previousFrame!.Function == frame.Function ? previous : null;
            var state = new TraceState(index, frame.Line, frame.Function, frame.Line is null,
                Locals(frame, comparable), Arrays(frame, arrayVisuals, comparable), Tables(frame, tableVisuals, comparable));
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
            if (!frame.Tracked.TryGetValue(visual.Variable, out var tracked) || !visual.Draws(tracked.Type)) continue;
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

    private static int? PointerIndex(TraceFrame frame, string name, int cellCount)
    {
        var local = frame.Locals.FirstOrDefault(l => l.Name == name);
        if (local is not { Type: "int" } || !int.TryParse(local.Repr, out var value)) return null;
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
}
