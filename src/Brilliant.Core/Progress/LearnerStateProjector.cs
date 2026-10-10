using Brilliant.Core.Content;

namespace Brilliant.Core.Progress;

/// <summary>
/// The one place progress is computed: a pure, deterministic function of (events, content, now). It reads nothing
/// else and writes nothing, so two devices holding the same merged log always agree.
/// </summary>
/// <remarks>
/// Content changes are handled by stable ID, so authors can edit freely:
/// <list type="bullet">
/// <item>An <b>edited</b> step keeps its ID and therefore its history.</item>
/// <item>A <b>deleted</b> step (or lesson) is retired: its events stay in the log but no longer count.</item>
/// <item>A <b>new</b> step in a finished lesson shows as "new" and never un-completes the lesson.</item>
/// </list>
/// Events are de-duplicated by ID and their order is irrelevant, so replays and sync merges change nothing.
/// </remarks>
public static class LearnerStateProjector
{
    /// <param name="now">The projection time. Carried on the state so time-based rules (streaks, due reviews) can build on it.</param>
    public static LearnerState Project(IEnumerable<ProgressEvent> events, ContentGraph content, DateTimeOffset now)
    {
        var log = Distinct(events);
        var tracks = content.Tracks.Select(track => ProjectTrack(track, content, log)).ToList();
        return new LearnerState(now, tracks);
    }

    /// <summary>The progress of one lesson, for screens that don't need the whole course (e.g. the lesson player).</summary>
    public static LessonProgress ProjectLesson(Lesson lesson, IEnumerable<ProgressEvent> events)
    {
        var ofLesson = Distinct(events).Where(e => e.LessonId == lesson.Id).ToList();
        return new LessonProgress(lesson,
            ofLesson.Where(e => e.Type == ProgressEventTypes.StepCompleted).Select(e => e.StepId).ToHashSet(StringComparer.Ordinal),
            ofLesson.Any(e => e.Type == ProgressEventTypes.LessonCompleted));
    }

    /// <summary>Within a track a lesson is unlocked when it is the first or the previous lesson is complete.</summary>
    private static TrackState ProjectTrack(Track track, ContentGraph content, IReadOnlyList<ProgressEvent> log)
    {
        var lessons = new List<LessonState>();
        var previousComplete = true;
        foreach (var lesson in track.LessonIds.Select(content.Get<Lesson>))
        {
            var progress = ProjectLesson(lesson, log);
            var status = progress.IsComplete ? LessonStatus.Completed
                : !previousComplete ? LessonStatus.Locked
                : progress.CompletedCount > 0 ? LessonStatus.InProgress
                : LessonStatus.Available;
            lessons.Add(new LessonState(lesson, status, progress));
            previousComplete = progress.IsComplete;
        }
        return new TrackState(track, lessons);
    }

    /// <summary>First occurrence of each event ID, so an event delivered twice (a re-sync) counts once.</summary>
    private static IReadOnlyList<ProgressEvent> Distinct(IEnumerable<ProgressEvent> events)
    {
        if (events is DistinctEvents already) return already;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return new DistinctEvents(events.Where(e => seen.Add(e.Id)).ToList());
    }

    /// <summary>Marks a list that has already been de-duplicated, so each lesson doesn't repeat the work.</summary>
    private sealed class DistinctEvents(List<ProgressEvent> items) : List<ProgressEvent>(items);
}
