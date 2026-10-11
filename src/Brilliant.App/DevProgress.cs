#if DEBUG
using Brilliant.Core.Content;
using Brilliant.Core.Progress;

namespace Brilliant.App;

/// <summary>
/// Debug builds only: opens the app at a chosen lesson without touching real progress. With
/// <c>BRILLIANT_DEV_START_AT=lesson.some-id</c> set, progress lives in memory (nothing is written to brilliant.db) and every lesson
/// before the named one, across all tracks in pack order, counts as completed, so the named lesson is the first one available.
/// Quitting the app forgets it all.
/// </summary>
internal static class DevProgress
{
    public const string StartAtVariable = "BRILLIANT_DEV_START_AT";

    /// <summary>The scratch log for the lesson named by the environment variable, or null when it isn't set.</summary>
    public static IProgressEventLog? TryCreate(ContentGraph content)
    {
        var target = Environment.GetEnvironmentVariable(StartAtVariable);
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (!content.Lessons.Any(l => l.Id == target))
            throw new InvalidOperationException($"{StartAtVariable}={target}: no such lesson in the content pack.");

        var log = new MemoryLog();
        var at = DateTimeOffset.UtcNow;
        foreach (var lesson in content.Tracks.SelectMany(t => t.LessonIds).Select(content.Get<Lesson>).TakeWhile(l => l.Id != target))
        {
            foreach (var step in lesson.Steps)
                log.Append(new ProgressEvent(Guid.NewGuid().ToString("N"), "dev", at, ProgressEventTypes.StepCompleted, lesson.Id, step.Id));
            log.Append(new ProgressEvent(Guid.NewGuid().ToString("N"), "dev", at, ProgressEventTypes.LessonCompleted, lesson.Id, ""));
        }
        return log;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        private readonly List<ProgressEvent> _events = [];
        public bool Append(ProgressEvent evt)
        {
            if (_events.Any(e => e.Id == evt.Id)) return false;
            _events.Add(evt);
            return true;
        }
        public IReadOnlyList<ProgressEvent> ReadAll() => _events.ToList();
        public EventPage ReadSince(long cursor) => new(_events.Skip((int)cursor).ToList(), _events.Count);
    }
}
#endif
