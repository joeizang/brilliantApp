using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Review;

namespace Brilliant.Core.Progress;

/// <summary>Stamps events with a fresh ID, this device's ID and the current time, and appends them to the log.</summary>
public sealed class ProgressRecorder(IProgressEventLog log, string deviceId, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IProgressEventLog Log => log;

    /// <summary>The recorder's clock, so screens that time the learner use the same one that stamps events.</summary>
    public DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>
    /// The learner's state right now: the whole log projected against <paramref name="content"/>. Projected at local time,
    /// so the daily review cap rolls over at the learner's midnight.
    /// </summary>
    public LearnerState Project(ContentGraph content) =>
        LearnerStateProjector.Project(log.ReadAll(), content, _clock.GetLocalNow());

    /// <summary>
    /// Records LessonCompleted for lessons that a content change has just completed (the learner's last unfinished
    /// step was deleted), so the completion survives later content changes. The projector is pure and can't remember
    /// that on its own. Idempotent; run it whenever content is loaded. Returns how many completions were recorded.
    /// </summary>
    public int RecordCompletionsFrom(ContentGraph content)
    {
        var recorded = 0;
        foreach (var lesson in Project(content).Tracks.SelectMany(t => t.Lessons))
        {
            if (lesson.Status != LessonStatus.Completed || lesson.Progress.CompletionRecorded || lesson.Progress.TotalSteps == 0) continue;
            Append(ProgressEventTypes.LessonCompleted, lesson.Lesson.Id, "", null);
            recorded++;
        }
        return recorded;
    }

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

    /// <summary>
    /// Records that the learner revealed hint <paramref name="level"/> (1 = the gentlest nudge) of a step. Pass
    /// <paramref name="reviewItemId"/> when the question was asked by Review, so a lesson attempt and a review attempt stay apart.
    /// </summary>
    public void HintUsed(string lessonId, string stepId, int level, string? reviewItemId = null) =>
        Append(ProgressEventTypes.HintUsed, lessonId, stepId,
            JsonSerializer.Serialize(new { level, item = reviewItemId }));

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

    /// <summary>Records a review answer with an explicit rating.</summary>
    public void ReviewAnswered(string lessonId, ReviewItem item, bool correct, int hintsUsed, TimeSpan elapsed, Rating rating) =>
        Append(ProgressEventTypes.ReviewAnswered, lessonId, item.StepId,
            JsonSerializer.Serialize(new { item = item.Id, correct, hintsUsed, elapsedMs = (int)elapsed.TotalMilliseconds, rating = (int)rating }));

    /// <summary>Records a review answer, inferring the FSRS rating from correctness, hints and time. Returns the rating.</summary>
    public Rating ReviewAnswered(ReviewItemState review, bool correct, int hintsUsed, TimeSpan elapsed)
    {
        var rating = RatingInference.Infer(correct, hintsUsed, elapsed, review.Question);
        ReviewAnswered(review.Lesson.Id, review.Item, correct, hintsUsed, elapsed, rating);
        return rating;
    }

    private void Append(string type, string lessonId, string stepId, string? data) =>
        log.Append(new ProgressEvent(Guid.NewGuid().ToString("N"), deviceId, _clock.GetUtcNow(), type, lessonId, stepId, data));
}
