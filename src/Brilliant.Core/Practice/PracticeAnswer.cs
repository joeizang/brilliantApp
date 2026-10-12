using System.Text.Json;
using Brilliant.Core.Progress;
using Brilliant.Core.Review;

namespace Brilliant.Core.Practice;

/// <summary>Where a practice answer was given: a free practice visit, or a timed interview run.</summary>
public enum PracticeMode { Practice, Interview }

/// <summary>The two halves of a practice problem, each scheduled as its own review item.</summary>
public enum PracticePart
{
    /// <summary>Naming the pattern. Schedules <c>pick.&lt;step&gt;</c>, a <see cref="Content.ReviewKind.Pattern"/> item.</summary>
    Pick,

    /// <summary>Solving the problem. Schedules <c>resolve.&lt;step&gt;</c>, a <see cref="Content.ReviewKind.Resolve"/> item.</summary>
    Solve,
}

/// <summary>
/// The data of a <see cref="ProgressEventTypes.PracticeAnswered"/> event: one part of one problem, answered in a practice visit or an
/// interview run.
/// </summary>
/// <param name="Step">The problem step.</param>
/// <param name="Picked">For a pick, the pattern ID the learner chose; null when they never chose. Always null for a solve.</param>
/// <param name="Rating">The FSRS rating inferred when it was recorded, stored so the schedule replays identically if inference is tuned later.</param>
/// <param name="Run">The practice visit or interview run. Only the first answer to a part within one run is scheduled.</param>
public sealed record PracticeAnswer(string Step, PracticePart Part, bool Correct, string? Picked, int HintsUsed, TimeSpan Elapsed, Rating Rating,
    PracticeMode Mode, string? Run)
{
    public const string PickPrefix = "pick.";
    public const string ResolvePrefix = "resolve.";

    /// <summary>The review item this answer schedules.</summary>
    public string ItemId => ItemIdOf(Step, Part);

    /// <summary><c>pick.&lt;step&gt;</c> or <c>resolve.&lt;step&gt;</c>.</summary>
    public static string ItemIdOf(string stepId, PracticePart part) => (part == PracticePart.Pick ? PickPrefix : ResolvePrefix) + stepId;

    internal string ToJson() => JsonSerializer.Serialize(new
    {
        step = Step,
        part = Part == PracticePart.Pick ? "pick" : "solve",
        correct = Correct,
        picked = Picked,
        hintsUsed = HintsUsed,
        elapsedMs = (int)Elapsed.TotalMilliseconds,
        rating = (int)Rating,
        mode = Mode == PracticeMode.Interview ? "interview" : "practice",
        run = Run,
    });

    /// <summary>
    /// The answer a PracticeAnswered event holds, or null for any other event and for malformed data (no step, an unknown part, a rating
    /// outside 1–4, or a step that disagrees with the event's own <see cref="ProgressEvent.StepId"/>). Optional fields default.
    /// </summary>
    public static PracticeAnswer? From(ProgressEvent e)
    {
        if (e.Type != ProgressEventTypes.PracticeAnswered || e.Data is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (String(root, "step") is not { Length: > 0 } step || step != e.StepId) return null;
            PracticePart? part = String(root, "part") switch { "pick" => PracticePart.Pick, "solve" => PracticePart.Solve, _ => null };
            if (part is null) return null;
            if (!root.TryGetProperty("correct", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            if (Int(root, "rating") is not { } grade || grade is < 1 or > 4) return null;
            return new PracticeAnswer(step, part.Value, ok.GetBoolean(), part == PracticePart.Pick ? String(root, "picked") : null,
                Math.Max(0, Int(root, "hintsUsed") ?? 0), TimeSpan.FromMilliseconds(Math.Max(0, Int(root, "elapsedMs") ?? 0)), (Rating)grade,
                String(root, "mode") == "interview" ? PracticeMode.Interview : PracticeMode.Practice, String(root, "run"));
        }
        catch (JsonException) { return null; }
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
