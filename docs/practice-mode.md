# Practice mode and timed interview simulation

Issue [#36](https://github.com/joeizang/brilliantApp/issues/36) (PRD milestone M4). **Status: design agreed (PR #93). Slices 1 (content model) and 2 (practice core) are built; slices 3–4 follow ([Proposed slices](#proposed-slices)).**

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

### As built (slice 1)

`Pattern(Id, Title)` is a content item like a concept (`pattern.` IDs, in the Content Graph and the pack), `Lesson.Patterns` holds a
lesson's declarations and `Step.Pattern` a problem's tag. The pack format is unchanged (version 1): both fields are optional, so an
older pack loads with no patterns and an older app ignores them. A pack whose step names a pattern it doesn't contain is refused on load.

The validator checks the tags once every lesson is loaded, so a step can name a pattern from an earlier lesson or from another track.
It rejects, with the file and step: a pattern no lesson declares, one taught by a *later* lesson of the step's own track, a `pattern.`
ID declared twice, a pattern without a title, `pattern` on anything but write-code, fill-blank or parsons, `pattern` on a phone
alternative (it takes its step's), and an empty `pattern`. A prompt containing its pattern's title (case and line breaks ignored), in
the step or its phone alternative, is a **warning**: `brilliant validate`, `pack` and `publish` print `warning: …` and still succeed.

| Pattern | Declared by | Tagged steps |
|---|---|---|
| `pattern.hash-map-lookup` (Hash-map lookup) | Track 1 lesson 7 | its 4 problems; capstone pair-difference and count-pairs; Track 2 lesson 6 unsorted two-sum |
| `pattern.counting` (Counting and frequency) | Track 1 lesson 8 | its 4 problems; capstone first-unique and top-k |
| `pattern.grouping-by-key` (Grouping by key) | Track 1 lesson 9 | its 4 problems; capstone anagram groups |
| `pattern.prefix-sums` (Prefix sums) | Track 1 lesson 10 | its 4 problems; capstone count-splits |
| `pattern.converging-pointers` (Two pointers from both ends) | Track 2 lesson 1 | its 5 problems; the 5 palindrome problems of lesson 2; lesson 6 three-sum (fill and write) |
| `pattern.reader-writer` (A reader and a writer) | Track 2 lesson 3 | its 6 problems |
| `pattern.fixed-window` (Fixed-size sliding window) | Track 2 lesson 4 | its 6 problems |
| `pattern.variable-window` (Variable-size sliding window) | Track 2 lesson 5 | its 5 problems |

Left untagged: every problem in Track 1 lessons 1–6 (Python and Big-O drills), Track 2 lesson 6's `count_subarrays` (its prompt says
"prefix sums and a dict" on purpose, to teach when a window fails) and `count_palindromes` (expand-around-centre, not one of the eight).
The remove-duplicates prompt said "using a reader and a writer"; it now says "without building a second list".

## Practice

**Entry.** A *Practice* card on the Tracks screen, next to the Review card. It is disabled, with a one-line reason, until the learner has completed lessons that teach **at least three patterns** and a tagged problem exists in them (with fewer than three there is nothing to choose between).

**Choosing problems.** Eligible: tagged problem steps in `Completed` lessons. They are ranked by weakness, then one is picked at random from the weakest few so the set changes from visit to visit:

1. a step whose pick or solve was last answered wrong, or has a re-solve item due;
2. steps never practised;
3. steps on the learner's lowest pattern mastery ([see Mastery below](#mastery));
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

A practice answer is scheduled on a review item, scored by the same rating inference (wrong → Again, hints and time as today) and scheduled by the same FSRS.

| Practice result | Review item it updates | Kind (queue weight) |
|---|---|---|
| The pattern pick | `pick.<step id>` — "which pattern fits this problem?" | Pattern (2.0) |
| The solution | `resolve.<step id>` | Re-solve (1.5) |

Both items are **derived**, never authored, like re-solves today. Differences from today's rules, which the implementation must change in `LearnerStateProjector`:

1. A `resolve.<step>` item currently exists only if the lesson attempt failed or used a hint. It now **also exists once the step has been practised**, so a correct practice solve starts its own schedule. A `pick.<step>` item exists once the step has been practised.
2. Practice and interview answers are written as a new event type, `PracticeAnswered` `{ step, part: "pick"|"solve", correct, picked?, hintsUsed, elapsedMs, rating, mode, run? }`, which the projector folds through FSRS like `ReviewAnswered` but which is ignored by lesson progress. (A separate event keeps the log honest about where an answer came from, and keeps today's `ReviewAnswered` meaning unchanged.)
3. Only the **first** answer to a part in a given practice visit or interview run is scheduled; retrying is practice, as in Review.
4. Interview unanswered problems are recorded as `Again` for both parts when the run ends.

**The chosen pattern is persisted.** A `pick` answer carries `picked`: the stable ID of the pattern the learner chose, so two different wrong choices are distinguishable. A pick that was never made (an interview problem left unanswered) is written by the finishing step with `picked: null` and `correct: false`. Anything that shows a pick reads it from the log, never from UI state:

- the interview summary shows "you picked X, it was Y" from the `pick` answer for that run and step, and "no answer" when `picked` is `null`;
- resuming an open run restores each problem's locked pick and any submitted solution from the run's `PracticeAnswered` events, so a reopened or synced run looks the same on any device;
- if the same part has several answers in a run (a retry), the first is the scheduled and displayed one, matching rule 3.

Practice never changes lesson completion or unlocking.

### Mastery

Practice does **not** move the existing concept and track mastery bars. Those are the mean of a concept's *authored* review items; derived `resolve.` items belong to no concept and are excluded by design ([mastery.md](mastery.md)), and `pick.` items are the same. Pretending otherwise would leave the bars unchanged while the docs promised they moved. Instead practice gets its own measure, **pattern mastery**:

- A step's score is the mean of its two items' mastery (`min(1, stability / 21 days) × retrievability now`, the item rule in [mastery.md](mastery.md)), a part never practised counting as zero.
- A pattern's mastery is the mean of the scores of the tagged problem steps of that pattern in completed lessons, so one practised problem out of five is a fifth of the way, as with concepts. A pattern with no tagged steps in completed lessons has no value and is never offered.
- It is a pure projection, like concept mastery: `LearnerState.Patterns` (`PatternMastery`: pattern, lesson, mastery, steps practised, steps tagged), computed by the projector from the log and the content, with the same level names (not started, seen, learning, known).
- It is what the selector's "lowest-mastery patterns" ranking reads, and what the interview summary and the Practice card show ("Prefix sums: learning, 34%").

Concept mastery is unchanged by this feature. Linking a pattern to a concept so that practice also lifts the concept bar is a possible later step, not part of this one.

In Review, a `pick.` item is shown as the same unlabelled problem with the pattern choice. A `resolve.` item behaves as today.

### As built (slice 2)

Pure code in `Brilliant.Core` (`src/Brilliant.Core/Practice/`); nothing on screen yet. Tests: `tests/Brilliant.Core.Tests/PracticeTests.cs`.

**The event.** `ProgressRecorder.PracticePicked(lesson, step, picked, correct, elapsed, mode, run)` and
`PracticeSolved(lesson, presented, step, correct, hints, elapsed, mode, run)` write `PracticeAnswered`, with the step and lesson IDs on
the event as usual and `{ step, part, correct, picked, hintsUsed, elapsedMs, rating, mode, run }` as data (`PracticeAnswer`).
`run` names the practice visit or the interview run; slice 3 gives each visit a fresh ID. A pick with `picked: null` is
always wrong. A solve is rated like Review, from the form actually solved (a phone alternative has its own time thresholds). A pick has
no hints and is rated by time alone (`RatingInference.InferPick`): it involves reading an unlabelled problem first, so it gets
longer than a choice question, Easy under 15 seconds and Hard from a minute. A malformed event, or one whose `step` disagrees
with its `StepId`, is ignored.

**The projector.**
- `resolve.<step>` exists once the problem was failed or hinted in its lesson (as before) **or solved** in practice.
- `pick.<step>` (kind Pattern, so weight 2.0 in the queue) exists once the step was **picked** in practice and is still tagged. Untagging it retires the item.
- A pick alone doesn't create the re-solve, so abandoning a problem after naming its pattern leaves nothing new due in Review.
- Answers from both events are folded through FSRS in time order. Within one `run`, only the first answer to each part of each problem counts.
- Practice changes no lesson progress, daily goal, daily review cap or concept mastery.
- Help asked during practice is recorded with the item ID (`HintUsed` with `item: "resolve.<step>"`), so it is never mistaken for lesson help.
- `ReviewItemState.LastRating` now holds the rating of the latest answer.

**Pattern mastery** is described in [mastery.md](mastery.md#pattern-mastery).

**The selector.** `PracticeSelector.Availability(state)` reports the learned patterns and the problems on offer. Practice is available
with 3 or more learned patterns and at least one problem. A pattern counts as *learned* when its teaching lesson is completed. A problem is on offer when it is tagged, its lesson is completed, **and its
pattern is learned**, so the right answer is always one the learner was taught (a later track's lesson can use an earlier track's
pattern before that track is finished). `Next(state, random, previousStepId)` ranks the problems into tiers:

1. needs work: the pick or re-solve was last rated Again, or the re-solve is due;
2. never practised;
3. the rest.

Within a tier it orders by lowest pattern mastery, then least recently practised, then content order. It draws at random from the first
three of the weakest non-empty tier, skipping the previous problem unless it is the only one. Drawing only from that tier means a
problem answered wrong always comes back first. `Choices` gives the right pattern plus three other learned ones, shuffled. All
randomness comes from the `Random` passed in, so a seed reproduces a visit.

## Not in this slice

- Standalone problems authored for Practice (a `practice` content type with its own statement, tests and pattern tag). The tagging above is what that would build on; it can be a later issue.
- Per-problem timers, a custom time limit, difficulty levels, a practice streak or a Today suggestion.
- Interview mode on a phone is supported, but code-heavy problems keep the existing "best on a larger screen" note ([responsive-layouts.md](responsive-layouts.md)).
- Tuning FSRS for practice answers.

## Proposed slices

1. **Content model**: `patterns:` on lessons, `pattern:` on problem steps, validator rules and the warning, tags on the existing Tracks 1–2 content, and a note in the content-authoring docs.
2. **Practice core** (pure, Brilliant.Core): `PracticeSelector`, the `PracticeAnswered` event (with `picked`), the projector changes for `pick.` and `resolve.`, `PatternMastery` / `LearnerState.Patterns`, and the matching update to [mastery.md](mastery.md), with tests.
3. **Practice UI**: the card, the pick-then-solve flow, results, `pick.` items in Review.
4. **Interview mode**: `InterviewStarted` / `InterviewFinished`, the clock, resume, the summary.

Each lands as its own PR; slices 2–4 depend on 1.
