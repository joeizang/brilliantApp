using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Review;

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
    /// <param name="now">
    /// The projection time. Carried on the state so time-based rules (streaks, due reviews) can build on it. Its offset
    /// defines the learner's day: pass local time and "today" (the daily review cap) rolls over at the learner's midnight.
    /// </param>
    /// <param name="queueOptions">The daily review cap and weights; the defaults when omitted.</param>
    public static LearnerState Project(IEnumerable<ProgressEvent> events, ContentGraph content, DateTimeOffset now, ReviewQueueOptions? queueOptions = null)
    {
        var log = Distinct(events);
        var tracks = content.Tracks.Select(track => ProjectTrack(track, content, log)).ToList();
        var (reviews, answeredToday) = ProjectReviews(tracks, log, now);
        return new LearnerState(now, tracks, reviews, ReviewQueueBuilder.Build(reviews, now, answeredToday, queueOptions));
    }

    /// <summary>The progress of one lesson, for screens that don't need the whole course (e.g. the lesson player).</summary>
    public static LessonProgress ProjectLesson(Lesson lesson, IEnumerable<ProgressEvent> events)
    {
        var ofLesson = Distinct(events).Where(e => e.LessonId == lesson.Id).ToList();
        return new LessonProgress(lesson,
            ofLesson.Where(e => e.Type == ProgressEventTypes.StepCompleted).Select(e => e.StepId).ToHashSet(StringComparer.Ordinal),
            ofLesson.Any(e => e.Type == ProgressEventTypes.LessonCompleted));
    }

    /// <summary>
    /// Review items of the current content: the ones lessons declare, plus a re-solve for each problem (write-code,
    /// fill-blank, Parsons) the learner has answered incorrectly. An item is unlocked once its lesson is completed; its
    /// schedule is every recorded answer folded through FSRS in time order (ties broken by event ID, so every device
    /// agrees). An item whose question step has been deleted is retired along with its history.
    /// Also returns how many reviews were answered on the same (local) day as <paramref name="now"/>.
    /// </summary>
    private static (IReadOnlyList<ReviewItemState> Reviews, int AnsweredToday) ProjectReviews(
        IReadOnlyList<TrackState> tracks, IReadOnlyList<ProgressEvent> log, DateTimeOffset now)
    {
        var parsed = log.Where(e => e.Type == ProgressEventTypes.ReviewAnswered)
            .Select(e => (Event: e, Answer: ParseReviewAnswer(e)))
            .Where(x => x.Answer is not null)
            .OrderBy(x => x.Event.OccurredAt).ThenBy(x => x.Event.Id, StringComparer.Ordinal)
            .ToList();
        var answers = parsed.ToLookup(x => x.Answer!.Value.Item, x => (x.Event.OccurredAt, x.Answer!.Value.Rating), StringComparer.Ordinal);
        var answeredToday = parsed.Count(x => x.Event.OccurredAt.ToOffset(now.Offset).Date == now.Date);

        var failed = log.Where(IsFailedAttempt).Select(e => (e.LessonId, e.StepId)).ToHashSet();

        var reviews = new List<ReviewItemState>();
        foreach (var lesson in tracks.SelectMany(t => t.Lessons))
        {
            var resolves = lesson.Lesson.Steps
                .Where(s => s is WriteCodeStep or FillBlankStep or ParsonsStep && failed.Contains((lesson.Lesson.Id, s.Id)))
                .Select(s => new ReviewItem($"resolve.{s.Id}", "", s.Id, ReviewKind.Resolve));
            foreach (var item in lesson.Lesson.ReviewItems.Concat(resolves))
            {
                if (lesson.Lesson.Steps.FirstOrDefault(s => s.Id == item.StepId) is not { } question) continue;
                CardState? card = null;
                var count = 0;
                foreach (var (at, rating) in answers[item.Id])
                {
                    card = FsrsScheduler.Schedule(card, rating, at);
                    count++;
                }
                reviews.Add(new ReviewItemState(lesson.Lesson, item, question, lesson.Status == LessonStatus.Completed, card, count));
            }
        }
        return (reviews, answeredToday);
    }

    /// <summary>A wrong answer or a submission that failed a test. Malformed events are not failures.</summary>
    private static bool IsFailedAttempt(ProgressEvent e)
    {
        if (e.Data is null || e.Type is not (ProgressEventTypes.StepAnswered or ProgressEventTypes.CodeSubmitted)) return false;
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(e.Type == ProgressEventTypes.CodeSubmitted ? "passed" : "correct", out var ok)
                && ok.ValueKind == JsonValueKind.False;
        }
        catch (JsonException) { return false; }
    }

    private static (string Item, Rating Rating)? ParseReviewAnswer(ProgressEvent e)
    {
        if (e.Data is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("rating", out var rating) || rating.ValueKind != JsonValueKind.Number || !rating.TryGetInt32(out var grade)
                || grade is < 1 or > 4) return null;
            return (item.GetString()!, (Rating)grade);
        }
        catch (JsonException) { return null; }
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
