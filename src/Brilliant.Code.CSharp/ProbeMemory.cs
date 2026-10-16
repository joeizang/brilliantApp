using System.Collections;
using System.Reflection;

namespace Brilliant.Code.CSharp;

/// <summary>
/// A value-typed local or parameter, marked as one when the rewriter is asked for the stack and the heap: a local's value reaches the probe boxed,
/// so only the marker says the variable holds the value itself and not a reference to a box.
/// </summary>
public sealed class InlineValue(object? value)
{
    public object? Value { get; } = value;
}

/// <summary>
/// One place that holds a value: a local, a field or an array element. A scalar (<see cref="Value"/>: a primitive, string, enum, decimal, date)
/// or a struct (<see cref="Fields"/>, copied with it) sits inline; a reference to a heap object has <see cref="Ref"/> set
/// (<see cref="CapturedHeapObject.Key"/>); a null reference has neither.
/// </summary>
public sealed record CapturedSlot(string Name, string Type, object? Value, int Ref = -1, IReadOnlyList<CapturedSlot>? Fields = null);

/// <summary>An object on the heap: <see cref="Kind"/> is "class", "box" (a value type boxed) or "array" (an array or a framework collection); <see cref="More"/> counts the slots cut off.</summary>
public sealed record CapturedHeapObject(int Key, string Type, string Kind, IReadOnlyList<CapturedSlot> Slots, int More);

/// <summary>A call's locals: <see cref="CallId"/> is the call's <see cref="CapturedCall.Id"/> (-1: the code outside any tracked method).</summary>
public sealed record CapturedFrame(int CallId, string Function, IReadOnlyList<CapturedSlot> Slots);

/// <summary>The stack (outermost frame first) and the heap objects reachable from it, at one probe.</summary>
public sealed record CapturedMemory(IReadOnlyList<CapturedFrame> Frames, IReadOnlyList<CapturedHeapObject> Objects);

/// <summary>
/// Follows the stack of calls and, at a probe, describes every frame's locals and every heap object they reach. A frame's locals are the
/// ones its last probe saw, so a caller waiting on a call still shows what it held when it called. Objects are numbered (<see cref="CapturedHeapObject.Key"/>)
/// in the order they are first seen, never from an address, so the same object keeps its number for the whole run.
/// </summary>
internal sealed class MemoryCapturer(string? mainFunction)
{
    internal const int MaxObjects = 24, MaxSlots = 16, MaxLocals = 24, MaxDepth = 4;

    private readonly Dictionary<object, int> _keys = new(ReferenceEqualityComparer.Instance);
    private readonly List<Frame> _stack = [];
    private Frame? _main;

    private sealed class Frame(int callId, string function, string[] names, object?[] values)
    {
        public int CallId { get; } = callId;
        public string Function { get; } = function;
        public string[] Names { get; set; } = names;
        public object?[] Values { get; set; } = values;
    }

    public void Enter(int callId, string function, string[] names, object?[] values) => _stack.Add(new(callId, function, names, values));

    public void Exit()
    {
        if (_stack.Count > 0) _stack.RemoveAt(_stack.Count - 1);
    }

    /// <summary>A probe fired in <paramref name="function"/>: its locals are the running frame's (a probe in anything not tracked, like a constructor or lambda, is not a frame of its own).</summary>
    public void Probe(string? function, string[] names, object?[] values)
    {
        if (_stack.Count > 0)
        {
            if (_stack[^1].Function == function) { _stack[^1].Names = names; _stack[^1].Values = values; }
        }
        else if (function == mainFunction)
        {
            if (_main is null) _main = new(-1, function ?? "", names, values);
            else { _main.Names = names; _main.Values = values; }
        }
    }

    public CapturedMemory Capture()
    {
        var context = new Context();
        var frames = new List<CapturedFrame>();
        foreach (var frame in _main is null ? _stack : _stack.Prepend(_main))
        {
            var slots = new List<CapturedSlot>();
            foreach (var index in Enumerable.Range(0, Math.Min(frame.Names.Length, frame.Values.Length)).OrderBy(i => frame.Names[i], StringComparer.Ordinal).Take(MaxLocals))
            {
                var raw = frame.Values[index];
                slots.Add(raw is InlineValue inline ? Describe(frame.Names[index], inline.Value, true, null, 0, context) : Describe(frame.Names[index], raw, false, null, 0, context));
            }
            frames.Add(new(frame.CallId, frame.Function, slots));
        }
        // Breadth first from the frames, so the objects come in a fixed order.
        var objects = new List<CapturedHeapObject>();
        while (context.Queue.Count > 0)
        {
            var (key, value) = context.Queue.Dequeue();
            objects.Add(Object(key, value, context));
        }
        return new(frames, objects);
    }

    private sealed class Context
    {
        public readonly HashSet<int> Seen = [];
        public readonly Queue<(int Key, object Value)> Queue = new();
    }

    private static bool Scalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
        || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid);

    // A slot for a value that is "inline" when the place holding it has a value type (a boxed value in a reference-typed place is a box on the heap).
    private CapturedSlot Describe(string name, object? value, bool inline, Type? declared, int depth, Context context)
    {
        if (value is null) return new(name, declared is null ? "object" : TypeNames.Of(declared), null);
        var type = value.GetType();
        if (type == typeof(string) || (Scalar(type) && inline)) return new(name, TypeNames.Of(type), value);
        if (type.IsValueType && inline)
        {
            if (depth >= MaxDepth) return new(name, TypeNames.Of(type), new UnavailableCapture("limit"));
            var fields = new List<CapturedSlot>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Take(MaxSlots))
                fields.Add(Describe(Unique(FieldName(field.Name), field.DeclaringType!, used), field.GetValue(value), field.FieldType.IsValueType, field.FieldType, depth + 1, context));
            return new(name, TypeNames.Of(type), null, Fields: fields);
        }
        return Reference(name, type, value, context);
    }

    private CapturedSlot Reference(string name, Type type, object value, Context context)
    {
        if (!_keys.TryGetValue(value, out var key)) _keys[value] = key = _keys.Count;
        if (!context.Seen.Contains(key))
        {
            if (context.Seen.Count >= MaxObjects) return new(name, TypeNames.Of(type), new UnavailableCapture("limit"));
            context.Seen.Add(key);
            context.Queue.Enqueue((key, value));
        }
        return new(name, TypeNames.Of(type), null, key);
    }

    private CapturedHeapObject Object(int key, object value, Context context)
    {
        var type = value.GetType();
        var display = TypeNames.Of(type);
        if (type.IsValueType) return new(key, display, "box", [Describe("value", value, true, null, 0, context)], 0);
        if (value is Array { Rank: 1 } array)
            return Elements(key, display, array, array.Length, type.GetElementType()!, context);
        if (value is Array grid) return new(key, display, "array", [], grid.Length);
        if (type.IsGenericType && type.GetGenericTypeDefinition() is var definition
            && (definition == typeof(List<>) || definition == typeof(Stack<>) || definition == typeof(Queue<>) || definition == typeof(HashSet<>) || definition == typeof(SortedSet<>)))
            return Elements(key, display, (IEnumerable)value, (int)type.GetProperty("Count")!.GetValue(value)!, type.GetGenericArguments()[0], context);
        // The rest of the framework is shown by its name: its internals are not what a learner's code is about.
        if (type.FullName is { } full && (full.StartsWith("System.", StringComparison.Ordinal) || full.StartsWith("Microsoft.", StringComparison.Ordinal)))
            return new(key, display, "class", [], 0);
        var slots = new List<CapturedSlot>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var more = 0;
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (slots.Count >= MaxSlots) { more++; continue; }
                slots.Add(Describe(Unique(FieldName(field.Name), current, used), field.GetValue(value), field.FieldType.IsValueType, field.FieldType, 0, context));
            }
        return new(key, display, "class", slots, more);
    }

    private CapturedHeapObject Elements(int key, string display, IEnumerable items, int total, Type element, Context context)
    {
        var slots = new List<CapturedSlot>();
        foreach (var item in items)
        {
            if (slots.Count >= MaxSlots) break;
            slots.Add(Describe($"[{slots.Count}]", item, element.IsValueType, element, 0, context));
        }
        return new(key, display, "array", slots, Math.Max(0, total - slots.Count));
    }

    // A backing field is named for its property: <Name>k__BackingField.
    private static string FieldName(string name) => name.StartsWith('<') && name.IndexOf('>') is var end and > 0 ? name[1..end] : name;

    // A field hidden by one of the same name in a derived class is told apart by the class it belongs to.
    private static string Unique(string name, Type owner, HashSet<string> used)
    {
        var unique = used.Add(name) ? name : owner.Name + "." + name;
        used.Add(unique);
        return unique;
    }
}
