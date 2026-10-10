# Mastery and the concept view

Issue [#21](https://github.com/joeizang/brilliantApp/issues/21), PRD stories 41 and 42. The point of mastery is the difference between
what you have **truly learned** and what you have **merely seen**. Finishing a lesson makes a concept *seen*. Only reviews that go well,
spaced further and further apart, make it *known*.

## The rule

Computed by the Learner State Projector (`MasteryModel`, `src/Brilliant.Core/Progress/Mastery.cs`) from the event log and the content.
Nothing is stored.

| Level | Meaning | Mastery |
|---|---|---|
| Not started | The lesson that teaches the concept isn't finished. | 0 |
| Seen | Lesson finished, but no review has been answered. | 0 |
| Learning | At least one review answered, mastery below 80%. | above 0 |
| Known | Mastery at or above 80% (`KnownThreshold`). | 80–100% |

**A review item** is worth `min(1, stability / 21 days) × retrievability now`.

- *Stability* is FSRS's estimate of how long the memory lasts. 21 days (`KnownStabilityDays`) counts as fully learned, so one good review
  scores about 15% and it takes a handful of on-schedule reviews to get past 80%.
- *Retrievability* is the chance you'd still recall it today. A concept you have stopped reviewing fades, and a lapse (an Again) cuts stability.
  Measured on its due date a well-learned item sits near 90%, the retention FSRS aims for, and reads higher just after a review.

**A concept** is the mean of its review items. An item never reviewed counts as zero, so a concept with two questions and one review is
half-way to where a single-question concept would be. A concept with *no* review items can be seen but never mastered, and its screen says so.
Re-solve items (the problems you got wrong or needed hints on) belong to no concept, so they don't move any concept's bar.

**A track** is the mean of its concepts, concepts you haven't reached counting as zero. That is deliberately harsher than "lessons
complete": the track bar says how much of the whole track you could use today.

Pure and order-independent like the rest of the projector: duplicate events and event order change nothing, and a deleted question step takes
its review history with it.

## Accuracy and history

A concept's evidence is its review items' question steps. Every answer to them counts:

- in the lesson that taught it: a `StepAnswered`, or a `CodeSubmitted` that passed every test;
- in Review: a `ReviewAnswered`.

The concept screen shows right-of-total for the lesson and for Review, an overall percentage (or "No answers yet"), and the full list newest
first. A review's *correct* flag is separate from its FSRS rating: a right answer with three hints is correct but rates Again.

## Where it shows

| Screen | What |
|---|---|
| Tracks | A green mastery bar under the lessons-complete bar of each track card. |
| Track | The track's mastery bar, then a *Concepts* list: title, level and bar. Tapping one opens it. |
| Concept | Level and mastery; the lesson that teaches it with *Review lesson* / *Continue lesson* / *Start lesson* (disabled while locked); accuracy; its review questions with when each is next due; the history. Back returns to the track. Leaving a lesson opened from here returns here. |

Bars are `role="meter"` with a percentage label, so a screen reader hears "Mastery of Lists 15 percent".

## API

`LearnerState` gains `Concepts` (`ConceptMastery`: concept, lesson, mastery, level, items, history, attempt counts, accuracy, next due),
`Concept(id)`, `ConceptsOf(trackId)` and `TrackMastery(trackId)`.

Tests: `tests/Brilliant.Core.Tests/MasteryTests.cs` and `tests/Brilliant.Lessons.UI.Tests/MasteryViewTests.cs`.

## Not in this slice

Mastery in the sidebar; a mastery-change summary at the end of a session (story 6); tuning the 21-day and 80% constants against real use;
a streak and daily goal ([#22](https://github.com/joeizang/brilliantApp/issues/22)).
