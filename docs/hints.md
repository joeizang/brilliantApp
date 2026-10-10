# Hints: the four-level ladder and its effect on Review

PRD stories 30 and 33 ([#20](https://github.com/joeizang/brilliantApp/issues/20)). Every question can offer help in rungs, so a learner
gets exactly as much as they need, and the help they took is remembered so the item comes back sooner.

```
lesson.yaml  step.hints[]   ──►  HintLadder (UI)        one rung per click, gentlest first
HintLadder   OnRevealed(n)  ──►  HintUsed event         { level, item? }, one per rung
ReviewView   hints before the first answer ──► RatingInference.Infer(hintsUsed) ──► ReviewAnswered { hintsUsed, rating } ──► FSRS
```

## Authoring

`hints:` on a choice, predict-output, write-code, fill-blank or parsons step, gentlest first, **at most four**. The convention is
*nudge → pattern hint → partial code → full solution*. Hints are markdown (raw HTML is dropped). A ladder may be shorter; the validator
only requires non-empty text. Explain steps take no hints.

## The ladder (`HintLadder.razor`)

- Closed to begin with: *Need a hint?* reveals the first rung, then *Show another hint*, then *Show the last hint*. The button goes away
  when every rung is shown. Rungs can't be skipped or revealed together.
- A full four-rung ladder labels its rungs *Nudge*, *Pattern hint*, *Partial code*, *Full solution*. A shorter ladder says *Hint n of N*,
  because we can't know which rung it left out.
- Each step starts with a closed ladder (the step is keyed in the viewer), and the ladder stays open across *Try again*.
- In Review, a caption says *A hint makes this come back sooner.*

## The `HintUsed` event

`StepId` is the question step; `Data` is `{ "level": 1-based rung, "item": review item ID or null }`. `item` is set only when the hint was asked
in Review, so a lesson attempt and a review attempt of the same step never blur together. Malformed events (no level, a level below 1,
a non-string item) are ignored. The projector reads them in two ways, both pure functions of the log:

- **Lesson hints** (`item` null) → `ReviewItemState.LessonHints`: the highest rung used on that step while learning it.
- **Review hints** → `ReviewItemState.AttemptHints`: the highest rung asked for that item *since its last `ReviewAnswered`*. That is the help already
  taken on the attempt in progress. Answering closes the attempt, so the next one starts clean.

They never change lesson progress or unlock anything.

## Rating impact

The hints counted against a review answer are `ReviewItemState.HintsBefore(askedThisAttempt)`: the most of

1. the hints asked on this attempt (live in the ladder, and those restored from before, see below), and
2. the **carried lesson hints**, on an item's *first* review only.

That count goes to `RatingInference` (see [review.md](review.md)) and is stored as `hintsUsed` on `ReviewAnswered`:

| Hints counted | Rating of a correct answer |
|---|---|
| 0 | Easy / Good / Hard by time |
| 1–2 | Hard |
| 3 or more (partial code or the solution) | Again |

A wrong answer is Again whatever the hints. FSRS does the rest: less stability, and a card due sooner. For a first review, Hard and Again
both round to a one-day interval, so the difference shows in the stored stability and difficulty and in the second interval; against
an unaided Good or Easy answer the item comes back days sooner.

**Interrupted attempts.** Hint use is read back from the log, not held in the screen, so leaving Review (or quitting the app) after revealing
hints and coming back can't erase them: the ladder reopens with those rungs shown and they still count. A write-code re-solve resumes the same way
as its code draft does.

**Hints asked after the first answer** are practice: not recorded, and they don't change the rating that was stored.

## Hints used in a lesson (story 33)

- **Re-solves.** A write-code, fill-blank or Parsons step that the learner used *any* hint on gets a re-solve (`resolve.<step id>`), exactly like one
  answered incorrectly, even if the first submission passed. It unlocks with the lesson and is scheduled by FSRS like any review item.
  Choice and predict-output steps get none: their authored review items already cover them.
- **First review.** For a concept or pattern item, the lesson hints on its question step count against its first review (the "carried" hints
  above), so a question the learner needed the answer for can't be rated Easy the first time it comes back. The re-solve is exempt: it is the
  test of that very problem and is judged on its own hints.
- Later reviews of an item look only at the hints asked in Review.

## Not in this slice

- No keyboard shortcut for *Need a hint?* yet.
- The *full solution* rung doesn't link to the trace player.
- Any hint, however small, creates a re-solve; if that turns out to be too many a threshold is one line in the projector.
