using Brilliant.Core.Content;

namespace Brilliant.Core.Progress;

public enum LessonStatus { Locked, Available, InProgress, Completed }

public sealed record LessonState(Lesson Lesson, LessonStatus Status, LessonProgress Progress)
{
    public bool IsUnlocked => Status != LessonStatus.Locked;
}

public sealed record TrackState(Track Track, IReadOnlyList<LessonState> Lessons)
{
    public int TotalLessons => Lessons.Count;
    public int CompletedLessons => Lessons.Count(l => l.Status == LessonStatus.Completed);
    public bool IsComplete => TotalLessons > 0 && CompletedLessons == TotalLessons;
}

/// <summary>Tracks, lessons and their lock/completion status, derived purely from the event log.</summary>
public static class CourseProgress
{
    /// <summary>Within a track a lesson is unlocked when it is the first or the previous lesson is complete.</summary>
    public static IReadOnlyList<TrackState> From(ContentGraph content, IEnumerable<ProgressEvent> events)
    {
        var log = events as IReadOnlyList<ProgressEvent> ?? events.ToList();
        return content.Tracks.Select(track =>
        {
            var lessons = new List<LessonState>();
            var previousComplete = true;
            foreach (var lesson in track.LessonIds.Select(content.Get<Lesson>))
            {
                var progress = LessonProgress.From(lesson, log);
                var status = progress.IsComplete ? LessonStatus.Completed
                    : !previousComplete ? LessonStatus.Locked
                    : progress.CompletedCount > 0 ? LessonStatus.InProgress
                    : LessonStatus.Available;
                lessons.Add(new LessonState(lesson, status, progress));
                previousComplete = progress.IsComplete;
            }
            return new TrackState(track, lessons);
        }).ToList();
    }
}
