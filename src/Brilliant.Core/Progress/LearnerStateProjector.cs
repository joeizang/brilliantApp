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
        return new LearnerState(now, tracks, reviews, ReviewQueueBuilder.Build(reviews, now, answeredToday, queueOptions),
            ProjectConcepts(tracks, reviews, log, now));
    }

    /// <summary>
    /// Mastery per concept. A concept's evidence is its review items and the answers to their question steps, both in the
    /// lesson that taught it and in Review; the lesson's own other steps say nothing about it.
    /// </summary>
    private static IReadOnlyList<ConceptMastery> ProjectConcepts(
        IReadOnlyList<TrackState> tracks, IReadOnlyList<ReviewItemState> reviews, IReadOnlyList<ProgressEvent> log, DateTimeOffset now)
    {
        var concepts = new List<ConceptMastery>();
        foreach (var lesson in tracks.SelectMany(t => t.Lessons))
        {
            foreach (var concept in lesson.Lesson.Concepts)
            {
                var items = reviews.Where(r => r.Lesson.Id == lesson.Lesson.Id && r.Item.ConceptId == concept.Id).ToList();
                var steps = items.Select(i => i.Item.StepId).ToHashSet(StringComparer.Ordinal);
                var itemIds = items.Select(i => i.Item.Id).ToHashSet(StringComparer.Ordinal);
                var history = log
                    .Select(e => (Event: e, Source: AttemptSourceOf(e, lesson.Lesson.Id, steps, itemIds), Correct: Outcome(e)))
                    .Where(x => x.Source is not null && x.Correct is not null)
                    .OrderByDescending(x => x.Event.OccurredAt).ThenBy(x => x.Event.Id, StringComparer.Ordinal)
                    .Select(x => new ConceptAttempt(x.Event.OccurredAt, x.Source!.Value, x.Event.StepId, x.Correct!.Value))
                    .ToList();
                var mastery = MasteryModel.OfConcept(items, now);
                var level = MasteryModel.LevelOf(lesson.Status == LessonStatus.Completed, items, mastery);
                concepts.Add(new ConceptMastery(concept, lesson, lesson.Status == LessonStatus.Completed ? mastery : 0, level, items, history));
            }
        }
        return concepts;
    }

    private static AttemptSource? AttemptSourceOf(ProgressEvent e, string lessonId, HashSet<string> steps, HashSet<string> itemIds) => e.Type switch
    {
        ProgressEventTypes.StepAnswered or ProgressEventTypes.CodeSubmitted when e.LessonId == lessonId && steps.Contains(e.StepId) => AttemptSource.Lesson,
        ProgressEventTypes.ReviewAnswered when e.LessonId == lessonId && ParseReviewItem(e) is { } id && itemIds.Contains(id) => AttemptSource.Review,
        _ => null,
    };

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

        var hints = log.Where(e => e.Type == ProgressEventTypes.HintUsed)
            .Select(e => (Event: e, Hint: ParseHint(e))).Where(x => x.Hint is not null).ToList();
        var lessonHints = hints.Where(x => x.Hint!.Value.Item is null)
            .GroupBy(x => (x.Event.LessonId, x.Event.StepId))
            .ToDictionary(g => g.Key, g => g.Max(x => x.Hint!.Value.Level));
        var reviewHints = hints.Where(x => x.Hint!.Value.Item is not null)
            .ToLookup(x => x.Hint!.Value.Item!, x => (x.Event.OccurredAt, x.Hint!.Value.Level), StringComparer.Ordinal);

        // A problem comes back as a re-solve if it was answered incorrectly or the learner needed help with it.
        var troubled = log.Where(IsFailedAttempt).Select(e => (e.LessonId, e.StepId)).Concat(lessonHints.Keys).ToHashSet();

        var reviews = new List<ReviewItemState>();
        foreach (var lesson in tracks.SelectMany(t => t.Lessons))
        {
            var resolves = lesson.Lesson.Steps
                .Where(s => s is WriteCodeStep or FillBlankStep or ParsonsStep && troubled.Contains((lesson.Lesson.Id, s.Id)))
                .Select(s => new ReviewItem($"resolve.{s.Id}", "", s.Id, ReviewKind.Resolve));
            foreach (var item in lesson.Lesson.ReviewItems.Concat(resolves))
            {
                if (lesson.Lesson.Steps.FirstOrDefault(s => s.Id == item.StepId) is not { } question) continue;
                CardState? card = null;
                DateTimeOffset? lastAnswered = null;
                var count = 0;
                foreach (var (at, rating) in answers[item.Id])
                {
                    card = FsrsScheduler.Schedule(card, rating, at);
                    (lastAnswered, count) = (at, count + 1);
                }
                // Help asked for since the last answer belongs to the attempt in progress; earlier help was already judged.
                var attemptHints = reviewHints[item.Id].Where(h => lastAnswered is null || h.OccurredAt > lastAnswered).Select(h => h.Level).DefaultIfEmpty(0).Max();
                lessonHints.TryGetValue((lesson.Lesson.Id, question.Id), out var fromLesson);
                reviews.Add(new ReviewItemState(lesson.Lesson, item, question, lesson.Status == LessonStatus.Completed, card, count, attemptHints, fromLesson));
            }
        }
        return (reviews, answeredToday);
    }

    /// <summary>A wrong answer or a submission that failed a test. Malformed events are not failures.</summary>
    private static bool IsFailedAttempt(ProgressEvent e) =>
        e.Type is ProgressEventTypes.StepAnswered or ProgressEventTypes.CodeSubmitted && Outcome(e) == false;

    /// <summary>
    /// Whether an answer was right: <c>correct</c> for a step answer or review, <c>passed</c> (every test) for a code
    /// submission. Null for other events and for malformed data.
    /// </summary>
    private static bool? Outcome(ProgressEvent e)
    {
        if (e.Data is null || e.Type is not (ProgressEventTypes.StepAnswered or ProgressEventTypes.CodeSubmitted or ProgressEventTypes.ReviewAnswered)) return null;
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(e.Type == ProgressEventTypes.CodeSubmitted ? "passed" : "correct", out var ok)) return null;
            return ok.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        }
        catch (JsonException) { return null; }
    }

    private static string? ParseReviewItem(ProgressEvent e) => ParseReviewAnswer(e)?.Item;

    /// <summary>The rung a HintUsed event reached, and the review item it was asked for (null in a lesson). Malformed events are ignored.</summary>
    private static (int Level, string? Item)? ParseHint(ProgressEvent e)
    {
        if (e.Data is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("level", out var level) || level.ValueKind != JsonValueKind.Number || !level.TryGetInt32(out var rung) || rung < 1) return null;
            if (!root.TryGetProperty("item", out var item) || item.ValueKind == JsonValueKind.Null) return (rung, null);
            return item.ValueKind == JsonValueKind.String ? (rung, item.GetString()) : null;
        }
        catch (JsonException) { return null; }
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
