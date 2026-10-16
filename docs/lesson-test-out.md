# Per-lesson test-out

Issue [#146](https://github.com/joeizang/brilliantApp/issues/146), PRD user stories 19–26, plan decision 7 in [csharp-app-plan.md](csharp-app-plan.md). An engine feature: every app gets it.

At the start of a lesson the learner can choose **I know this — test me**. They answer 3–4 of the lesson's own review questions. All right on the first try completes the lesson and starts its review items off as already known. Any miss ends the test, records nothing, and the lesson starts as normal.

## What is asked (`LessonTestOut.Select`)

A pure function of the lesson, so every device and every attempt asks the same questions.

- The candidates are the lesson's **concept** review items whose question can be asked in a lesson screen (choice, predict-output, fill-in-the-blank, Parsons, write-code). Two items on one question count once.
- With fewer than **3** candidates the lesson has too little to test: no test-out is offered. With 3 or more, it asks up to **4**.
- "Hard" means the form of the question: writing code (5) > Parsons (4) > fill-in-the-blank (3) > predict-output (2) > choice (1). The four hardest are chosen, ties going to the earlier item, and are asked in the lesson's own order.

`LessonTestOut.IsOffered(progress)` also requires that the lesson is not complete and that no step has been played yet: once the learner has started, skipping ahead is moot.

## What a pass records

One lesson-level event, `LessonTestedOut` (empty StepId, data `{ "items": [<ids asked>] }`), written by `ProgressRecorder.LessonTestedOut`. It is recorded only for a perfect attempt. **A miss writes nothing**, not even the answers, so it cannot reach the schedule, mastery or the daily goal. The questions are not recorded as `ReviewAnswered` for the same reason.

It syncs and replays like every other event: sync carries event types generically, and `TwoDeviceConvergenceTests` covers a test-out made on one device appearing, seeded identically, on the other.

## What it changes in the projection

- **Completion.** `LessonProgress.TestedOut` makes `IsComplete` true, so the lesson shows as Completed and the next one unlocks. The steps were skipped, not played: `NextStep` is null and `NewSteps` is empty, so the lesson player shows "You tested out of this lesson" and the track screen shows plain "Completed", not "N new steps". No `StepCompleted` events exist, so the daily goal doesn't move. `RecordCompletionsFrom` does not add a `LessonCompleted` for it (that would turn every skipped step into a "new" one). If the same lesson is also played to the end (say on another device), `LessonCompleted` is recorded and it is judged like any other lesson from then on.
- **Review items.** Each of the lesson's declared review items without a schedule gets `FsrsScheduler.Known(at)`: stability 30 days, the difficulty of a first "Easy", first review about a month out, `Answers` still 0. The seed sits in the time-ordered answer fold at the moment of the event, so an answer made before it keeps its real schedule (a test-out never overwrites what the learner did) and answers after it continue from the seed through ordinary FSRS. The earliest `LessonTestedOut` of a lesson is the one that counts, so every device seeds at the same instant.
- **Mastery.** Stability 30 days is past the 21 days the mastery model treats as fully learned, so the lesson's concepts read **Known** straight away. They fade like any other if left alone (retrievability falls with time), and a lapse in Review drags them down like any other.

## UI

`LessonViewer` shows the offer above the first step when `IsOffered`; it is never shown in Review mode. `TestOutView` asks the questions one at a time with the same step views Review uses (in their phone forms where the screen calls for it), with no hints. The first wrong answer swaps the question for "Not this time, and that's fine. Nothing is held against you" and a **Start the lesson** button. A pass records the event and the viewer shows the finished screen. Because a miss records nothing, the offer is still there if the learner backs out before playing a step.
