using System.Text.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Practice;
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
    public LearnerState Project(ContentGraph content, ScreenClass screen = ScreenClass.Desktop) =>
        LearnerStateProjector.Project(log.ReadAll(), content, _clock.GetLocalNow(), screen: screen);

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
            JsonSerializer.Serialize(new { item = item.Id, correct, hintsUsed, elapsedMs = (int)elapsed.TotalMilliseconds, rating = (int)rating, day = Day() }));

    // The learner's local date now, stored on answers so the same-day rule replays the same everywhere (AnswerDay).
    private string Day() => AnswerDay.Today(_clock).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Records a review answer, inferring the FSRS rating from correctness, hints and time. Returns the rating. A derived pattern pick
    /// is rated as a pick (<see cref="RatingInference.InferPick"/>): its question is the problem, but the answer only names the pattern.
    /// </summary>
    public Rating ReviewAnswered(ReviewItemState review, bool correct, int hintsUsed, TimeSpan elapsed)
    {
        var rating = review.IsPracticePick
            ? RatingInference.InferPick(correct, elapsed, hintsUsed)
            : RatingInference.Infer(correct, hintsUsed, elapsed, review.Presented);
        ReviewAnswered(review.Lesson.Id, review.Item, correct, hintsUsed, elapsed, rating);
        return rating;
    }

    /// <summary>
    /// Records the pattern pick of a practice or interview problem: <paramref name="picked"/> is the pattern ID chosen, or null when the
    /// learner never chose (an interview problem left unanswered: wrong, and not <see cref="PracticeAnswer.Answered"/>). Returns the rating
    /// inferred from correctness and time, as a pick in practice and as an interview answer in a run.
    /// </summary>
    public Rating PracticePicked(string lessonId, string stepId, string? picked, bool correct, TimeSpan elapsed, PracticeMode mode, string run)
    {
        correct = correct && picked is not null;
        var rating = mode == PracticeMode.Interview
            ? RatingInference.InferInterview(correct, elapsed, RatingInference.PickThresholds.Fast)
            : RatingInference.InferPick(correct, elapsed);
        var answer = new PracticeAnswer(stepId, PracticePart.Pick, correct, picked, 0, elapsed, rating, mode, run) { Answered = picked is not null, Day = AnswerDay.Today(_clock) };
        Append(ProgressEventTypes.PracticeAnswered, lessonId, stepId, answer.ToJson());
        return answer.Rating;
    }

    /// <summary>
    /// Records the solution of a practice or interview problem, rated from correctness, hints and time as in Review in practice, and as
    /// an interview answer in a run. <paramref name="presented"/> is the form that was solved (the step or its phone alternative), which
    /// sets the time thresholds. Returns the rating.
    /// </summary>
    public Rating PracticeSolved(string lessonId, Step presented, string stepId, bool correct, int hintsUsed, TimeSpan elapsed, PracticeMode mode, string run)
    {
        var rating = mode == PracticeMode.Interview
            ? RatingInference.InferInterview(correct, elapsed, RatingInference.Thresholds(presented).Fast)
            : RatingInference.Infer(correct, hintsUsed, elapsed, presented);
        return AppendSolve(lessonId, new PracticeAnswer(stepId, PracticePart.Solve, correct, null, hintsUsed, elapsed, rating, mode, run));
    }

    private Rating AppendSolve(string lessonId, PracticeAnswer answer)
    {
        Append(ProgressEventTypes.PracticeAnswered, lessonId, answer.Step, (answer with { Day = AnswerDay.Today(_clock) }).ToJson());
        return answer.Rating;
    }

    /// <summary>
    /// Starts a timed interview run on <paramref name="problems"/> (from <see cref="PracticeSelector.Set"/>) and returns it. The clock
    /// starts now. Returns null, recording nothing, when another run is still open: at most one runs at a time. <paramref name="limit"/>
    /// is the clock: the preset's own when omitted, and required for <see cref="InterviewPreset.Custom"/>, whose problem count and limit
    /// must be <see cref="InterviewPresets.IsValidCustom">within its bounds</see>.
    /// </summary>
    public InterviewRun? StartInterview(InterviewPreset preset, IReadOnlyList<PracticeProblem> problems, TimeSpan? limit = null)
    {
        if (preset == InterviewPreset.Custom && (limit is not { } custom || !InterviewPresets.IsValidCustom(problems.Count, custom)))
            throw new ArgumentOutOfRangeException(nameof(limit), "A custom interview needs 1 to 5 problems and a limit of 10 to 90 whole minutes.");
        if (problems.Count == 0 || InterviewRuns.Open(log.ReadAll()) is not null) return null;
        var run = Guid.NewGuid().ToString("N");
        var set = problems.Select(p => new InterviewProblem(p.Lesson.Id, p.Step.Id, p.Choices.Select(c => c.Id).ToList())).ToList();
        Append(ProgressEventTypes.InterviewStarted, "", "", InterviewRuns.StartedJson(run, preset, set, limit ?? InterviewPresets.Limit(preset)));
        return InterviewRuns.Get(log.ReadAll(), run);
    }

    /// <summary>
    /// Ends an interview run: every part still unanswered is recorded as wrong and not <see cref="PracticeAnswer.Answered"/> (a pick of
    /// nothing, a failed solve, with no time spent), so the run is complete on every device but no review item moves, then InterviewFinished.
    /// <paramref name="steps"/> finds each problem's step: a solve is recorded only for a step that still exists. Does nothing for a run
    /// that is unknown or already finished, so two devices ending the same run add nothing that counts twice.
    /// </summary>
    public void FinishInterview(string runId, Func<string, Step?> steps)
    {
        if (InterviewRuns.Get(log.ReadAll(), runId) is not { IsFinished: false } run) return;
        foreach (var problem in run.Problems)
        {
            if (run.Answer(problem.StepId, PracticePart.Pick) is null)
                PracticePicked(problem.LessonId, problem.StepId, null, false, TimeSpan.Zero, PracticeMode.Interview, run.Id);
            if (run.Answer(problem.StepId, PracticePart.Solve) is null && steps(problem.StepId) is not null)
                AppendSolve(problem.LessonId, new PracticeAnswer(problem.StepId, PracticePart.Solve, false, null, 0, TimeSpan.Zero, Rating.Again,
                    PracticeMode.Interview, run.Id) { Answered = false });
        }
        Append(ProgressEventTypes.InterviewFinished, "", "", InterviewRuns.FinishedJson(run.Id));
    }

    /// <summary>Records the daily goal in steps (kept within <see cref="TodayState.MinGoal"/>–<see cref="TodayState.MaxGoal"/>). Returns the goal as stored.</summary>
    public int SetDailyGoal(int steps)
    {
        var goal = TodayState.ClampGoal(steps);
        Append(ProgressEventTypes.DailyGoalSet, "", "", JsonSerializer.Serialize(new { steps = goal }));
        return goal;
    }

    /// <summary>Records the start of a daily session that continues with <paramref name="lessonId"/> (null when there is no lesson left).</summary>
    public void StartSession(string? lessonId) => Append(ProgressEventTypes.SessionStarted, lessonId ?? "", "", null);

    /// <summary>Flags a step as confusing or wrong, with an optional note (blank notes are stored as none).</summary>
    public void StepFlagged(string lessonId, string stepId, string? note = null) =>
        Append(ProgressEventTypes.StepFlagged, lessonId, stepId,
            JsonSerializer.Serialize(new { note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() }));

    public void CompleteSession() => Append(ProgressEventTypes.SessionCompleted, "", "", null);

    private void Append(string type, string lessonId, string stepId, string? data) =>
        log.Append(new ProgressEvent(Guid.NewGuid().ToString("N"), deviceId, _clock.GetUtcNow(), type, lessonId, stepId, data));
}
