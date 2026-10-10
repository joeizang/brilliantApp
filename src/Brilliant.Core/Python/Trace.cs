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

/// <summary>A watched list as recorded at one step: the first cells' reprs, and how many more there were.</summary>
public sealed record TrackedArray(string Type, IReadOnlyList<string> Cells, int More);

/// <summary>
/// The state just before <see cref="Line"/> ran (<see cref="Event"/> "line"), or the state the module finished in
/// (<see cref="Event"/> "return", <see cref="Line"/> null).
/// </summary>
public sealed record TraceFrame(
    string Event,
    int? Line,
    string Function,
    IReadOnlyList<TraceLocal> Locals,
    IReadOnlyDictionary<string, TrackedArray> Tracked);

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

    /// <summary>The variables the tracer should record as arrays: <c>["nums","grid"]</c>.</summary>
    public static string SerializeWatch(IEnumerable<Visual> visuals) =>
        JsonSerializer.Serialize(visuals.Where(v => v.As == Visual.Array).Select(v => v.Variable).Distinct());
}

public sealed record LocalView(string Name, string Type, string Value, bool Changed);

public sealed record CellView(string Text, bool Changed);

/// <summary>A named marker. <see cref="Index"/> is the cell it sits on, null when its value is not a cell of the array (missing, not an int, negative or past the end).</summary>
public sealed record PointerView(string Name, int? Index, bool Moved);

/// <summary><paramref name="More"/> counts the cells beyond the ones recorded.</summary>
public sealed record ArrayView(string Variable, IReadOnlyList<CellView> Cells, int More, IReadOnlyList<PointerView> Pointers);

/// <summary>One step of the player: what to highlight and draw. <see cref="Line"/> is null on the final state.</summary>
public sealed record TraceState(int Index, int? Line, string Function, bool IsFinal, IReadOnlyList<LocalView> Locals, IReadOnlyList<ArrayView> Arrays);

/// <summary>
/// The Trace Model: turns the raw frames of a run into the states the Trace Player renders, with what changed since the
/// previous step marked so the player can animate it. Changes are only marked against the immediately preceding step and
/// only when it ran in the same function; entering or leaving a call marks nothing.
/// </summary>
public static class TraceModel
{
    public static IReadOnlyList<TraceState> Build(TraceResult result, IReadOnlyList<Visual> visuals)
    {
        var arrayVisuals = visuals.Where(v => v.As == Visual.Array).ToList();
        var states = new List<TraceState>(result.Frames.Count);
        TraceFrame? previousFrame = null;
        TraceState? previous = null;
        foreach (var (frame, index) in result.Frames.Select((f, i) => (f, i)))
        {
            var comparable = previous is not null && previousFrame!.Function == frame.Function ? previous : null;
            var state = new TraceState(index, frame.Line, frame.Function, frame.Line is null,
                Locals(frame, comparable), Arrays(frame, arrayVisuals, comparable));
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
            if (!frame.Tracked.TryGetValue(visual.Variable, out var tracked)) continue;
            var before = previous?.Arrays.FirstOrDefault(a => a.Variable == visual.Variable);

            var cells = tracked.Cells
                .Select((text, i) => new CellView(text, before is not null && (i >= before.Cells.Count || before.Cells[i].Text != text)))
                .ToList();
            var pointers = visual.Pointers.Select(name =>
            {
                var index = PointerIndex(frame, name, cells.Count);
                var was = before?.Pointers.FirstOrDefault(p => p.Name == name);
                return new PointerView(name, index, before is not null && was?.Index != index);
            }).ToList();
            views.Add(new ArrayView(visual.Variable, cells, tracked.More, pointers));
        }
        return views;
    }

    private static int? PointerIndex(TraceFrame frame, string name, int cellCount)
    {
        var local = frame.Locals.FirstOrDefault(l => l.Name == name);
        if (local is not { Type: "int" } || !int.TryParse(local.Repr, out var value)) return null;
        return value >= 0 && value < cellCount ? value : null;
    }
}
