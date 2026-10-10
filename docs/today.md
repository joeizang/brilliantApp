# Today: daily session, goal and streak (issue #22)

Today is the app's home screen. It answers "what should I do now?" with one button.

## What the learner sees

- **Today screen** (`TodayView`): the streak, "X of Y steps" with a progress bar, a stepper to change the goal, the review count, the next lesson, and one primary button: *Start today's session*, *Resume session* or (goal met, nothing open) *Do another session*. *Browse tracks* opens the old Tracks list. An open session always keeps its *Resume session* button, even if the last step or answer used up all the work, so its summary can still be reached. When nothing is due and every lesson is finished, it says so.
- **Daily session** (`SessionRunner`): Review first (if anything is due), then the current lesson, then the summary. Leaving part-way and pressing *Resume session* returns to the same phase and the same step.
- **End-of-session summary** (`SessionSummaryView`): steps, reviews, accuracy, mastery changes (before → after, largest first), goal progress and streak.
- **Navigation**: Back climbs lesson → track → Tracks → Today. The sidebar has a Today item with the streak.

## Rules (all in `Brilliant.Core`, tested)

| Thing | Rule |
|---|---|
| Goal | In steps; default 10, clamped 1–500; the UI steps by 5 within 5–100. Recorded as `DailyGoalSet`, latest wins. |
| A step | A distinct `(lesson, step)` completion per local day, plus each valid `ReviewAnswered`. |
| Goal for a day | The latest goal set on or before that day, else the default. Raising the goal today doesn't rewrite past days. |
| Streak | Consecutive days that met their goal, ending today if today is met, else yesterday (so the day isn't lost until it ends). |
| Next lesson | First in-progress lesson in track order, else first available. |
| Session | `SessionStarted` today with no later `SessionCompleted`. Which phase it is in is derived, never stored. |
| Summary | `LearnerStateProjector.Summarize(events, content, since, until)`: pure; mastery changes compare the projection before `since` with the one at `until`. |

## Events added

`DailyGoalSet {steps}`, `SessionStarted` (LessonId = the lesson it continues with, empty if none), `SessionCompleted`. Everything else (streak, progress, phase) is projected from the existing log, so nothing syncs differently.

## Phases on (re)opening a session

Review if the queue is non-empty and the learner hasn't completed a step of the session lesson since the session began; else Lesson if it isn't completed; else Summary (recording `SessionCompleted` once).

## Not in this slice

Goal in minutes, notifications/reminders, streak freezes, and a history of past sessions.
