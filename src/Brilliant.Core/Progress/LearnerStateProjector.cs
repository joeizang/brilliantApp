using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Practice;
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
    /// <param name="screen">The screen the review queue is for. It changes only the form of each queued question, never the learner's progress.</param>
    public static LearnerState Project(IEnumerable<ProgressEvent> events, ContentGraph content, DateTimeOffset now, ReviewQueueOptions? queueOptions = null,
        ScreenClass screen = ScreenClass.Desktop)
    {
        var log = Distinct(events);
        var tracks = content.Tracks.Select(track => ProjectTrack(track, content, log)).ToList();
        var practice = PracticeAnswers(log);
        var (reviews, answeredToday) = ProjectReviews(tracks, log, practice, now);
        var queue = ReviewQueueBuilder.Build(reviews, now, answeredToday, queueOptions, screen);
        return new LearnerState(now, tracks, reviews, queue, ProjectConcepts(tracks, reviews, log, now), ProjectToday(tracks, queue, log, now),
            ProjectPatterns(tracks, reviews, practice, now));
    }

    /// <summary>
    /// What the learner did in a window of time: steps completed, reviews answered, how many of the answers were right, and which
    /// concepts' mastery moved (compared as projected at <paramref name="since"/> and at <paramref name="until"/>). Pure, like
    /// <see cref="Project"/>.
    /// </summary>
    public static SessionSummary Summarize(IEnumerable<ProgressEvent> events, ContentGraph content, DateTimeOffset since, DateTimeOffset until)
    {
        var log = Distinct(events);
        var window = log.Where(e => e.OccurredAt >= since && e.OccurredAt <= until).ToList();
        var outcomes = window.Select(Outcome).Where(o => o is not null).ToList();

        var before = Project(log.Where(e => e.OccurredAt < since), content, since).Concepts
            .ToDictionary(c => (c.Lesson.Lesson.Id, c.Concept.Id), c => c.Mastery);
        var changes = Project(log.Where(e => e.OccurredAt <= until), content, until).Concepts
            .Where(c => c.History.Any(a => a.At >= since && a.At <= until))      // only what was practised, not what merely faded meanwhile
            .Select(c => new MasteryChange(c.Concept, c.Lesson, before[(c.Lesson.Lesson.Id, c.Concept.Id)], c.Mastery))
            .Where(c => Math.Abs(c.Delta) > 1e-9)
            .OrderByDescending(c => Math.Abs(c.Delta)).ThenBy(c => c.Concept.Id, StringComparer.Ordinal)
            .ToList();

        return new SessionSummary(
            window.Where(e => e.Type == ProgressEventTypes.StepCompleted).Select(e => (e.LessonId, e.StepId)).Distinct().Count(),
            window.Count(e => e.Type == ProgressEventTypes.ReviewAnswered && ParseReviewAnswer(e) is not null),
            outcomes.Count, outcomes.Count(o => o == true), changes);
    }

    /// <summary>
    /// The goal, today's progress and the streak. Progress is the steps completed (each step once a day) plus the reviews
    /// answered, on the learner's local day. A day counts toward the streak when it met the goal in force at its end, so changing
    /// the goal never rewrites history.
    /// </summary>
    private static TodayState ProjectToday(
        IReadOnlyList<TrackState> tracks, ReviewQueue queue, IReadOnlyList<ProgressEvent> log, DateTimeOffset now)
    {
        DateTime DayOf(ProgressEvent e) => e.OccurredAt.ToOffset(now.Offset).Date;

        var done = new Dictionary<DateTime, int>();
        foreach (var day in log.Where(e => e.Type == ProgressEventTypes.StepCompleted).Select(e => (Day: DayOf(e), e.LessonId, e.StepId)).Distinct())
            done[day.Day] = done.GetValueOrDefault(day.Day) + 1;
        foreach (var e in log.Where(e => e.Type == ProgressEventTypes.ReviewAnswered && ParseReviewAnswer(e) is not null))
            done[DayOf(e)] = done.GetValueOrDefault(DayOf(e)) + 1;

        var goals = log.Where(e => e.Type == ProgressEventTypes.DailyGoalSet)
            .Select(e => (Event: e, Steps: ParseGoal(e))).Where(x => x.Steps is not null)
            .OrderBy(x => x.Event.OccurredAt).ThenBy(x => x.Event.Id, StringComparer.Ordinal)
            .Select(x => (Day: DayOf(x.Event), Steps: x.Steps!.Value)).ToList();
        int GoalOn(DateTime day) => goals.LastOrDefault(g => g.Day <= day) is { Steps: > 0 } g ? g.Steps : TodayState.DefaultGoal;
        bool Met(DateTime day) => done.GetValueOrDefault(day) >= GoalOn(day);

        var today = now.Date;
        var streak = 0;
        for (var day = Met(today) ? today : today.AddDays(-1); Met(day); day = day.AddDays(-1)) streak++;

        var lessons = tracks.SelectMany(t => t.Lessons).ToList();
        var next = lessons.FirstOrDefault(l => l.Status == LessonStatus.InProgress) ?? lessons.FirstOrDefault(l => l.Status == LessonStatus.Available);

        var started = log.Where(e => e.Type == ProgressEventTypes.SessionStarted && DayOf(e) == today)
            .OrderByDescending(e => e.OccurredAt).ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
        var session = started is not null && !log.Any(e => e.Type == ProgressEventTypes.SessionCompleted && e.OccurredAt >= started.OccurredAt)
            ? new SessionState(started.LessonId.Length == 0 ? null : started.LessonId, started.OccurredAt)
            : null;

        return new TodayState(goals.Count == 0 ? TodayState.DefaultGoal : GoalOn(today), done.GetValueOrDefault(today), streak, next,
            queue.Items.Count, session);
    }

    private static int? ParseGoal(ProgressEvent e)
    {
        if (e.Data is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Number && steps.TryGetInt32(out var n)
                ? TodayState.ClampGoal(n) : null;
        }
        catch (JsonException) { return null; }
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
    /// The practice and interview answers that count, oldest first (ties broken by event ID): within one run only the first answer to
    /// each part of each problem, so a retry is practice and changes no schedule. Parts an interview ended without
    /// (<see cref="PracticeAnswer.Answered"/> false) and malformed events are ignored; an unanswered part stays unanswered even if a later
    /// answer to it arrives, since only the first counts.
    /// </summary>
    private static IReadOnlyList<(ProgressEvent Event, PracticeAnswer Answer)> PracticeAnswers(IReadOnlyList<ProgressEvent> log)
    {
        // The first answer to a part in a run wins before unanswered ones are dropped, as InterviewRuns decides it: an unanswered
        // placeholder that came first (one device finished the run, another then submitted) keeps the part unanswered everywhere.
        var seen = new HashSet<(string, string, PracticePart)>();
        return log.Select(e => (Event: e, Answer: PracticeAnswer.From(e)))
            .Where(x => x.Answer is not null)
            .OrderBy(x => x.Event.OccurredAt).ThenBy(x => x.Event.Id, StringComparer.Ordinal)
            .Where(x => x.Answer!.Run is not { } run || seen.Add((run, x.Answer.Step, x.Answer.Part)))
            .Where(x => x.Answer!.Answered)
            .Select(x => (x.Event, x.Answer!))
            .ToList();
    }

    /// <summary>
    /// Review items of the current content: the ones lessons declare, plus items derived from the log for problem steps (write-code,
    /// fill-blank, Parsons): a re-solve for each problem the learner answered incorrectly, needed a hint on or solved in practice, and a
    /// pattern pick for each tagged problem picked in practice. An item is unlocked once its lesson is completed; its schedule is every
    /// recorded answer, from Review and from practice, folded through FSRS in time order (ties broken by event ID, so every device agrees),
    /// except a practice answer on the same local day as the item's previous answer, by the day each answer recorded (<see cref="AnswerDay"/>):
    /// an evening spent on one problem is one day's evidence.
    /// An item whose question step has been deleted (or, for a pick, untagged) is retired along with its history.
    /// Also returns how many reviews were answered on the same (local) day as <paramref name="now"/>; practice doesn't count toward it.
    /// </summary>
    private static (IReadOnlyList<ReviewItemState> Reviews, int AnsweredToday) ProjectReviews(
        IReadOnlyList<TrackState> tracks, IReadOnlyList<ProgressEvent> log, IReadOnlyList<(ProgressEvent Event, PracticeAnswer Answer)> practice,
        DateTimeOffset now)
    {
        var parsed = log.Where(e => e.Type == ProgressEventTypes.ReviewAnswered)
            .Select(e => (Event: e, Answer: ParseReviewAnswer(e)))
            .Where(x => x.Answer is not null)
            .ToList();
        var answers = parsed.Select(x => (x.Event, x.Answer!.Value.Item, x.Answer!.Value.Rating, Practice: false))
            .Concat(practice.Select(x => (x.Event, Item: x.Answer.ItemId, x.Answer.Rating, Practice: true)))
            .OrderBy(x => x.Event.OccurredAt).ThenBy(x => x.Event.Id, StringComparer.Ordinal)
            .ToLookup(x => x.Item, x => (x.Event.OccurredAt, Day: AnswerDay.Of(x.Event), x.Rating, x.Practice), StringComparer.Ordinal);
        var answeredToday = parsed.Count(x => x.Event.OccurredAt.ToOffset(now.Offset).Date == now.Date);
        var practised = practice.Select(x => (x.Event.LessonId, x.Answer.Step, x.Answer.Part)).ToHashSet();

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
            var id = lesson.Lesson.Id;
            var problems = lesson.Lesson.AllSteps().Where(s => s is WriteCodeStep or FillBlankStep or ParsonsStep).ToList();
            var resolves = problems
                .Where(s => troubled.Contains((id, s.Id)) || practised.Contains((id, s.Id, PracticePart.Solve)))
                .Select(s => new ReviewItem(PracticeAnswer.ItemIdOf(s.Id, PracticePart.Solve), "", s.Id, ReviewKind.Resolve));
            var picks = problems
                .Where(s => s.Pattern is not null && practised.Contains((id, s.Id, PracticePart.Pick)))
                .Select(s => new ReviewItem(PracticeAnswer.ItemIdOf(s.Id, PracticePart.Pick), "", s.Id, ReviewKind.Pattern));
            foreach (var item in lesson.Lesson.ReviewItems.Concat(resolves).Concat(picks))
            {
                if (lesson.Lesson.AllSteps().FirstOrDefault(s => s.Id == item.StepId) is not { } question) continue;
                CardState? card = null;
                DateTimeOffset? lastAnswered = null;
                DateOnly? lastDay = null;
                Rating? lastRating = null;
                var count = 0;
                foreach (var (at, day, rating, fromPractice) in answers[item.Id])
                {
                    if (fromPractice && lastDay == day) continue;
                    card = FsrsScheduler.Schedule(card, rating, at);
                    (lastAnswered, lastDay, lastRating, count) = (at, day, rating, count + 1);
                }
                // Help asked for since the last answer belongs to the attempt in progress; earlier help was already judged.
                var attemptHints = reviewHints[item.Id].Where(h => lastAnswered is null || h.OccurredAt > lastAnswered).Select(h => h.Level).DefaultIfEmpty(0).Max();
                lessonHints.TryGetValue((lesson.Lesson.Id, question.Id), out var fromLesson);
                reviews.Add(new ReviewItemState(lesson.Lesson, item, question, lesson.Status == LessonStatus.Completed, card, count, attemptHints, fromLesson)
                    { LastRating = lastRating });
            }
        }
        return (reviews, answeredToday);
    }

    /// <summary>
    /// Mastery per declared pattern, in content order. A pattern's problems are the steps tagged with it in completed lessons of any
    /// track; each scores the mean of its pick and re-solve items (<see cref="MasteryModel.OfPracticeStep"/>).
    /// </summary>
    private static IReadOnlyList<PatternMastery> ProjectPatterns(IReadOnlyList<TrackState> tracks, IReadOnlyList<ReviewItemState> reviews,
        IReadOnlyList<(ProgressEvent Event, PracticeAnswer Answer)> practice, DateTimeOffset now)
    {
        var lessons = tracks.SelectMany(t => t.Lessons).ToList();
        var items = reviews.Where(r => r.Item.Kind != ReviewKind.Concept && r.Item.ConceptId.Length == 0)
            .ToDictionary(r => (r.Lesson.Id, r.Item.Id));
        var lastPractised = practice.GroupBy(x => (x.Event.LessonId, x.Answer.Step))
            .ToDictionary(g => g.Key, g => g.Max(x => x.Event.OccurredAt));

        var tagged = lessons.Where(l => l.Status == LessonStatus.Completed)
            .SelectMany(l => l.Lesson.AllSteps().Where(s => s.Pattern is not null && s is WriteCodeStep or FillBlankStep or ParsonsStep).Select(s =>
            {
                var pick = items.GetValueOrDefault((l.Lesson.Id, PracticeAnswer.ItemIdOf(s.Id, PracticePart.Pick)));
                var resolve = items.GetValueOrDefault((l.Lesson.Id, PracticeAnswer.ItemIdOf(s.Id, PracticePart.Solve)));
                return new PatternStep(l, s, pick, resolve, lastPractised.TryGetValue((l.Lesson.Id, s.Id), out var at) ? at : null,
                    MasteryModel.OfPracticeStep(pick, resolve, now));
            }))
            .ToLookup(s => s.Step.Pattern!, StringComparer.Ordinal);

        return lessons.SelectMany(l => l.Lesson.Patterns.Select(p =>
        {
            var steps = tagged[p.Id].ToList();
            var learned = l.Status == LessonStatus.Completed;
            var mastery = learned && steps.Count > 0 ? steps.Average(s => s.Mastery) : 0;
            return new PatternMastery(p, l, mastery, MasteryModel.LevelOf(learned, steps, mastery), steps);
        })).ToList();
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
