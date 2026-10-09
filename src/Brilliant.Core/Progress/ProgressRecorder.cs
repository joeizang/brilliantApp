using System.Text.Json;

namespace Brilliant.Core.Progress;

/// <summary>Stamps events with a fresh ID, this device's ID and the current time, and appends them to the log.</summary>
public sealed class ProgressRecorder(IProgressEventLog log, string deviceId, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IProgressEventLog Log => log;

    public void StepAnswered(string lessonId, string stepId, bool correct, IReadOnlyList<int> selected) =>
        Append(ProgressEventTypes.StepAnswered, lessonId, stepId,
            JsonSerializer.Serialize(new { correct, selected }));

    public void StepCompleted(string lessonId, string stepId) =>
        Append(ProgressEventTypes.StepCompleted, lessonId, stepId, null);

    private void Append(string type, string lessonId, string stepId, string? data) =>
        log.Append(new ProgressEvent(Guid.NewGuid().ToString("N"), deviceId, _clock.GetUtcNow(), type, lessonId, stepId, data));
}
