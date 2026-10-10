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
in Review, so a lesson attempt and a review attempt of the same step never blur together. The projector ignores `HintUsed`: it doesn't
change lesson progress, unlock reviews or touch a schedule.

## Rating impact

Review counts the hints a learner asked for **before their first answer** and passes the count to `RatingInference` (see [review.md](review.md)):

| Hints used | Rating of a correct answer |
|---|---|
| 0 | Easy / Good / Hard by time |
| 1–2 | Hard |
| 3 or more (partial code or the solution) | Again |

A wrong answer is Again whatever the hints. FSRS does the rest: less stability, and a card due sooner. For a first review, Hard and Again
both round to a one-day interval, so the difference shows in the stored stability and difficulty and in the second interval; against
an unaided Good or Easy answer the item comes back days sooner.

Hints asked after the first answer are practice: they are not recorded and don't change the rating that was stored.

## Not in this slice

- Hints used in a **lesson** are recorded but nothing reads them yet. A step solved only with the answer shown doesn't become a re-solve,
  and doesn't change the first review. Mastery ([#21](https://github.com/joeizang/brilliantApp/issues/21)) is the natural reader.
- No keyboard shortcut for *Need a hint?* yet.
- The *full solution* rung doesn't link to the trace player.
