# Review: review items, FSRS scheduling and the Review screen

Issue [#18](https://github.com/joeizang/brilliantApp/issues/18), PRD user stories 35, 38, 39 and 71.

## How it fits together

```
lesson.yaml  ──►  ReviewItem(id, concept, step)      declared by the lesson, re-asks one of its choice / predict-output steps
event log    ──►  ReviewAnswered events              one per first answer: item, correct, hints, time, inferred rating
projector    ──►  ReviewItemState(card, unlocked)    every answer folded through FSRS, in time order
Review screen ──► LearnerState.DueReviews            unlocked items that are new or due; answers append ReviewAnswered
```

Nothing about a schedule is stored. It is always recomputed from the log by `LearnerStateProjector`, so two devices
holding the same merged log agree on what is due.

## Unlocking

A review item is `IsUnlocked` only when its lesson's status is `Completed`. Until then it is neither new nor due. Finishing the
lesson makes all of its items appear as *new*. A review item added to an already finished lesson shows up as new too, and a
review item whose question step is deleted is retired along with its history (the PRD's "deleted steps retire their review items").

## FSRS (`Brilliant.Core.Review.FsrsScheduler`)

FSRS-5 with its published default parameters (19 weights), 90% desired retention, intervals rounded to whole days and
clamped to 1…36500 days. There is no per-learner optimisation in v1. Pure: `Schedule(card?, rating, now) → card`.

- First review: stability `w[rating-1]`, difficulty `w4 - e^(w5·(g-1)) + 1`.
- Later review after at least a day: recall grows stability (hard penalty `w15`, easy bonus `w16`, more for later reviews);
  a lapse (`Again`) resets it to at most the old stability.
- Later review within a day: the short-term rule `S·e^(w17·(g-3+w18))`.
- Difficulty is damped near 10 and reverts toward the difficulty of a first `Easy`.

The unit tests use numbers worked out independently from the published formulas, not copied from the code.
Reference: [Implementing FSRS in 100 Lines](https://borretti.me/article/implementing-fsrs-in-100-lines) (the FSRS-5 equations and default weights).

Because the minimum interval is one day, an item answered `Again` comes back tomorrow, not later in the same session.
Bringing wrong answers back sooner is the Review Queue Builder's job ([#19](https://github.com/joeizang/brilliantApp/issues/19)).

## Rating inference (`RatingInference.Infer`)

The learner never grades themselves. `Infer(correct, hintsUsed, elapsed, step)`:

| Situation | Rating |
|---|---|
| Wrong | Again |
| Correct but 3+ hints (partial code or the solution was shown) | Again |
| Correct after 1–2 hints | Hard |
| Correct, no hints, at or under the "fast" time for the step type | Easy |
| Correct, no hints, at or over the "slow" time | Hard |
| Otherwise | Good |

| Step type | Fast | Slow |
|---|---|---|
| choice | 6 s | 30 s |
| predict-output | 10 s | 60 s |
| fill-blank | 20 s | 90 s |
| parsons | 30 s | 150 s |
| write-code | 90 s | 420 s |

The hint ladder arrives with [#20](https://github.com/joeizang/brilliantApp/issues/20); until then Review records `hintsUsed = 0`.
The rating is stored on the event so history replays identically if these thresholds are tuned later.

## The `ReviewAnswered` event

`StepId` is the question step; `Data` is `{ "item", "correct", "hintsUsed", "elapsedMs", "rating" }`. Events with
missing or malformed data, or an unknown item, are ignored. Answers are ordered by `(OccurredAt, event ID)`, so identical
timestamps from two devices resolve the same way everywhere.

Review answers never touch lesson progress: they use a different event type, so lesson completion and unlocking are unaffected.

## The Review screen

- **Entry points:** a *Review · N due* card on the Tracks screen (or "No reviews due"), and a *Review* item with a due-count badge in the sidebar.
- **Queue:** `DueReviews`, snapshotted when the screen opens. Items already scheduled come first, most overdue first; new items follow in content order.
- **Answering:** only the first check of each item is recorded; trying again is practice. After it, the screen says when the item will return.
- **Question types:** choice and predict-output, the only types a review item may reference today (the validator enforces it).
- **Caught up:** shows when the next review is due.
- The Back shortcut returns to the Tracks screen, as it does from a lesson.

## Not in this slice

Daily cap, pattern weighting and re-solve items ([#19](https://github.com/joeizang/brilliantApp/issues/19)); hints ([#20](https://github.com/joeizang/brilliantApp/issues/20));
mastery and concept views ([#21](https://github.com/joeizang/brilliantApp/issues/21)); streaks and the Today screen ([#22](https://github.com/joeizang/brilliantApp/issues/22)).
