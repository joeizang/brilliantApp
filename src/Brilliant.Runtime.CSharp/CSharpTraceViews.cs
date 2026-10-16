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
    public const int MaxNodes = 30;       // linked-list nodes recorded per frame (the rest are counted in More)
    public const int MaxTreeNodes = 31;   // tree nodes recorded per frame, five full levels
    public const int MaxWalk = 1000;      // nodes walked from a start, the most a cyclic or huge structure costs
    public const int MaxVisited = 100;    // members of a tree's visited list or set looked at
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

    public IReadOnlyDictionary<string, TrackedList>? Lists(IReadOnlyDictionary<string, object?> locals) =>
        Collect(_watch.Where(v => v.As == Visual.LinkedList), v => List(v, locals));

    public IReadOnlyDictionary<string, TrackedTree>? Trees(IReadOnlyDictionary<string, object?> locals) =>
        Collect(_watch.Where(v => v.As == Visual.Tree), v => Tree(v, locals));

    // ---- linked lists and trees ----
    // A node is an instance of a user class that has the link fields; its id is the identity the probe gave the object, the same at every step.

    private IReadOnlyDictionary<int, CapturedObject>? _indexed;
    private IReadOnlyDictionary<string, object?>? _indexedFor;

    // Every tracked object of one snapshot by identity, so a link back to an object captured further up (a cycle) can be followed.
    private IReadOnlyDictionary<int, CapturedObject> Index(IReadOnlyDictionary<string, object?> locals)
    {
        if (ReferenceEquals(_indexedFor, locals)) return _indexed!;
        var index = new Dictionary<int, CapturedObject>();
        void Visit(object? value)
        {
            switch (value)
            {
                case CapturedObject captured:
                    if (captured.Identity >= 0 && !index.TryAdd(captured.Identity, captured)) return;
                    foreach (var field in captured.Fields.Values) Visit(field);
                    break;
                case CapturedSequence sequence:
                    foreach (var item in sequence.Values) Visit(item);
                    break;
                case CapturedTable table:
                    foreach (var row in table.Rows) { Visit(row.Key); Visit(row.Value); }
                    break;
            }
        }
        foreach (var value in locals.Values) Visit(value);
        _indexedFor = locals;
        return _indexed = index;
    }

    private static object? Resolve(object? value, IReadOnlyDictionary<int, CapturedObject> index) =>
        value is CapturedReference reference ? index.GetValueOrDefault(reference.Identity) : value;

    private static bool Has(CapturedObject node, string name) => Member(node, name, out _);

    private static bool Member(CapturedObject node, string name, out object? value)
    {
        // The name as written first; else the one that differs only in case, so the default 'next' and 'val' find C#'s Next and Val.
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            foreach (var (key, held) in node.Fields)
                if (key != "…" && string.Equals(CSharpTraceRecorder.FieldName(key), name, comparison)) { value = held; return true; }
        value = null;
        return false;
    }

    private static bool IsNode(object? value, params string[] links) => value is CapturedObject { Identity: >= 0 } node && links.All(link => Has(node, link));

    // The node a link holds: null for the end, or for something that isn't a node.
    private static CapturedObject? Follow(CapturedObject node, string link, IReadOnlyDictionary<int, CapturedObject> index, string[] links, out bool cut)
    {
        Member(node, link, out var held);
        cut = held is UnavailableCapture { Reason: "limit" };
        return Resolve(held, index) is { } next && IsNode(next, links) ? (CapturedObject)next : null;
    }

    // The value a node shows: null when it has no such attribute.
    private static string? ValueOf(CapturedObject node, string attribute, out object? raw) =>
        Member(node, attribute, out raw) ? CSharpTraceRecorder.Clip(CSharpTraceRecorder.Repr(raw), CSharpTraceRecorder.MaxCellRepr) : null;

    private TrackedList? List(Visual visual, IReadOnlyDictionary<string, object?> locals)
    {
        string[] links = [visual.Next];
        var index = Index(locals);
        if (!locals.TryGetValue(visual.Variable, out var held)) return null;
        var head = held as CapturedObject;
        if (held is not null && !IsNode(head, links)) return null;
        var starts = new List<CapturedObject>();
        if (head is not null) starts.Add(head);
        var pointers = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var name in visual.Pointers)
        {
            if (!locals.TryGetValue(name, out var value)) continue;
            if (value is null) pointers[name] = null;
            else if (IsNode(value, links)) { pointers[name] = ((CapturedObject)value).Identity; starts.Add((CapturedObject)value); }
        }

        var recorded = new List<ListNode>();
        var seen = new HashSet<int>();
        var more = 0;
        foreach (var start in starts)
        {
            var node = start;
            while (node is not null && seen.Add(node.Identity) && seen.Count <= MaxWalk)
            {
                var after = Follow(node, visual.Next, index, links, out var cut);
                if (recorded.Count < MaxNodes) recorded.Add(new(node.Identity, ValueOf(node, visual.Value, out _), after?.Identity));
                else more++;
                if (cut) more++;   // the capture stopped here: at least one more node that can't be counted
                node = after;
            }
        }
        return new(head?.Identity, recorded, more, pointers);
    }

    private TrackedTree? Tree(Visual visual, IReadOnlyDictionary<string, object?> locals)
    {
        string[] links = [visual.Left, visual.Right];
        var index = Index(locals);
        if (!locals.TryGetValue(visual.Variable, out var held)) return null;
        var root = held as CapturedObject;
        if (held is not null && !IsNode(root, links)) return null;
        var starts = new List<CapturedObject>();
        if (root is not null) starts.Add(root);
        var pointers = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var name in visual.Pointers)
        {
            if (!locals.TryGetValue(name, out var value)) continue;
            if (value is null) pointers[name] = null;
            else if (IsNode(value, links)) { pointers[name] = ((CapturedObject)value).Identity; starts.Add((CapturedObject)value); }
        }

        var kept = new List<(CapturedObject Node, bool HasValue, object? Raw, TreeNode Recorded)>();
        var seen = new HashSet<int>();
        var more = 0;
        foreach (var start in starts)
        {
            var level = new Queue<CapturedObject>([start]);
            while (level.Count > 0 && seen.Count < MaxWalk)
            {
                var node = level.Dequeue();
                if (!seen.Add(node.Identity)) continue;
                var left = Follow(node, visual.Left, index, links, out var cutLeft);
                var right = Follow(node, visual.Right, index, links, out var cutRight);
                foreach (var child in new[] { left, right })
                    if (child is not null && !seen.Contains(child.Identity)) level.Enqueue(child);
                if (kept.Count < MaxTreeNodes)
                {
                    var text = ValueOf(node, visual.Value, out var raw);
                    kept.Add((node, text is not null, raw, new(node.Identity, text, left?.Identity, right?.Identity)));
                }
                else more++;
                if (cutLeft) more++;
                if (cutRight) more++;
            }
        }

        TreeVisited? visited = null;
        if (visual.Visited is { Length: > 0 } visitedName && locals.TryGetValue(visitedName, out var marked)) visited = Visited(marked, kept.Select(k => (k.Node, k.HasValue, k.Raw, k.Recorded)).ToArray());
        return new(root?.Identity, kept.Select(k => k.Recorded).ToArray(), more, pointers, visited);
    }

    // Which recorded nodes a visited list or set marks: a member that is a node marks it, and any other marks the nodes whose value equals it
    // (in a list, the first such node no earlier member marked).
    private static TreeVisited? Visited(object? value, (CapturedObject Node, bool HasValue, object? Raw, TreeNode Recorded)[] kept)
    {
        IReadOnlyList<object?> members;
        bool ordered;
        int total;
        switch (value)
        {
            case CapturedSequence { Shape: not CapturedShape.Stack } sequence: ordered = true; members = sequence.Values; total = Length(sequence); break;
            case CapturedTable { IsSet: true } set: ordered = false; members = set.Rows.Select(row => row.Key).ToArray(); total = set.Total; break;
            default: return null;
        }
        var marks = new Dictionary<int, int?>();
        var unmatched = 0;
        var place = 0;
        foreach (var member in members.Take(MaxVisited))
        {
            place++;
            var found = member is CapturedObject { Identity: >= 0 } node ? kept.Where(k => k.Node.Identity == node.Identity).ToArray() : [];
            if (found.Length == 0 && Canonical(member) is { } canonical)
                found = kept.Where(k => k.HasValue && Canonical(k.Raw) == canonical).ToArray();
            var matches = ordered ? found.Where(k => !marks.ContainsKey(k.Recorded.Id)).Take(1) : found;
            var any = false;
            foreach (var match in matches)
            {
                any = true;
                marks.TryAdd(match.Recorded.Id, ordered ? place : null);
            }
            if (!any) unmatched++;
        }
        var nodes = kept.Where(k => marks.ContainsKey(k.Recorded.Id)).Select(k => new VisitedNode(k.Recorded.Id, marks[k.Recorded.Id])).ToArray();
        return new(ordered, nodes, unmatched + Math.Max(0, total - MaxVisited));
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
        UnavailableCapture or CapturedTable or CapturedReference => null,
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
