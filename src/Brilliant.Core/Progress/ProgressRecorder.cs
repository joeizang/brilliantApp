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
