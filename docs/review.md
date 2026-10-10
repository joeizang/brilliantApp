# Review: review items, FSRS scheduling, the queue and the Review screen

Issues [#18](https://github.com/joeizang/brilliantApp/issues/18) (PRD user stories 35, 38, 39, 71) and [#19](https://github.com/joeizang/brilliantApp/issues/19) (36, 37, 40).

## How it fits together

```
lesson.yaml  ──►  ReviewItem(id, concept, step)      declared by the lesson, re-asks one of its choice / predict-output steps
event log    ──►  ReviewAnswered events              one per first answer: item, correct, hints, time, inferred rating
projector    ──►  ReviewItemState(card, unlocked)    every answer folded through FSRS, in time order
queue builder ──► LearnerState.Queue                 today's due items: capped, overdue first, patterns weighted up
Review screen ──► Queue.Items                        answers append ReviewAnswered
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

Hints come from the [hint ladder](hints.md): each rung used before the first answer lowers the rating (1–2 hints → Hard, 3 or more → Again), and
Review stores that count in `hintsUsed`.
The rating is stored on the event so history replays identically if these thresholds are tuned later.

## The `ReviewAnswered` event

`StepId` is the question step; `Data` is `{ "item", "correct", "hintsUsed", "elapsedMs", "rating" }`. Events with
missing or malformed data are ignored: not JSON, not an object, an `item` that isn't a string, or a `rating` that isn't a JSON number from 1 to 4.
An unknown item is ignored too. One bad event can never stop the projector. Answers are ordered by `(OccurredAt, event ID)`, so identical
timestamps from two devices resolve the same way everywhere.

Review answers never touch lesson progress: they use a different event type, so lesson completion and unlocking are unaffected.

## The Review screen

- **Entry points:** a *Review · N due* card on the Tracks screen (or "No reviews due"), and a *Review* item with a due-count badge in the sidebar.
- **Queue:** `LearnerState.Queue.Items` (see below), snapshotted when the screen opens.
- **Answering:** only the first check of each item is recorded; trying again is practice. After it, the screen says when the item will return, and the sidebar's due count refreshes.
- **Question types:** choice and predict-output for authored items; fill-blank, Parsons and write-code for re-solves. Each is labelled when it isn't an ordinary concept question (*Pattern*, *Re-solve*).
- **Caught up:** shows when the next review is due. If today's cap held items back, it says how many are waiting for tomorrow instead.
- The Back shortcut returns to the Tracks screen, as it does from a lesson.

## The daily queue (`ReviewQueueBuilder`)

A pure function of `(review items, now, reviews answered today, options)`. The projector feeds it and exposes the result as
`LearnerState.Queue` (`Items`, `Waiting`, `DoneToday`).

1. **Due only.** Unlocked items that are new or past their due time.
2. **Learned before new.** Items that already have a schedule come first, then new ones.
3. **Most forgotten first.** Among scheduled items the order is `weight × (1 − retrievability)`, using FSRS's own estimate of
   how likely the learner is to still recall the item. A long-overdue item outranks a fresh one, and an item with a short
   memory outranks one with a long memory that is the same number of days late.
4. **Patterns weighted up.** Weights: pattern 2.0, re-solve 1.5, concept 1.0 (`ReviewQueueOptions`). The weight is a multiplier, not
   a guarantee: an authored pattern item that is just due ranks above a concept item that is a few days late, but not above one
   that has been forgotten for weeks. New items are ordered heaviest kind first, then in content order.
5. **Capped.** At most 20 reviews a day (`DailyCap`), *counting those already answered today*. Whatever doesn't fit is `Waiting`:
   still due, offered when room opens up (normally tomorrow), so a missed week is a normal day, not a mountain.

"Today" is the learner's day: `ProgressRecorder.Project` projects at local time and the projector counts answers whose local date
matches. (Events are stored in UTC; their local date is taken with the offset of the projection time.)

## Review item kinds

| Kind | Where it comes from | Question types |
|---|---|---|
| Concept | `reviewItems:` in `lesson.yaml` (the default) | choice, predict-output |
| Pattern | the same, with `kind: pattern` ("which pattern fits this problem?") | choice, predict-output |
| Re-solve | derived by the projector, never authored | write-code, fill-blank, Parsons |

A **re-solve** appears for each problem step (write-code, fill-blank, Parsons) that has at least one failed attempt in the log (a
`StepAnswered` with `correct: false`, or a `CodeSubmitted` that did not pass) **or** a hint used in the lesson (`HintUsed` with no review item; see [hints.md](hints.md)). Its ID is `resolve.<step id>`, it unlocks with its
lesson like any other item, and from then on it is scheduled by FSRS like any other item (answers are `ReviewAnswered` events
with that item ID). Wrong answers and hints given *during Review* are not lesson attempts, so they don't create more re-solves. A re-solve
retires if its problem step is deleted. A write-code re-solve keeps its own code draft (`review.<item id>.<answers so far>`), so it starts from
the starter instead of the solution the learner saved in the lesson, never overwrites that lesson draft, resumes if interrupted, and starts afresh at the next due attempt. Choice and predict-output steps never get re-solves: the lesson's own review items cover them.

The validator accepts `kind: concept` or `kind: pattern` and rejects anything else (including `resolve`). There are no pattern items
in Track 1 (it teaches Python syntax); the first arrive with the DSA tracks.

Mastery per concept and track, built on these review items, is in [mastery.md](mastery.md).

## Not in this slice

A per-learner cap or weights in settings (the options exist, the UI doesn't);
choosing an easier form of a re-solve on a phone, such as Parsons instead of typing code (story 43); streaks and the Today screen ([#22](https://github.com/joeizang/brilliantApp/issues/22)).
