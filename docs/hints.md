# Hints: the four-level ladder and its effect on Review

PRD stories 30, 31 and 33 ([#20](https://github.com/joeizang/brilliantApp/issues/20), [#25](https://github.com/joeizang/brilliantApp/issues/25)). Every question can offer help in rungs, so a learner
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

**The full solution is a `walkthrough:`, not a hint.** A write-code, fill-blank, parsons or predict-output step may add one; it becomes the
last rung, and then `hints:` may hold **at most three** (the walkthrough is the fourth). Without a walkthrough the limit stays four.

```yaml
- id: step.functions.write-sum-to
  type: write-code
  # …prompt, entrypoint, solution, tests, hints…
  walkthrough:
    call: sum_to(4)            # required for write-code: how to run the solution so the trace has something to do
    visualise:                 # optional, same shape as a trace step's `visualise`
      - { variable: total, as: array }
    explanation: |
      `total` starts at 0 and each pass adds the next number…
```

- The code walked through is the step's own answer: the `solution` (write-code), the lines in solution order (parsons), the template with
  each blank's first accepted answer (fill-blank), or the step's `code` (predict-output, Python only). `call` is appended to it.
  Choice, trace and explain steps have no code, so they reject a walkthrough.
- `explanation` (markdown) is required: it says *why* it works, so peeking still teaches.
- `validate content` runs the walkthrough under CPython exactly like a trace step, so a broken one fails the build rather than the learner.
- The walkthrough ships in the Content Pack (an optional `walkthrough` on the step; the format version is unchanged). A write-code
  step's `solution` itself is still never shipped.

## The ladder (`HintLadder.razor`)

- Closed to begin with: *Need a hint?* reveals the first rung, then *Show another hint*, then *Show the last hint*. The button goes away
  when every rung is shown. Rungs can't be skipped or revealed together.
- A full four-rung ladder labels its rungs *Nudge*, *Pattern hint*, *Partial code*, *Full solution*. A shorter ladder says *Hint n of N*,
  because we can't know which rung it left out. The walkthrough is always labelled *Full solution*, and the button that reaches it reads
  *Show the full solution*.
- Opening the walkthrough shows the explanation beside the solution playing in the [Trace Player](trace-player.md) (`TraceRun` runs the code
  and builds the states, as a trace step does). Nothing runs until the learner asks for it.
- Each step starts with a closed ladder (the step is keyed in the viewer), and the ladder stays open across *Try again*.
- In Review, a caption says *A hint makes this come back sooner.*

## The `HintUsed` event

`StepId` is the question step; `Data` is `{ "level": 1-based rung, "item": review item ID or null }`. The walkthrough always reports **level 4**,
whatever its position, so a ladder with fewer than three text hints still counts opening the solution as "shown the answer". `item` is set only when the hint was asked
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
| 3 or more (partial code or the walkthrough) | Again |

A wrong answer is Again whatever the hints. FSRS does the rest: less stability, and a card due sooner. For a first review, Hard and Again
both round to a one-day interval, so the difference shows in the stored stability and difficulty and in the second interval; against
an unaided Good or Easy answer the item comes back days sooner.

**Solved with the solution.** This is how an item answered after peeking at the walkthrough is marked for rating: level 4 is at least 3, so
the answer is Again and the item comes back soon, even if the learner answers correctly straight afterwards. In a lesson it also creates a re-solve.

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
- Any hint, however small, creates a re-solve; if that turns out to be too many a threshold is one line in the projector.
