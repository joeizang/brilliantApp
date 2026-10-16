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

/// <summary>
/// An object frozen at a probe. <see cref="Identity"/> is its number for the whole run when the budget tracks objects (see
/// <see cref="ProbeBudget"/>): the same object has the same number at every probe, whichever variable holds it, and the numbers are
/// handed out in the order objects are first captured, so they never come from an address or a hash code. -1: not tracked.
/// </summary>
public sealed record CapturedObject(string Type, IReadOnlyDictionary<string, object?> Fields, string Display = "", int Identity = -1);

/// <summary>An object reached again on the way to itself (a cycle): the <see cref="CapturedObject.Identity"/> of the object it is, which is captured further up.</summary>
public sealed record CapturedReference(int Identity);

public sealed record UnavailableCapture(string Reason);

/// <summary>A call under way when a probe fired: <see cref="Id"/> numbers the calls of the run in the order they were made, and <see cref="Args"/> are its parameters as they were at the call.</summary>
public sealed record CapturedCall(int Id, string Function, IReadOnlyList<KeyValuePair<string, object?>> Args);

/// <summary>What a call returned: the call's <see cref="CallId"/>, and the value (<see cref="Void"/>: the method returns nothing, so <see cref="Value"/> is null).</summary>
public sealed record CapturedReturn(int CallId, string Function, object? Value, bool Void);

/// <summary>
/// <see cref="Function"/> is the method, local function or lambda the probe sits in (null when the rewriter wasn't told). <see cref="Calls"/>
/// are the calls under way, outermost first, when the rewriter tracks calls (else null); <see cref="Return"/> is set when the probe is a call
/// returning (the state it returns in) rather than a statement about to run.
/// </summary>
public sealed record ProbeSnapshot(int Line, IReadOnlyDictionary<string, object?> Locals, string? Function = null,
    IReadOnlyList<CapturedCall>? Calls = null, CapturedReturn? Return = null);

/// <summary>
/// One synchronous run's limits, installed on the dedicated execution thread before invoking learner code. With <c>trackObjects</c> the
/// snapshots also give every instance of a user class a run-wide identity (so a recording can tell that two snapshots hold the same linked-list
/// or tree node) and capture deeper, since a list of nodes is as deep as it is long.
/// </summary>
public sealed class ProbeBudget(int maxSteps = 100_000, TimeSpan? timeout = null, int maxDepth = 128, int maxSnapshots = 1_000, bool trackObjects = false)
{
    public const int DefaultStackSize = 4 * 1024 * 1024;
    [ThreadStatic] private static ProbeBudget? _current;
    public static ProbeBudget? Current { get => _current; set => _current = value; }
    private readonly int _maxSteps = maxSteps > 0 ? maxSteps : throw new ArgumentOutOfRangeException(nameof(maxSteps));
    private readonly int _maxDepth = maxDepth > 0 && maxDepth <= 128 ? maxDepth : throw new ArgumentOutOfRangeException(nameof(maxDepth), "Call depth must be between 1 and 128.");
    private readonly TimeSpan _timeout = (timeout ?? TimeSpan.FromSeconds(2)) > TimeSpan.Zero ? timeout ?? TimeSpan.FromSeconds(2) : throw new ArgumentOutOfRangeException(nameof(timeout));
    private readonly int _maxSnapshots = maxSnapshots >= 0 ? maxSnapshots : throw new ArgumentOutOfRangeException(nameof(maxSnapshots));
    private readonly List<ProbeSnapshot> _snapshots = [];
    private readonly Dictionary<object, int> _identities = new(ReferenceEqualityComparer.Instance);
    private readonly List<CapturedCall> _calls = [];
    private int _callsTaken;
    private int CaptureDepth => trackObjects ? 256 : 16;
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

    /// <summary>A tracked call begins (see <see cref="ProbeRewriter"/>): counts as <see cref="Enter()"/>, and puts the call on the stack the snapshots carry.</summary>
    public void Enter(string function, string[] names, object?[] values)
    {
        Enter();
        var args = new List<KeyValuePair<string, object?>>();
        for (var i = 0; i < Math.Min(names.Length, values.Length); i++)
            args.Add(new(names[i], _snapshots.Count >= _maxSnapshots ? new UnavailableCapture("limit") : Freeze(values[i], new HashSet<object>(ReferenceEqualityComparer.Instance), 0, new CaptureLimit { Remaining = 200 })));
        _calls.Add(new(_callsTaken++, function, args));
    }

    /// <summary>A tracked call ends, however it ends.</summary>
    public void ExitCall()
    {
        if (_calls.Count > 0) _calls.RemoveAt(_calls.Count - 1);
        Exit();
    }

    /// <summary>A tracked call returns <paramref name="value"/>: records the state it returns in, and hands the value on.</summary>
    public T Return<T>(T value, int line, string? function, string[] names, object?[] values)
    {
        Record(line, function, names, values, value, isVoid: false);
        return value;
    }

    /// <summary>A tracked call that returns nothing ends (a <c>return;</c>, or running off its end).</summary>
    public void ReturnVoid(int line, string? function, string[] names, object?[] values) => Record(line, function, names, values, null, isVoid: true);

    private void Record(int line, string? function, string[] names, object?[] values, object? value, bool isVoid)
    {
        Tick();
        if (_snapshots.Count >= _maxSnapshots) { SnapshotsDropped = true; return; }
        var locals = FreezeLocals(names, values);
        var returned = new CapturedReturn(_calls.Count > 0 ? _calls[^1].Id : -1, function ?? "", isVoid ? null : Freeze(value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0, new CaptureLimit { Remaining = 200 }), isVoid);
        _snapshots.Add(new(line, locals, function, _calls.ToArray(), returned));
    }

    private Dictionary<string, object?> FreezeLocals(string[] names, object?[] values)
    {
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(names.Length, values.Length); i++)
            locals[names[i]] = Freeze(values[i], new HashSet<object>(ReferenceEqualityComparer.Instance), 0, new CaptureLimit { Remaining = trackObjects ? 4_000 : 1_000 });
        return locals;
    }

    public void Snapshot(int line, string[] names, object?[] values) => Snapshot(line, null, names, values);

    public void Snapshot(int line, string? function, string[] names, object?[] values)
    {
        Tick();
        if (_snapshots.Count >= _maxSnapshots) { SnapshotsDropped = true; return; }
        _snapshots.Add(new(line, FreezeLocals(names, values), function, _calls.Count > 0 ? _calls.ToArray() : null));
    }

    private sealed class CaptureLimit { public int Remaining; }

    // Only instances of the learner's own classes are numbered: framework types and value types have no identity that matters here.
    private bool Tracks(Type type) => trackObjects && type.IsClass && type.FullName is { } name && !name.StartsWith("System.", StringComparison.Ordinal) && !name.StartsWith("Microsoft.", StringComparison.Ordinal);

    private int IdentityOf(object value)
    {
        if (!_identities.TryGetValue(value, out var id)) _identities[value] = id = _identities.Count;
        return id;
    }

    // Read instance fields instead of properties: capturing a local must not invoke learner getters.
    private object? Freeze(object? value, HashSet<object> path, int depth, CaptureLimit limit)
    {
        if (value is null) return null;
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid) return value;
        if (depth >= CaptureDepth || --limit.Remaining < 0) return new UnavailableCapture("limit");
        if (!path.Add(value)) return Tracks(type) ? new CapturedReference(IdentityOf(value)) : new UnavailableCapture("cycle");
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
            return new CapturedObject(type.FullName ?? type.Name, fields, TypeNames.Of(type), Tracks(type) ? IdentityOf(value) : -1);
        }
        finally { path.Remove(value); }
    }

    private static int Count(object collection) => (int)collection.GetType().GetProperty("Count")!.GetValue(collection)!;
}
