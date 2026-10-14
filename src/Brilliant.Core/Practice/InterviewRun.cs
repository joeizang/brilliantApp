using System.Text.Json;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Practice;

/// <summary>
/// The kinds of timed interview run. Phone screen and Interview are fixed (problems, minutes); Custom takes the learner's own, within
/// <see cref="InterviewPresets.CustomProblems"/> and <see cref="InterviewPresets.CustomMinutes"/> (#128).
/// </summary>
public enum InterviewPreset { PhoneScreen, Interview, Custom }

public static class InterviewPresets
{
    /// <summary>The problem counts a Custom run may ask for.</summary>
    public static readonly (int Min, int Max) CustomProblems = (1, 5);

    /// <summary>The limits, in minutes, a Custom run may set.</summary>
    public static readonly (int Min, int Max) CustomMinutes = (10, 90);

    /// <summary>A preset's problem count; for Custom, what its setup starts at.</summary>
    public static int Problems(InterviewPreset preset) => preset switch { InterviewPreset.PhoneScreen => 2, _ => 3 };

    /// <summary>A preset's limit; for Custom, what its setup starts at.</summary>
    public static TimeSpan Limit(InterviewPreset preset) => TimeSpan.FromMinutes(preset == InterviewPreset.PhoneScreen ? 30 : 45);

    public static string Title(InterviewPreset preset) => preset switch
    {
        InterviewPreset.PhoneScreen => "Phone screen",
        InterviewPreset.Interview => "Interview",
        _ => "Custom",
    };

    /// <summary>Whether a Custom run may have this many problems against this limit (whole minutes only).</summary>
    public static bool IsValidCustom(int problems, TimeSpan limit) =>
        problems >= CustomProblems.Min && problems <= CustomProblems.Max
        && limit.Ticks % TimeSpan.TicksPerMinute == 0 && limit.TotalMinutes >= CustomMinutes.Min && limit.TotalMinutes <= CustomMinutes.Max;

    internal static string Key(InterviewPreset preset) => preset switch
    {
        InterviewPreset.PhoneScreen => "phone-screen",
        InterviewPreset.Interview => "interview",
        _ => "custom",
    };

    internal static InterviewPreset? Parse(string? key) => key switch
    {
        "phone-screen" => InterviewPreset.PhoneScreen,
        "interview" => InterviewPreset.Interview,
        "custom" => InterviewPreset.Custom,
        _ => null,
    };
}

/// <summary>One problem of an interview run, fixed when the run starts: the step, its lesson and the patterns offered, in order.</summary>
public sealed record InterviewProblem(string LessonId, string StepId, IReadOnlyList<string> Choices);

/// <summary>
/// A timed interview run as the log tells it. The clock is wall time: the run ends at <see cref="EndsAt"/> whether or not the app was
/// open. It is <em>open</em> until an <see cref="ProgressEventTypes.InterviewFinished"/> exists for it, even once its time is up.
/// </summary>
/// <param name="Answers">The run's first answer to each part of each problem; later ones are retries and never count.</param>
public sealed record InterviewRun(string Id, InterviewPreset Preset, IReadOnlyList<InterviewProblem> Problems, DateTimeOffset StartedAt,
    TimeSpan Limit, bool IsFinished, IReadOnlyList<PracticeAnswer> Answers)
{
    public DateTimeOffset EndsAt => StartedAt + Limit;

    /// <summary>The time left on the clock at <paramref name="now"/>, never negative.</summary>
    public TimeSpan Left(DateTimeOffset now) => EndsAt > now ? EndsAt - now : TimeSpan.Zero;

    /// <summary>Finished, or out of time (the summary is due even if nobody has written the finish yet).</summary>
    public bool IsOver(DateTimeOffset now) => IsFinished || now >= EndsAt;

    public PracticeAnswer? Answer(string stepId, PracticePart part) => Answers.FirstOrDefault(a => a.Step == stepId && a.Part == part);

    /// <summary>
    /// The soft time budget for one problem: the limit shared evenly. Nothing enforces it; the run shows how each problem's time compares.
    /// </summary>
    public TimeSpan Budget => Limit / Problems.Count;

    /// <summary>Both parts of every problem are answered: the run can end.</summary>
    public bool IsComplete => Problems.All(p => Answer(p.StepId, PracticePart.Pick) is not null && Answer(p.StepId, PracticePart.Solve) is not null);
}

/// <summary>Reads interview runs back from the log. Pure.</summary>
public static class InterviewRuns
{
    /// <summary>Every run in the log, in the order started. A malformed InterviewStarted is ignored.</summary>
    public static IReadOnlyList<InterviewRun> From(IEnumerable<ProgressEvent> events)
    {
        var list = events as IReadOnlyList<ProgressEvent> ?? events.ToList();
        var finished = list.Where(e => e.Type == ProgressEventTypes.InterviewFinished).Select(RunOf).OfType<string>().ToHashSet();
        // Oldest first, ties broken by event ID, as the projector orders them: two devices holding the same events in different append
        // orders agree on which answer came first, and the run shows the answer that was scheduled.
        var answers = list.Select(e => (Event: e, Answer: PracticeAnswer.From(e)))
            .Where(x => x.Answer is { Mode: PracticeMode.Interview, Run: not null })
            .OrderBy(x => x.Event.OccurredAt).ThenBy(x => x.Event.Id, StringComparer.Ordinal)
            .Select(x => x.Answer!)
            .ToLookup(a => a.Run!);
        var runs = new List<InterviewRun>();
        foreach (var e in list.Where(e => e.Type == ProgressEventTypes.InterviewStarted).OrderBy(e => e.OccurredAt).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            if (Started(e) is not { } run || runs.Any(r => r.Id == run.Id)) continue;
            // The first answer to each part counts.
            var first = answers[run.Id].GroupBy(a => (a.Step, a.Part)).Select(g => g.First()).ToList();
            runs.Add(run with { IsFinished = finished.Contains(run.Id), Answers = first });
        }
        return runs;
    }

    /// <summary>The run not yet finished, if any: the latest one started, as at most one is open at a time.</summary>
    public static InterviewRun? Open(IEnumerable<ProgressEvent> events) => From(events).LastOrDefault(r => !r.IsFinished);

    /// <summary>The run with this ID, or null.</summary>
    public static InterviewRun? Get(IEnumerable<ProgressEvent> events, string runId) => From(events).FirstOrDefault(r => r.Id == runId);

    internal static string StartedJson(string run, InterviewPreset preset, IReadOnlyList<InterviewProblem> problems, TimeSpan limit) =>
        JsonSerializer.Serialize(new
        {
            run,
            preset = InterviewPresets.Key(preset),
            steps = problems.Select(p => new { step = p.StepId, lesson = p.LessonId, choices = p.Choices }),
            limitSeconds = (int)limit.TotalSeconds,
        });

    internal static string FinishedJson(string run) => JsonSerializer.Serialize(new { run });

    private static InterviewRun? Started(ProgressEvent e)
    {
        if (e.Data is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (String(root, "run") is not { Length: > 0 } run) return null;
            if (InterviewPresets.Parse(String(root, "preset")) is not { } preset) return null;
            if (!root.TryGetProperty("limitSeconds", out var l) || l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out var seconds) || seconds <= 0) return null;
            if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return null;
            var problems = new List<InterviewProblem>();
            foreach (var s in steps.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object || String(s, "step") is not { Length: > 0 } step || String(s, "lesson") is not { } lesson) return null;
                var choices = s.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : [];
                problems.Add(new InterviewProblem(lesson, step, choices));
            }
            if (problems.Count == 0) return null;
            return new InterviewRun(run, preset, problems, e.OccurredAt, TimeSpan.FromSeconds(seconds), false, []);
        }
        catch (JsonException) { return null; }
    }

    private static string? RunOf(ProgressEvent e)
    {
        if (e.Data is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? String(doc.RootElement, "run") : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
