using System.Diagnostics;
using System.Collections;
using System.Reflection;

namespace Brilliant.Code.CSharp;

/// <summary>A cooperative execution limit. Learner catch clauses must never intercept this exception.</summary>
public sealed class BudgetExceededException(string message) : Exception(message);

/// <summary>How a captured sequence is laid out: an array or a list front to back, a stack bottom (index 0) to top, a queue front to back.</summary>
public enum CapturedShape { Array, List, Stack, Queue }

/// <summary>
/// An array or framework collection frozen at a probe. <see cref="Values"/> holds at most 128 items in <see cref="Shape"/>'s order;
/// <see cref="Total"/> is how many the collection held (-1: all of them). <see cref="Display"/> is its C# name, e.g. <c>List&lt;int&gt;</c>.
/// </summary>
public sealed record CapturedSequence(string Type, IReadOnlyList<object?> Values, string Display = "", CapturedShape Shape = CapturedShape.Array, int Total = -1);

/// <summary>
/// A dictionary or set frozen at a probe. <see cref="Rows"/> holds at most 128 entries (a set's value is null) in the collection's own
/// order; <see cref="Ordered"/> is true when that order is the collection's by design (sorted types), false when it merely happens
/// (hashed types). <see cref="Total"/> is how many entries the collection held.
/// </summary>
public sealed record CapturedTable(string Type, string Display, bool IsSet, bool Ordered, IReadOnlyList<KeyValuePair<object?, object?>> Rows, int Total);
public sealed record CapturedObject(string Type, IReadOnlyDictionary<string, object?> Fields, string Display = "");
public sealed record UnavailableCapture(string Reason);

/// <summary><see cref="Function"/> is the method, local function or lambda the probe sits in (null when the rewriter wasn't told).</summary>
public sealed record ProbeSnapshot(int Line, IReadOnlyDictionary<string, object?> Locals, string? Function = null);

/// <summary>One synchronous run's limits, installed on the dedicated execution thread before invoking learner code.</summary>
public sealed class ProbeBudget(int maxSteps = 100_000, TimeSpan? timeout = null, int maxDepth = 128, int maxSnapshots = 1_000)
{
    public const int DefaultStackSize = 4 * 1024 * 1024;
    [ThreadStatic] private static ProbeBudget? _current;
    public static ProbeBudget? Current { get => _current; set => _current = value; }
    private readonly int _maxSteps = maxSteps > 0 ? maxSteps : throw new ArgumentOutOfRangeException(nameof(maxSteps));
    private readonly int _maxDepth = maxDepth > 0 && maxDepth <= 128 ? maxDepth : throw new ArgumentOutOfRangeException(nameof(maxDepth), "Call depth must be between 1 and 128.");
    private readonly TimeSpan _timeout = (timeout ?? TimeSpan.FromSeconds(2)) > TimeSpan.Zero ? timeout ?? TimeSpan.FromSeconds(2) : throw new ArgumentOutOfRangeException(nameof(timeout));
    private readonly int _maxSnapshots = maxSnapshots >= 0 ? maxSnapshots : throw new ArgumentOutOfRangeException(nameof(maxSnapshots));
    private readonly List<ProbeSnapshot> _snapshots = [];
    private long? _started;
    private volatile string? _exhausted;
    public int Depth { get; private set; }
    public int Steps { get; private set; }
    public IReadOnlyList<ProbeSnapshot> Snapshots => _snapshots;

    /// <summary>True once a probe fired after the snapshot limit was reached: the recording stops short of the run.</summary>
    public bool SnapshotsDropped { get; private set; }

    /// <summary>Request cooperative stopping from a watchdog; the next probe throws.</summary>
    public void Cancel() => Interlocked.CompareExchange(ref _exhausted, "The code exceeded its time budget.", null);

    /// <summary>Start the next call of a repeated measurement with fresh step, depth and time counts. Does not allocate.</summary>
    public void ResetCounts()
    {
        Steps = 0;
        Depth = 0;
        _started = null;
    }

    public void Tick()
    {
        _started ??= Stopwatch.GetTimestamp();
        if (_exhausted is null && ++Steps > _maxSteps) _exhausted = $"The code exceeded its step budget ({_maxSteps} probes).";
        if (_exhausted is null && Stopwatch.GetElapsedTime(_started.Value) > _timeout) _exhausted = "The code exceeded its time budget.";
        if (_exhausted is not null) throw new BudgetExceededException(_exhausted);
    }

    public void Enter()
    {
        Tick();
        if (Depth >= _maxDepth)
        {
            _exhausted = $"The code exceeded its call-depth limit ({_maxDepth}).";
            throw new BudgetExceededException(_exhausted);
        }
        Depth++;
    }

    public void Exit()
    {
        if (Depth > 0) Depth--;
        // CLR discards exceptions thrown by exception filters. Never allow a swallowed
        // probe failure to turn a subsequent catch/return into a successful execution.
        Tick();
    }

    public void Snapshot(int line, string[] names, object?[] values) => Snapshot(line, null, names, values);

    public void Snapshot(int line, string? function, string[] names, object?[] values)
    {
        Tick();
        if (_snapshots.Count >= _maxSnapshots) { SnapshotsDropped = true; return; }
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(names.Length, values.Length); i++)
            locals[names[i]] = Freeze(values[i], new HashSet<object>(ReferenceEqualityComparer.Instance), 0, new CaptureLimit());
        _snapshots.Add(new(line, locals, function));
    }

    private sealed class CaptureLimit { public int Remaining = 1_000; }

    // Read instance fields instead of properties: capturing a local must not invoke learner getters.
    private static object? Freeze(object? value, HashSet<object> path, int depth, CaptureLimit limit)
    {
        if (value is null) return null;
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid) return value;
        if (depth >= 16 || --limit.Remaining < 0) return new UnavailableCapture("limit");
        if (!path.Add(value)) return new UnavailableCapture("cycle");
        try
        {
            if (value is Array { Rank: 2 } grid)
            {
                // A rectangular array is a grid of rows, so it draws like a list of lists.
                var rowType = TypeNames.Of(type.GetElementType()!) + "[]";
                var rows = new object?[Math.Min(grid.GetLength(0), 128)];
                for (var r = 0; r < rows.Length; r++)
                {
                    var cells = new object?[Math.Min(grid.GetLength(1), 128)];
                    for (var c = 0; c < cells.Length; c++)
                        cells[c] = Freeze(grid.GetValue(r + grid.GetLowerBound(0), c + grid.GetLowerBound(1)), path, depth + 2, limit);
                    rows[r] = new CapturedSequence(rowType, cells, rowType, CapturedShape.Array, grid.GetLength(1));
                }
                return new CapturedSequence(type.FullName ?? type.Name, rows, TypeNames.Of(type), CapturedShape.Array, grid.GetLength(0));
            }
            if (value is Array array)
                return new CapturedSequence(type.FullName ?? type.Name,
                    array.Cast<object?>().Take(128).Select(item => Freeze(item, path, depth + 1, limit)).ToArray(),
                    TypeNames.Of(type), CapturedShape.Array, array.Length);
            // Only framework collections; arbitrary IList/IEnumerable implementations can execute user code.
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (IList)value;
                return new CapturedSequence(type.FullName ?? type.Name,
                    list.Cast<object?>().Take(128).Select(item => Freeze(item, path, depth + 1, limit)).ToArray(),
                    TypeNames.Of(type), CapturedShape.List, list.Count);
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Stack<>))
            {
                // Enumerates top first; the drawing runs bottom (index 0) to top, so a deep stack keeps its bottom 128 items.
                var items = ((IEnumerable)value).Cast<object?>().Reverse().Take(128).Select(item => Freeze(item, path, depth + 1, limit)).ToArray();
                return new CapturedSequence(type.FullName ?? type.Name, items, TypeNames.Of(type), CapturedShape.Stack, Count(value));
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Queue<>))
                return new CapturedSequence(type.FullName ?? type.Name,
                    ((IEnumerable)value).Cast<object?>().Take(128).Select(item => Freeze(item, path, depth + 1, limit)).ToArray(),
                    TypeNames.Of(type), CapturedShape.Queue, Count(value));
            if (type.IsGenericType && type.GetGenericTypeDefinition() is var definition)
            {
                if (definition == typeof(Dictionary<,>) || definition == typeof(SortedDictionary<,>) || definition == typeof(SortedList<,>))
                {
                    var rows = new List<KeyValuePair<object?, object?>>();
                    foreach (DictionaryEntry entry in (IDictionary)value)
                    {
                        if (rows.Count == 128) break;
                        rows.Add(new(Freeze(entry.Key, path, depth + 1, limit), Freeze(entry.Value, path, depth + 1, limit)));
                    }
                    return new CapturedTable(type.FullName ?? type.Name, TypeNames.Of(type), IsSet: false, Ordered: definition != typeof(Dictionary<,>), rows, Count(value));
                }
                if (definition == typeof(HashSet<>) || definition == typeof(SortedSet<>))
                    return new CapturedTable(type.FullName ?? type.Name, TypeNames.Of(type), IsSet: true, Ordered: definition == typeof(SortedSet<>),
                        ((IEnumerable)value).Cast<object?>().Take(128).Select(item => new KeyValuePair<object?, object?>(Freeze(item, path, depth + 1, limit), null)).ToArray(),
                        Count(value));
            }
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var current = type; current is not null; current = current.BaseType)
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Take(128))
                {
                    if (limit.Remaining-- <= 0) { fields["…"] = new UnavailableCapture("limit"); break; }
                    fields[current.Name + "." + field.Name] = Freeze(field.GetValue(value), path, depth + 1, limit);
                }
            return new CapturedObject(type.FullName ?? type.Name, fields, TypeNames.Of(type));
        }
        finally { path.Remove(value); }
    }

    private static int Count(object collection) => (int)collection.GetType().GetProperty("Count")!.GetValue(collection)!;
}
