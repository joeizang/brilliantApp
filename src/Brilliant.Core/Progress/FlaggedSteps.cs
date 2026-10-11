using System.Text;
using System.Text.Json;
using Brilliant.Core.Content;

namespace Brilliant.Core.Progress;

/// <summary>One time the learner flagged a step. <see cref="Lesson"/> and <see cref="Step"/> are null when content has since removed them.</summary>
public sealed record FlaggedStep(string LessonId, string StepId, Lesson? Lesson, Step? Step, string? Note, DateTimeOffset FlaggedAt)
{
    public string LessonTitle => Lesson?.Title ?? LessonId;
    public string StepTitle => Step?.Title ?? StepId;
}

/// <summary>Reads StepFlagged events back as a list for the author, and renders that list as Markdown.</summary>
public static class FlaggedSteps
{
    /// <summary>
    /// Every flag, in content order (track, lesson, step), then oldest first. Flags are never withdrawn, so flagging a step twice
    /// lists it twice, each with its own note and date. Flags on content that no longer exists come last.
    /// </summary>
    public static IReadOnlyList<FlaggedStep> From(IEnumerable<ProgressEvent> events, ContentGraph content)
    {
        var lessonOrder = content.Tracks
            .SelectMany(t => t.LessonIds)
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        // Distinct by ID and in a fixed order first, so the same events give the same list however they arrived (sync delivers them from any device in any order).
        return events
            .Where(e => e.Type == ProgressEventTypes.StepFlagged)
            .DistinctBy(e => e.Id)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e =>
            {
                content.TryGet<Lesson>(e.LessonId, out var lesson);
                var step = lesson?.Steps.FirstOrDefault(s => s.Id == e.StepId);
                return new FlaggedStep(e.LessonId, e.StepId, lesson, step, NoteOf(e.Data), e.OccurredAt);
            })
            .OrderBy(f => f.Step is null ? int.MaxValue : lessonOrder.GetValueOrDefault(f.LessonId, int.MaxValue))
            .ThenBy(f => f.Lesson is null || f.Step is null ? int.MaxValue : IndexOf(f.Lesson, f.Step))
            .ThenBy(f => f.FlaggedAt)
            .ToList();
    }

    /// <summary>The flagged steps as a Markdown document: a section per lesson, a bullet per flag with step ID, note and date.</summary>
    public static string ToMarkdown(IReadOnlyList<FlaggedStep> flags)
    {
        var md = new StringBuilder("# Flagged steps\n");
        if (flags.Count == 0) return md.Append("\nNothing is flagged.\n").ToString();

        foreach (var lesson in flags.GroupBy(f => f.LessonId))
        {
            md.Append("\n## ").Append(lesson.First().LessonTitle).Append(" (`").Append(lesson.Key).Append("`)\n\n");
            foreach (var f in lesson)
            {
                md.Append("- **").Append(f.StepTitle).Append("** (`").Append(f.StepId).Append("`), ")
                  .Append(f.FlaggedAt.ToString("yyyy-MM-dd"));
                if (f.Step is null) md.Append(", step no longer exists");
                md.Append('\n');
                if (f.Note is not null)
                    foreach (var line in f.Note.ReplaceLineEndings("\n").Split('\n'))
                        md.Append("  > ").Append(line).Append('\n');
            }
        }
        return md.ToString();
    }

    private static int IndexOf(Lesson lesson, Step step)
    {
        for (var i = 0; i < lesson.Steps.Count; i++)
            if (lesson.Steps[i].Id == step.Id) return i;
        return int.MaxValue;
    }

    private static string? NoteOf(string? data)
    {
        if (data is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }
}
