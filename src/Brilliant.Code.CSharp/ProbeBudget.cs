using System.Diagnostics;
using System.Collections;
using System.Reflection;

namespace Brilliant.Code.CSharp;

/// <summary>A cooperative execution limit. Learner catch clauses must never intercept this exception.</summary>
public sealed class BudgetExceededException(string message) : Exception(message);

public sealed record CapturedSequence(string Type, IReadOnlyList<object?> Values);
public sealed record CapturedObject(string Type, IReadOnlyDictionary<string, object?> Fields);
public sealed record UnavailableCapture(string Reason);

public sealed record ProbeSnapshot(int Line, IReadOnlyDictionary<string, object?> Locals);

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
    private string? _exhausted;
    public int Depth { get; private set; }
    public int Steps { get; private set; }
    public IReadOnlyList<ProbeSnapshot> Snapshots => _snapshots;

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

    public void Snapshot(int line, string[] names, object?[] values)
    {
        Tick();
        if (_snapshots.Count >= _maxSnapshots) return;
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(names.Length, values.Length); i++)
            locals[names[i]] = Freeze(values[i], new HashSet<object>(ReferenceEqualityComparer.Instance), 0, new CaptureLimit());
        _snapshots.Add(new(line, locals));
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
            if (value is Array array)
                return new CapturedSequence(type.FullName ?? type.Name,
                    array.Cast<object?>().Take(128).Select(item => Freeze(item, path, depth + 1, limit)).ToArray());
            // Only framework List<T>; arbitrary IList implementations can execute user code.
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return new CapturedSequence(type.FullName ?? type.Name,
                    ((IList)value).Cast<object?>().Take(128).Select(item => Freeze(item, path, depth + 1, limit)).ToArray());
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var current = type; current is not null; current = current.BaseType)
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Take(128))
                {
                    if (limit.Remaining-- <= 0) { fields["…"] = new UnavailableCapture("limit"); break; }
                    fields[current.Name + "." + field.Name] = Freeze(field.GetValue(value), path, depth + 1, limit);
                }
            return new CapturedObject(type.FullName ?? type.Name, fields);
        }
        finally { path.Remove(value); }
    }
}
