using System.Text.Json;
using Brilliant.Core.Content;

namespace Brilliant.Core.Progress;

/// <summary>Stamps events with a fresh ID, this device's ID and the current time, and appends them to the log.</summary>
public sealed class ProgressRecorder(IProgressEventLog log, string deviceId, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IProgressEventLog Log => log;

    public void StepAnswered(string lessonId, string stepId, bool correct, IReadOnlyList<int> selected) =>
        Append(ProgressEventTypes.StepAnswered, lessonId, stepId,
            JsonSerializer.Serialize(new { correct, selected }));

    public void StepAnswered(string lessonId, string stepId, bool correct, string response) =>
        Append(ProgressEventTypes.StepAnswered, lessonId, stepId,
            JsonSerializer.Serialize(new { correct, response }));

    /// <summary>Records a fill-in-the-blank attempt: what the learner put in each blank (by blank ID).</summary>
    public void StepAnswered(string lessonId, string stepId, bool correct, IReadOnlyDictionary<string, string> blanks) =>
        Append(ProgressEventTypes.StepAnswered, lessonId, stepId,
            JsonSerializer.Serialize(new { correct, blanks }));

    /// <summary>Records a Parsons attempt: the arrangement as submitted, each line as its index in the solution plus the indentation level chosen.</summary>
    public void StepAnswered(string lessonId, string stepId, bool correct, IReadOnlyList<ParsonsPlacement> arrangement) =>
        Append(ProgressEventTypes.StepAnswered, lessonId, stepId,
            JsonSerializer.Serialize(new { correct, arrangement = arrangement.Select(p => new { line = p.Piece, level = p.Level }) }));

    /// <summary>Records one write-code submission: the code as submitted and how many hidden tests it passed.</summary>
    public void CodeSubmitted(string lessonId, string stepId, string code, int passedTests, int totalTests) =>
        Append(ProgressEventTypes.CodeSubmitted, lessonId, stepId,
            JsonSerializer.Serialize(new { code, passed = totalTests > 0 && passedTests == totalTests, passedTests, totalTests }));

    public void StepCompleted(string lessonId, string stepId) =>
        Append(ProgressEventTypes.StepCompleted, lessonId, stepId, null);

    /// <summary>Records StepCompleted and, when that finishes the lesson for the first time, LessonCompleted.</summary>
    public void CompleteStep(Lesson lesson, string stepId)
    {
        StepCompleted(lesson.Id, stepId);
        var progress = LessonProgress.From(lesson, log.ReadAll());
        if (progress.IsComplete && !progress.CompletionRecorded)
            Append(ProgressEventTypes.LessonCompleted, lesson.Id, "", null);
    }

    private void Append(string type, string lessonId, string stepId, string? data) =>
        log.Append(new ProgressEvent(Guid.NewGuid().ToString("N"), deviceId, _clock.GetUtcNow(), type, lessonId, stepId, data));
}
