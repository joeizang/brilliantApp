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

### Practice and interview answers

Issue [#129](https://github.com/joeizang/brilliantApp/issues/129). Practice answers (`PracticeAnswered`, see [practice-mode.md](practice-mode.md))
schedule the same `pick.` and `resolve.` items as Review, with these rules:

| Where | Part | Rating |
|---|---|---|
| Practice | pick | `InferPick`: wrong → Again, ≤ 15 s → Easy, ≥ 60 s → Hard, else Good |
| Practice | solve | `Infer`, exactly as in Review (hints and the step type's thresholds) |
| Interview | pick or solve | `InferInterview`: wrong → Again, at or under the part's *fast* time → Easy, else Good |
| Interview | not answered when the run ended | recorded with `answered: false`; schedules nothing |

- **Interview answers are never Hard for time.** They are blind (no verdict until the end), with no hints, against one clock that
  already sets the pace (15 minutes a problem). A right answer under that pressure is at least as strong as an unhurried one, so only a
  quick one is lifted to Easy and nothing is marked down for being slow. The *fast* time is the pick's 15 s or the step type's (table above).
- **Unanswered is not wrong.** Running out of time, or never reaching a problem, says nothing about what the learner remembers. Rating it
  Again would throw away the item's stability. The part is still recorded (`answered: false`), so the run is complete on every device and
  the summary shows it, but the projector ignores it: it creates no `pick.`/`resolve.` item, moves none, and doesn't count as practised for
  [pattern mastery](mastery.md). Older events have no `answered` field: a pick with no `picked` was unanswered, anything else was answered.
- **Practice counts as fully as Review, once a day.** A right answer given before an item is due already grows it less, because FSRS's
  gain depends on how far recall had decayed, and answers under a day apart only adjust short-term stability. So a practice pick is not
  discounted. But a practice answer on the same local day as the item's previous answer (from Review or practice) is skipped by the
  projector: working the same problem in several practice visits in one evening is one day's evidence, not five pushes outward. A Review
  answer always counts, even after practice the same day, since Review is what the item's due date asks for.
  The day is the one each answer recorded (`day`, below), not one recomputed at replay, so the same log schedules the same on every
  device and in every time zone, and across a daylight-saving change.
- **Only an interview's first answer to a part counts.** The projector takes the first answer to each (run, problem, part), as the run
  summary does, before dropping unanswered ones. If one device ended a run with a part unanswered and another later synced an answer to
  it, the part stays unanswered in both the summary and the schedule.

Tests: `tests/Brilliant.Core.Tests/PracticeRatingTests.cs` (each rule replayed through the projector).

## The `ReviewAnswered` event

`StepId` is the question step; `Data` is `{ "item", "correct", "hintsUsed", "elapsedMs", "rating", "day" }`. `day` (`yyyy-MM-dd`, since #129) is the
learner's local date when answering, in the device's time zone; `PracticeAnswered` carries the same field. An event without a valid
`day` (written before it existed) counts on its UTC date. `day` is used only for the same-day practice rule. Events with
missing or malformed data are ignored: not JSON, not an object, an `item` that isn't a string, or a `rating` that isn't a JSON number from 1 to 4.
An unknown item is ignored too. One bad event can never stop the projector. Answers are ordered by `(OccurredAt, event ID)`, so identical
timestamps from two devices resolve the same way everywhere.

Review answers never touch lesson progress: they use a different event type, so lesson completion and unlocking are unaffected.

## The Review screen

- **Entry points:** a *Review · N due* card on the Tracks screen (or "No reviews due"), and a *Review* item with a due-count badge in the sidebar (the bottom tab bar on narrow screens).
- **Queue:** `LearnerState.Queue.Items` (see below), snapshotted when the screen opens.
- **Answering:** only the first check of each item is recorded; trying again is practice. After it, the screen says when the item will return, and the sidebar's due count refreshes.
- **Question types:** choice and predict-output for authored items; fill-blank, Parsons and write-code for re-solves. Each is labelled when it isn't an ordinary concept question (*Pattern*, *Re-solve*).
- **On a phone:** a re-solve of a step flagged `largeScreenPreferred` is presented as its Parsons or fill-blank alternative when it has one, else with a "best on a larger screen" note. Only the presented question changes, not the item, its schedule or the event; see [responsive-layouts.md](responsive-layouts.md).
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
| Pattern pick | derived from practice, never authored (`pick.<step id>`, kind Pattern) | the problem with a choice of patterns |

A **re-solve** appears for each problem step (write-code, fill-blank, Parsons) that has at least one failed attempt in the log (a
`StepAnswered` with `correct: false`, or a `CodeSubmitted` that did not pass) **or** a hint used in the lesson (`HintUsed` with no review item; see [hints.md](hints.md)). Its ID is `resolve.<step id>`, it unlocks with its
lesson like any other item, and from then on it is scheduled by FSRS like any other item (answers are `ReviewAnswered` events
with that item ID). Wrong answers and hints given *during Review* are not lesson attempts, so they don't create more re-solves. A re-solve
retires if its problem step is deleted. A write-code re-solve keeps its own code draft (`review.<item id>.<answers so far>`), so it starts from
the starter instead of the solution the learner saved in the lesson, never overwrites that lesson draft, resumes if interrupted, and starts afresh at the next due attempt. Choice and predict-output steps never get re-solves: the lesson's own review items cover them.

Practice ([practice-mode.md](practice-mode.md)) adds to this. Solving a problem in practice or an interview also creates its re-solve,
and naming the pattern of a tagged problem creates a **pattern pick**, `pick.<step id>`. Their practice answers are
`PracticeAnswered` events, folded into the same FSRS schedule as the item's `ReviewAnswered` answers, in time order. They don't count
toward the daily review cap or the daily goal.

The validator accepts `kind: concept` or `kind: pattern` and rejects anything else (including `resolve`). There are no pattern items
in Track 1 (it teaches Python syntax); the first arrive with the DSA tracks.

Mastery per concept and track, built on these review items, is in [mastery.md](mastery.md).

## Not in this slice

A per-learner cap or weights in settings (the options exist, the UI doesn't). Streaks and the Today screen landed in [#22](https://github.com/joeizang/brilliantApp/issues/22); see [today.md](today.md).
