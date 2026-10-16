using System.Globalization;
using Brilliant.Code.CSharp;
using Brilliant.Core.Code;
using Brilliant.Core.Content;

namespace Brilliant.Runtime.CSharp;

/// <summary>
/// The views of a recorded C# frame that read the frozen variables of one probe: dictionary and set tables, heaps and grids. Each follows
/// the Python tracer's rules (same limits, same shapes) so the trace player draws them the same way.
/// </summary>
internal sealed class CSharpTraceViews(IReadOnlyList<Visual> visuals)
{
    public const int MaxRows = 20;        // dictionary entries / set members recorded per frame (the rest are counted in More)
    public const int MaxHeapCells = 31;   // heap cells recorded per frame, five full levels of its tree
    public const int MaxGrid = 15;        // rows of a grid recorded per frame, and cells of each row
    public const int MaxMarked = 100;     // members of a grid's highlight looked at (the rest mark nothing)

    // One watch per variable, the first given, as the Python tracer's watch list.
    private readonly Visual[] _watch = visuals.GroupBy(v => v.Variable, StringComparer.Ordinal).Where(g => g.Key.Length > 0).Select(g => g.First()).ToArray();
    private readonly Dictionary<string, int> _identities = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, TrackedTable>? Tables(IReadOnlyDictionary<string, object?> locals, IReadOnlyList<KeyValuePair<string, object?>> shown) =>
        Collect(_watch.Where(v => v.As is Visual.Dict or Visual.Set), v => Table(v.Variable, locals, shown));

    public IReadOnlyDictionary<string, TrackedHeap>? Heaps(IReadOnlyDictionary<string, object?> locals) =>
        Collect(_watch.Where(v => v.As == Visual.Heap), v => Heap(v.Variable, locals));

    public IReadOnlyDictionary<string, TrackedGrid>? Grids(IReadOnlyDictionary<string, object?> locals) =>
        Collect(_watch.Where(v => v.As == Visual.Grid), v => Grid(v, locals));

    private static IReadOnlyDictionary<string, T>? Collect<T>(IEnumerable<Visual> watched, Func<Visual, T?> record) where T : class
    {
        Dictionary<string, T>? found = null;
        foreach (var visual in watched)
            if (record(visual) is { } recorded) (found ??= new(StringComparer.Ordinal))[visual.Variable] = recorded;
        return found;
    }

    // ---- dictionaries and sets ----

    private TrackedTable? Table(string variable, IReadOnlyDictionary<string, object?> locals, IReadOnlyList<KeyValuePair<string, object?>> shown)
    {
        if (!locals.TryGetValue(variable, out var held) || held is not CapturedTable table) return null;
        var members = Members(table);
        var rows = members.Take(MaxRows)
            .Select(row => new TableRow(CSharpTraceRecorder.Clip(CSharpTraceRecorder.Repr(row.Key), CSharpTraceRecorder.MaxCellRepr),
                table.IsSet ? null : CSharpTraceRecorder.Clip(CSharpTraceRecorder.Repr(row.Value), CSharpTraceRecorder.MaxCellRepr), Identity(row.Key)))
            .ToArray();

        // The variables that hold a key (or member): the id of the row they find, which is no recorded row when that row was cut off.
        var keys = members.Select(row => Canonical(row.Key)).ToHashSet(StringComparer.Ordinal);
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, value) in shown)
            if (name != variable && value is not (UnavailableCapture or CapturedTable) && Canonical(value) is { } canonical && keys.Contains(canonical))
                hits[name] = Identity(value);
        return new(table.IsSet ? Visual.Set : Visual.Dict, table.Display, rows, Math.Max(0, table.Total - rows.Length), hits);
    }

    // A set's rows are sorted so they don't jump around as members are added; a dictionary's (and a sorted collection's) stay in their own order.
    // Only a set captured whole is sorted: a bigger one shows the members it met first.
    private static IReadOnlyList<KeyValuePair<object?, object?>> Members(CapturedTable table) =>
        table.IsSet && !table.Ordered && table.Rows.Count == table.Total ? table.Rows.OrderBy(row => row.Key, MemberOrder.Instance).ToArray() : table.Rows;

    private sealed class MemberOrder : IComparer<object?>
    {
        public static readonly MemberOrder Instance = new();

        public int Compare(object? a, object? b) => (a, b) switch
        {
            (string x, string y) => string.CompareOrdinal(x, y),
            (not null and not CapturedSequence and not CapturedObject and not CapturedTable and not UnavailableCapture and IComparable x, not null) when a.GetType() == b.GetType() => x.CompareTo(b),
            _ => string.CompareOrdinal(CSharpTraceRecorder.Repr(a), CSharpTraceRecorder.Repr(b)),
        };
    }

    // The number a key has for the whole run, the same every time it is met, in the order the keys are first met.
    private int Identity(object? key)
    {
        var canonical = Canonical(key) ?? CSharpTraceRecorder.Repr(key);
        if (!_identities.TryGetValue(canonical, out var id)) _identities[canonical] = id = _identities.Count;
        return id;
    }

    // Keys are told apart by value, and an int finds the long it equals.
    private static string? Canonical(object? value) => value switch
    {
        null => "null",
        string text => "s:" + text,
        char c => "c:" + c,
        bool b => "b:" + b,
        sbyte or byte or short or ushort or int or uint or long or ulong => "n:" + Convert.ToString(value, CultureInfo.InvariantCulture),
        CapturedSequence sequence => "q:" + sequence.Display + CSharpTraceRecorder.Repr(sequence),
        CapturedObject captured => "o:" + captured.Display + CSharpTraceRecorder.Repr(captured),
        UnavailableCapture or CapturedTable => null,
        _ => value.GetType().Name + ":" + CSharpTraceRecorder.Repr(value),
    };

    // ---- heaps ----

    private static TrackedHeap? Heap(string variable, IReadOnlyDictionary<string, object?> locals)
    {
        if (!locals.TryGetValue(variable, out var held) || held is not CapturedSequence { Shape: CapturedShape.Array or CapturedShape.List } sequence) return null;
        var cells = sequence.Values.Take(MaxHeapCells).Select(cell => CSharpTraceRecorder.Clip(CSharpTraceRecorder.Repr(cell), CSharpTraceRecorder.MaxCellRepr)).ToArray();
        return new(sequence.Display, cells, Math.Max(0, Length(sequence) - cells.Length));
    }

    // ---- grids ----

    private static TrackedGrid? Grid(Visual visual, IReadOnlyDictionary<string, object?> locals)
    {
        if (!locals.TryGetValue(visual.Variable, out var held) || held is not CapturedSequence { Shape: CapturedShape.Array or CapturedShape.List } sequence) return null;
        var recordedRows = sequence.Values.Take(MaxGrid).ToArray();
        if (!recordedRows.All(row => row is CapturedSequence or string)) return null;

        var rows = new List<IReadOnlyList<string>>();
        var lengths = new List<int>();
        foreach (var row in recordedRows)
        {
            if (row is string text)
            {
                lengths.Add(text.Length);
                rows.Add(text.Take(MaxGrid).Select(c => c.ToString()).ToArray());
            }
            else
            {
                var cells = (CapturedSequence)row!;
                lengths.Add(Length(cells));
                rows.Add(cells.Values.Take(MaxGrid).Select(cell => CSharpTraceRecorder.Clip(CSharpTraceRecorder.Repr(cell), CSharpTraceRecorder.MaxCellRepr)).ToArray());
            }
        }

        var pointers = new Dictionary<string, IReadOnlyList<int>?>(StringComparer.Ordinal);
        foreach (var pair in visual.Cells)
        {
            if (!locals.TryGetValue(pair.Row, out var row) || !locals.TryGetValue(pair.Col, out var col)) continue;
            pointers[pair.Name] = Int(row) is { } r && Int(col) is { } c ? [r, c] : null;
        }
        foreach (var name in visual.Pointers)
        {
            if (!locals.TryGetValue(name, out var pointer)) continue;
            if (pointer is null) pointers[name] = null;
            else if (Pair(pointer) is { } pair) pointers[name] = pair;
        }

        IReadOnlyList<string>? Labels(string? name) => name is { Length: > 0 } && locals.TryGetValue(name, out var labelled) ? LabelsOf(labelled) : null;
        GridHighlight? Highlight(string? name) => name is { Length: > 0 } && locals.TryGetValue(name, out var marked) ? HighlightOf(marked, rows.Select(r => r.Count).ToArray()) : null;
        return new(rows, lengths, Math.Max(0, Length(sequence) - MaxGrid), pointers, Labels(visual.RowLabels), Labels(visual.ColLabels), Highlight(visual.Highlight));
    }

    private static IReadOnlyList<string>? LabelsOf(object? value) => value switch
    {
        string text => text.Take(MaxGrid).Select(c => c.ToString()).ToArray(),
        CapturedSequence { Shape: CapturedShape.Array or CapturedShape.List } sequence => sequence.Values.Take(MaxGrid)
            .Select(item => item is string label ? CSharpTraceRecorder.Clip(label, CSharpTraceRecorder.MaxCellRepr) : CSharpTraceRecorder.Clip(CSharpTraceRecorder.Repr(item), CSharpTraceRecorder.MaxCellRepr)).ToArray(),
        _ => null,
    };

    // The recorded cells a list or set of (row, col) pairs, or a grid of true/false flags, marks.
    private static GridHighlight? HighlightOf(object? value, int[] widths)
    {
        bool Inside(int row, int col) => row >= 0 && row < widths.Length && col >= 0 && col < widths[row];
        IReadOnlyList<object?> members;
        int total;
        switch (value)
        {
            case CapturedSequence { Shape: CapturedShape.Array or CapturedShape.List } sequence:
                members = sequence.Values;
                total = Length(sequence);
                var rows = members.Take(MaxGrid).ToArray();
                if (rows.Length > 0 && rows.All(row => row is CapturedSequence) && rows.Cast<CapturedSequence>().All(row => row.Values.Take(MaxGrid).All(flag => flag is bool)))
                {
                    var flagged = new List<IReadOnlyList<int>>();
                    for (var r = 0; r < rows.Length; r++)
                    {
                        var flags = ((CapturedSequence)rows[r]!).Values.Take(MaxGrid).ToArray();
                        for (var c = 0; c < flags.Length; c++)
                            if ((bool)flags[c]! && Inside(r, c)) flagged.Add([r, c]);
                    }
                    return new(flagged, 0);
                }
                break;
            case CapturedTable { IsSet: true } set:
                members = set.Rows.Select(row => row.Key).ToArray();
                total = set.Total;
                break;
            default:
                return null;
        }
        var cells = new SortedSet<(int Row, int Col)>();
        var unmatched = 0;
        foreach (var member in members.Take(MaxMarked))
            if (Pair(member) is [var row, var col] && Inside(row, col)) cells.Add((row, col));
            else unmatched++;
        return new(cells.Select(cell => (IReadOnlyList<int>)[cell.Row, cell.Col]).ToArray(), unmatched + Math.Max(0, total - MaxMarked));
    }

    // [row, col] when the value is a pair of ints: a (int, int) tuple, or an array or list of two.
    private static int[]? Pair(object? value)
    {
        switch (value)
        {
            case CapturedSequence { Shape: CapturedShape.Array or CapturedShape.List, Values: [var a, var b] } when Int(a) is { } x && Int(b) is { } y:
                return [x, y];
            case CapturedObject captured when captured.Type.StartsWith("System.ValueTuple`2", StringComparison.Ordinal):
                var first = captured.Fields.FirstOrDefault(f => f.Key.EndsWith(".Item1", StringComparison.Ordinal)).Value;
                var second = captured.Fields.FirstOrDefault(f => f.Key.EndsWith(".Item2", StringComparison.Ordinal)).Value;
                return Int(first) is { } row && Int(second) is { } col ? [row, col] : null;
            default:
                return null;
        }
    }

    private static int? Int(object? value) => value is null or bool or char ? null : CSharpTraceRecorder.Integer(value);

    private static int Length(CapturedSequence sequence) => sequence.Total >= 0 ? sequence.Total : sequence.Values.Count;
}
