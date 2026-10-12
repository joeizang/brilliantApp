# Practice mode and timed interview simulation

Issue [#36](https://github.com/joeizang/brilliantApp/issues/36) (PRD milestone M4). **Status: design, agreed in outline with Joseph; this document is for review before anything is built.**

The skill the app exists to train is the one the learner fails interviews on: looking at a problem with no label on it and knowing which pattern it is. Lessons name the pattern in their titles. Practice takes the names away.

## Decisions

| Question | Decision |
|---|---|
| Where do problems come from? | **Existing problem steps** in lessons the learner has completed. No new authored problem format in this slice. |
| How is the pattern named? | **Pick, then solve.** The learner first chooses the pattern from the ones they have learned, then solves the problem. The two results are recorded separately. |
| Where does it live in the UI? | A **Practice card on the Tracks screen**. No new tab, nothing on Today yet. |
| What is the interview mode? | A **fixed set of problems against one clock**, no hints, no leaving to a lesson. |
| How do results feed Review? | Through **the same review items and the same FSRS scheduler**. A practice answer is a review answer. |
| Hints? | In **Practice**, the usual hint ladder, counted exactly as in Review. In **Interview**, none. |
| How are problems chosen? | **Weakest first, from completed lessons**, shuffled within the weakest band. |
| If the app is closed mid-interview? | **The clock keeps running.** Reopening resumes with the real time left, or shows the summary if time ran out. |

## What exists today, and the gap

Lessons declare `concepts` and `reviewItems` ([review.md](review.md)). The capstone lesson already has `kind: pattern` choice questions ("which pattern fits?"), but **nothing in the content model says which pattern a problem step belongs to**. The PRD (story 71) says lessons declare the patterns they teach; that was never built. Practice needs it, so the first slice adds it:

```yaml
patterns:                       # on a lesson: patterns this lesson teaches
  - id: pattern.prefix-sums
    title: Prefix sums
steps:
  - id: step.prefix.write-range-sum
    type: write-code
    pattern: pattern.prefix-sums   # on a problem step: which pattern solves it
```

- A pattern ID is declared by exactly one lesson (the one that teaches it). The validator rejects an unknown or duplicated pattern ID, and a step whose `pattern` is declared by a *later* lesson in the same track.
- Only `write-code`, `fill-blank` and `parsons` steps can carry `pattern`; those are the steps a learner can *solve*.
- The validator **warns** when a tagged step's prompt contains its pattern's title, since that would give the answer away in Practice.
- The eight patterns in Tracks 1 and 2 (hash-map lookup, counting and frequency, grouping by key, prefix sums, converging pointers, reader and writer, fixed window, variable window) get declared, and each existing problem step that is clearly one of them gets tagged. Steps that are not about a pattern (Python syntax drills) are left untagged and never appear in Practice.

## Practice

**Entry.** A *Practice* card on the Tracks screen, next to the Review card. It is disabled, with a one-line reason, until the learner has completed lessons that teach **at least three patterns** and a tagged problem exists in them (with fewer than three there is nothing to choose between).

**Choosing problems.** Eligible: tagged problem steps in `Completed` lessons. They are ranked by weakness, then one is picked at random from the weakest few so the set changes from visit to visit:

1. a step whose pick or solve was last answered wrong, or has a re-solve item due;
2. steps never practised;
3. steps on the learner's lowest-mastery patterns ([mastery.md](mastery.md));
4. the rest, least recently practised first.

The same problem is not offered twice in a row. This is a pure function (`PracticeSelector`) of the completed lessons, the log and a random seed, so it is unit-testable.

**The flow, for one problem.**

1. **Unlabelled problem.** The prompt only. The step title is replaced with "Problem". Nothing names the pattern or the lesson.
2. **Pick the pattern.** A choice of four: the right pattern plus three others the learner has learned (all of them if fewer than three others), in random order. After the answer, the feedback names the cue in the prompt that points to the right pattern. A wrong pick does not stop the learner: they still solve it, so the solving skill is trained either way.
3. **Solve it.** The normal step UI (write-code with the Pyodide runner, fill-blank, Parsons). The **hint ladder is available only after the pick is locked**, because the pattern-hint rung would reveal the pick. Hints used lower the rating exactly as in Review ([review.md](review.md#rating-inference-ratinginferenceinfer)).
4. **Result.** Two ticks or crosses (pattern, solution), the time taken, and when each will come back in Review. Then *Next problem* or *Finish*.

A session has no fixed length; the learner stops when they like. The Practice card shows nothing about a streak: practice is not part of the daily goal in this slice.

## Timed interview simulation

**Setup.** Two presets, *Phone screen* (2 problems, 30 minutes) and *Interview* (3 problems, 45 minutes). The problems are chosen like Practice, preferring a different pattern for each. Starting asks for confirmation ("The clock starts now and can't be paused").

**During the run.**
- One countdown for the whole run, always visible, turning urgent in the last 5 minutes.
- Each problem is the same pick-then-solve flow, but **no hints, no feedback, no "Check" results beyond the code runner's test output** (as in a real exercise, the learner can run their code against the visible tests). The pattern pick is locked on submit and its correctness is not shown until the end.
- The learner can move between the run's problems freely and submit each when ready. *Finish early* ends the run.
- Leaving the screen, switching apps or closing the app does **not** pause anything.

**Ending.** The run ends when the clock reaches zero, the learner finishes early, or every problem is submitted. An unsubmitted problem counts as unanswered (a failed pick and a failed solve).

**The summary.** For each problem: the pattern it was, what the learner picked, whether the solution passed, and the time used. Then a one-line read on the weakest pattern of the run and a note that the results are now in Review. This is the one place the answers are revealed.

**Interruption.** The run is stored as events, so it survives a closed app and syncs like everything else:

- `InterviewStarted` `{ run, preset, steps[], limitSeconds }` — the problem set and the start time are fixed here.
- `InterviewFinished` `{ run }` — written when the run ends by any route.

The time left is always `startedAt + limit − now`. If the app is reopened with time left, the run resumes; with none left, the summary is shown and any unanswered problems are recorded. A run is *open* until a `InterviewFinished` exists for it, and at most one run is open at a time. Because the clock is wall time, a device with a wrong clock would shorten or lengthen a run; that is accepted for a personal tool.

## How results feed Review

A practice answer is a `ReviewAnswered` event on a review item, scored by the same rating inference (wrong → Again, hints and time as today) and scheduled by the same FSRS.

| Practice result | Review item it updates | Kind (queue weight) |
|---|---|---|
| The pattern pick | `pick.<step id>` — "which pattern fits this problem?" | Pattern (2.0) |
| The solution | `resolve.<step id>` | Re-solve (1.5) |

Both items are **derived**, never authored, like re-solves today. Differences from today's rules, which the implementation must change in `LearnerStateProjector`:

1. A `resolve.<step>` item currently exists only if the lesson attempt failed or used a hint. It now **also exists once the step has been practised**, so a correct practice solve starts its own schedule. A `pick.<step>` item exists once the step has been practised.
2. Practice and interview answers are written as a new event type, `PracticeAnswered` `{ step, part: "pick"|"solve", correct, hintsUsed, elapsedMs, rating, mode, run? }`, which the projector folds through FSRS like `ReviewAnswered` but which is ignored by lesson progress. (A separate event keeps the log honest about where an answer came from, and keeps today's `ReviewAnswered` meaning unchanged.)
3. Only the **first** answer to a part in a given practice visit or interview run is scheduled; retrying is practice, as in Review.
4. Interview unanswered problems are recorded as `Again` for both parts when the run ends.

Practice never changes lesson completion or unlocking. Mastery ([mastery.md](mastery.md)) is built on review items, so practice moves it automatically.

In Review, a `pick.` item is shown as the same unlabelled problem with the pattern choice. A `resolve.` item behaves as today.

## Not in this slice

- Standalone problems authored for Practice (a `practice` content type with its own statement, tests and pattern tag). The tagging above is what that would build on; it can be a later issue.
- Per-problem timers, a custom time limit, difficulty levels, a practice streak or a Today suggestion.
- Interview mode on a phone is supported, but code-heavy problems keep the existing "best on a larger screen" note ([responsive-layouts.md](responsive-layouts.md)).
- Tuning FSRS for practice answers.

## Proposed slices

1. **Content model**: `patterns:` on lessons, `pattern:` on problem steps, validator rules and the warning, tags on the existing Tracks 1–2 content, and a note in the content-authoring docs.
2. **Practice core** (pure, Brilliant.Core): `PracticeSelector`, the `PracticeAnswered` event and the projector changes for `pick.` and `resolve.`, with tests.
3. **Practice UI**: the card, the pick-then-solve flow, results, `pick.` items in Review.
4. **Interview mode**: `InterviewStarted` / `InterviewFinished`, the clock, resume, the summary.

Each lands as its own PR; slices 2–4 depend on 1.
