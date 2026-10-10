# Learner State Projector (issue #17)

Progress is never stored. It is **derived** from the append-only event log every time it is needed:

```csharp
LearnerState state = LearnerStateProjector.Project(events, contentGraph, now);   // pure, deterministic
LessonProgress one = LearnerStateProjector.ProjectLesson(lesson, events);        // one lesson only
state.Lesson("lesson.lists")?.Status;                                            // Locked | Available | InProgress | Completed
```

`ProgressRecorder.Project(content)` supplies the log and its clock, so screens call that one method. Nothing else
in the app computes lesson progress, unlocking or completion.

## Guarantees
- **Deterministic:** the same events, content and `now` always give the same state. `now` is carried on the state as `AsOf`; later work (streaks, due reviews) builds on it.
- **Order-independent and idempotent:** events are de-duplicated by event ID and only read as sets, so a sync merge that interleaves two devices' logs differently, or delivers an event twice, changes nothing.
- **Read-only:** projecting never appends. Review mode and replaying a completed lesson leave the log untouched.

## When content changes
Progress attaches to stable step and lesson IDs, never to positions.

| Content change | Result |
|---|---|
| Step edited (same ID) | History kept. It stays complete. |
| Steps reordered | No effect. |
| Step deleted | Retired: its events stay in the log, listed in `RetiredStepIds`, but no longer count. Deleting the last unfinished step completes the lesson. That completion has no event of its own, so `ProgressRecorder.RecordCompletionsFrom` appends `LessonCompleted` for it (called from `CourseShell.OnParametersSet`, so on every content load). Without it, a step added later would silently un-complete the lesson. |
| Lesson deleted | Its events are ignored. |
| Step added to an **unfinished** lesson | Just another remaining step. |
| Step added to a **finished** lesson | The lesson stays `Completed`, the next lesson stays unlocked, and the step is listed in `NewSteps`. The track screen shows "N new steps" with a **Play new step(s)** button and a secondary **Review** button, and the lesson player offers only those steps. Finishing them clears the marker and does not record `LessonCompleted` again. |

`LessonProgress.CurrentStep` is `null` for a finished lesson; `NextStep` is the first step without a `StepCompleted`
event either way, which is what the lesson player uses.

Tests: `tests/Brilliant.Core.Tests/LearnerStateProjectorTests.cs` and `tests/Brilliant.Lessons.UI.Tests/NewStepsTests.cs`.
