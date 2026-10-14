# Practice mode and timed interview simulation

Issue [#36](https://github.com/joeizang/brilliantApp/issues/36) (PRD milestone M4). **Status: design agreed (PR #93). All four slices are built: content model, practice core, practice UI and interview mode ([Proposed slices](#proposed-slices)).**

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
| `pattern.stack-matching` (Matching with a stack) | Track 3 lesson 2 | its 5 problems |
| `pattern.monotonic-stack` (Monotonic stack) | Track 3 lesson 3 | its 5 problems; lesson 9 `next_larger` (Parsons and write) |
| `pattern.window-deque` (A deque for the window) | Track 3 lesson 4 | its 4 problems (the Josephus `survivor` is a plain queue drill, untagged) |
| `pattern.dummy-head` (A dummy head node) | Track 3 lesson 5 | its 4 problems (the `to_list` drill is untagged) |
| `pattern.reverse-linked-list` (Reverse with prev, cur and nxt) | Track 3 lesson 6 | its 5 problems; lesson 9 `reorder` |
| `pattern.fast-slow-pointers` (Fast and slow pointers) | Track 3 lesson 7 | its 6 problems; lesson 9 `remove_nth` (fill and write) |
| `pattern.merge-sorted-lists` (Merge with a tail pointer) | Track 3 lesson 8 | its 5 problems |
| `pattern.recursive-decomposition` (Solve it from a smaller call) | Track 4 lesson 2 | its 5 problems (lesson 1's `count_digits`, `digit_sum` and `gcd` are recursion drills, untagged) |
| `pattern.dfs-traversal` (Pick the walk: pre-order, in-order or post-order) | Track 4 lesson 3 | its 5 problems; lesson 9 `kth_smallest` |
| `pattern.bfs-level-order` (Level by level with a queue) | Track 4 lesson 4 | its 5 problems; lesson 9 `zigzag` (Parsons) |
| `pattern.dfs-return-values` (Combine the children's answers) | Track 4 lesson 5 | its 5 problems; lesson 9 `max_path_sum` |
| `pattern.bst-ordering` (Let the BST order pick the side) | Track 4 lesson 6 | its 5 problems; lesson 9 `lca` (fill) |
| `pattern.top-down-state` (Carry what the path knows down as arguments) | Track 4 lesson 7 | its 5 problems; lesson 9 `max_ancestor_diff` |
| `pattern.backtracking` (Choose, explore, unchoose) | Track 4 lesson 8 | its 5 problems |
| `pattern.graph-dfs` (Dive along the edges, marking nodes seen) | Track 5 lesson 2 | its 5 problems |
| `pattern.graph-bfs` (Spread out one step at a time for the fewest steps) | Track 5 lessons 3–4, 8 | lesson 3's 5 problems, lesson 4's `escape`, lesson 8's `knight_moves` |
| `pattern.grid-flood-fill` (Flood out from a cell to its four neighbours) | Track 5 lessons 4, 8 | lesson 4's other 4 problems, lesson 8's `closed_islands` |
| `pattern.multi-source-bfs` (Start the queue with every source at once) | Track 5 lessons 5, 8 | lesson 5's 5 problems, lesson 8's `gate_distances` |
| `pattern.topo-sort` (Take whatever has nothing left before it) | Track 5 lessons 6, 8 | lesson 6's 5 problems, lesson 8's `install_order` |
| `pattern.union-find` (Merge groups, then ask which group) | Track 5 lessons 7–8 | lesson 7's 5 problems, lesson 8's `is_tree` |
| `pattern.binary-search` (Halve a sorted range, keeping the target inside it) | Track 6 lesson 1 | its 5 problems |
| `pattern.binary-search-boundary` (Find the first index where a condition turns true) | Track 6 lessons 2, 8 | lesson 2's 6 problems, lesson 8's `h_index` |
| `pattern.binary-search-on-answer` (Binary search the answer, testing whether each guess is enough) | Track 6 lessons 3, 8 | lesson 3's 5 problems, lesson 8's `split_array` (Parsons) |
| `pattern.heap-repeated-min` (Take the smallest (or largest) again and again, as items come and go) | Track 6 lessons 4, 8 | lesson 4's 3 problems, lesson 8's `min_rooms` (fill) and `nth_number` |
| `pattern.top-k` (Keep the k best in a heap of size k, with the weakest of them at the root) | Track 6 lessons 5, 8 | lesson 5's 5 problems, lesson 8's `furthest` |
| `pattern.k-way-merge` (Merge k sorted sequences with a heap holding one head from each) | Track 6 lesson 6 | its 4 problems |
| `pattern.two-heaps` (Split the items between two heaps, and read the answer from their roots) | Track 6 lesson 7 | its 4 problems |
| `pattern.memoisation` (Recurse on smaller subproblems, caching each answer the first time) | Track 7 lesson 1 | its 4 problems |
| `pattern.tabulation` (Fill a table from the smallest subproblems up, then read the answer from it) | Track 7 lesson 2 | its 4 problems |
| `pattern.dp-1d-decisions` (At each position, take the best of a few choices built from the answers just before) | Track 7 lessons 3, 10 | lesson 3's 5 problems, lesson 10's `cheapest_paint` |
| `pattern.dp-unbounded-choices` (Build each amount from every item that could come last, reusing items as often as you like) | Track 7 lessons 4, 10 | lesson 4's 4 problems, lesson 10's `fewest_squares` (fill) |
| `pattern.dp-string-prefixes` (Answer each prefix of the string from every way its last piece could end there) | Track 7 lesson 5 | its 4 problems |
| `pattern.dp-grid` (Fill each cell from the cells you can arrive from, row by row) | Track 7 lesson 6 | its 4 problems |
| `pattern.dp-two-sequences` (Fill a table over prefixes of both sequences, deciding what happens to their last items) | Track 7 lessons 7, 10 | lesson 7's 4 problems, lesson 10's `shortest_holding` (Parsons) |
| `pattern.dp-knapsack` (Decide each item once, take it or skip it, building on the answers without it) | Track 7 lessons 8, 10 | lesson 8's 4 problems, lesson 10's `sign_ways` |
| `pattern.dp-lis` (For each item, extend the best sequence ending at an earlier, smaller item) | Track 7 lessons 9, 10 | lesson 9's 4 problems, lesson 10's `tallest_tower` |

Left untagged: every problem in Track 1 lessons 1–6 (Python and Big-O drills), Track 5 lesson 1's graph-building drills, Track 2 lesson 6's `count_subarrays` (its prompt says
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

A session has no fixed length; the learner stops when they like. Practice is not part of the daily goal, but it has its own streak
([below](#practice-streak-and-todays-suggestion-128)).

## Timed interview simulation

**Setup.** Three presets: *Phone screen* (2 problems, 30 minutes), *Interview* (3 problems, 45 minutes), and *Custom* (#128): 1 to 5
problems (no more than there are to draw from) and 10 to 90 minutes in steps of 5, starting at 3 and 45. The problems are chosen like Practice, preferring a different pattern for each. Starting asks for confirmation ("The clock starts now and can't be paused").

**During the run.**
- One countdown for the whole run, always visible, turning urgent in the last 5 minutes.
- Beside it, the time on the problem on screen against a **soft per-problem budget** (#128): the limit shared evenly (15 minutes each
  in a Phone screen). Going over shows "(over budget)" and nothing else happens; the run's clock is the only hard limit. The time counts
  whether or not the problem is answered, and is kept in the log (`InterviewTimeSpent`, below), so it survives switching problems,
  leaving, closing the app and finishing early. Time after the run's clock ran out doesn't count.
- Each problem is the same pick-then-solve flow, but **no hints, no feedback, no "Check" results beyond the code runner's test output** (as in a real exercise, the learner can run their code against the visible tests). The pattern pick is locked on submit and its correctness is not shown until the end.
- The learner can move between the run's problems freely and submit each when ready. *Finish early* ends the run.
- Leaving the screen, switching apps or closing the app does **not** pause anything.

**Ending.** The run ends when the clock reaches zero, the learner finishes early, or every problem is submitted. An unsubmitted problem counts as unanswered (a failed pick and a failed solve).

**The summary.** For each problem: the pattern it was, what the learner picked, whether the solution passed, and the time used
against the budget ("Time: 16m 00s of a 15m 00s budget (over)"). Then a one-line read on the weakest pattern of the run and a note that the results are now in Review. This is the one place the answers are revealed.

**Interruption.** The run is stored as events, so it survives a closed app and syncs like everything else:

- `InterviewStarted` `{ run, preset: "phone-screen"|"interview"|"custom", steps[], limitSeconds }`. A custom run's problem count is
  its `steps[]` and its limit is `limitSeconds`, so nothing else is new; `ProgressRecorder.StartInterview(preset, problems, limit)`
  refuses a custom run outside its bounds. An app older than #128 doesn't know `custom` and ignores that run (its answers still schedule). — the problem set and the start time are fixed here. Each `steps[]` entry
  is `{ step, lesson, choices[] }`: the pattern choices are fixed at the start too, so a resumed run (on any device) offers the same
  options in the same order.
- `InterviewFinished` `{ run }` — written when the run ends by any route.
- `InterviewTimeSpent` `{ run, step, spentMs }` — a problem's total time on screen so far, answered or not (#128). Written when the
  learner switches problems, answers, leaves or finishes, and every 30 seconds while the problem stays on screen; the largest total
  counts, and a finished run takes no more. A problem's time is that total, or its answers' elapsed times if they say more.
  Banking stops at the run's end, so a total over the run's limit (from another device's event) is capped to the limit, and one a
  `TimeSpan` can't hold is ignored.

The time left is always `startedAt + limit − now`. If the app is reopened with time left, the run resumes; with none left, the summary is shown and any unanswered problems are recorded. A run is *open* until a `InterviewFinished` exists for it, and at most one run is open at a time. Because the clock is wall time, a device with a wrong clock would shorten or lengthen a run; that is accepted for a personal tool.

## How results feed Review

A practice answer is scheduled on a review item, scored by the same rating inference (wrong → Again, hints and time as today) and scheduled by the same FSRS.

| Practice result | Review item it updates | Kind (queue weight) |
|---|---|---|
| The pattern pick | `pick.<step id>` — "which pattern fits this problem?" | Pattern (2.0) |
| The solution | `resolve.<step id>` | Re-solve (1.5) |

Both items are **derived**, never authored, like re-solves today. Differences from today's rules, which the implementation must change in `LearnerStateProjector`:

1. A `resolve.<step>` item currently exists only if the lesson attempt failed or used a hint. It now **also exists once the step has been practised**, so a correct practice solve starts its own schedule. A `pick.<step>` item exists once the step has been practised.
2. Practice and interview answers are written as a new event type, `PracticeAnswered` `{ step, part: "pick"|"solve", correct, picked?, hintsUsed, elapsedMs, rating, mode, run? }` (plus `answered` and `day` since #129), which the projector folds through FSRS like `ReviewAnswered` but which is ignored by lesson progress. (A separate event keeps the log honest about where an answer came from, and keeps today's `ReviewAnswered` meaning unchanged.)
3. Only the **first** answer to a part in a given practice visit or interview run is scheduled; retrying is practice, as in Review.
4. Interview unanswered problems are recorded for both parts when the run ends. (Originally rated `Again`; since #129 they are marked `answered: false` and schedule nothing. See [review.md](review.md#practice-and-interview-answers).)

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
longer than a choice question, Easy under 15 seconds and Hard from a minute. The same rule rates a `pick.` item answered in Review
(`ReviewItemState.IsPracticePick`): its question is the problem, but the answer only names the pattern, so the problem's own format
(write-code, Parsons, or its phone form) never sets the rating. Authored `kind: pattern` questions and re-solves keep their rules. A malformed event, or one whose `step` disagrees
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

### As built (slice 3)

The screens, in `Brilliant.Lessons.UI`. Tests: `tests/Brilliant.Lessons.UI.Tests/PracticeViewTests.cs`.

**The card.** Tracks shows a *Practice* card under the Review line. While Practice is closed it is disabled and says why
(`PracticeText.Unavailable`): "Finish lessons that teach 3 patterns to unlock Practice (2 so far)", or, with the patterns learned but no
problem on offer, that no finished lesson has one. Once open it names the weakest learned pattern ("Weakest: Prefix sums: learning, 34%").

**The screen** (`PracticeView`, `_practising` in `CourseShell`). It hides the phone's bottom tabs like Review; Back (the link or the
shortcut) returns to Tracks. Each visit is one run, a fresh GUID. For each problem:

1. `ProblemPrompt`: "Problem" and the prompt of the form being asked (the phone alternative on a phone), nothing else.
2. `PatternPick`: the choices from the selector. Checking locks the pick and records it (`PracticePicked`). The verdict names the right
   pattern and marks it; a wrong pick still continues to the solve. Number keys choose, Enter checks and continues.
3. The normal step view with its title replaced by "Problem" (a new `Title` parameter on the write-code, fill-blank and Parsons views),
   the "best on a larger screen" note where it applies, the hint ladder (shown only now), and the flag link. The first answer is recorded
   (`PracticeSolved`) with the hints revealed before it; later tries are not. A write-code draft is kept per run and step
   (`practice.<run>.<step>`). After a wrong first answer the learner can retry or choose *See the result*.
4. The result: a tick or cross for the pattern and the solution, the time from showing the problem to leaving the solve, and when each
   item comes back in Review. *Next problem* asks the selector again, never repeating the problem just done; *Finish* returns to Tracks.

**Feedback cue.** Every tagged step also carries an authored `patternCue`: one or two sentences (markdown) naming what in the prompt
points to its pattern and why, e.g. "*\"Sorted in ascending order\" plus \"two numbers that add up to `target`\": the order lets two
pointers at the ends decide which way to move.*" `PatternPick` shows it under the verdict once the pick is checked, right or wrong, in
Practice and for `pick.` items in Review; it is never in the page before the check, since it would give the answer away.

```yaml
  - id: step.converging.write-two-sum
    type: write-code
    pattern: pattern.converging-pointers
    patternCue: "\"Sorted in ascending order\" plus \"two numbers that add up to `target`\": ..."
```

The validator requires `patternCue` on every step with a `pattern`, and rejects an empty cue, a cue without a `pattern`, and a cue on a
phone alternative (it shares its step's). `Step.PatternCue` is optional in the pack, so the pack format stays at version 1.

**Pick items in Review.** A due `pick.` item shows the same `ProblemPrompt` and `PatternPick`, with choices drawn by
`PracticeSelector.Choices` each time it is asked, and no hint ladder (a pattern-hint rung would give it away). The first pick is recorded
as a `ReviewAnswered`, rated by `InferPick`.

### As built (slice 4)

Core in `Brilliant.Core/Practice/InterviewRun.cs`, the screen in `Brilliant.Lessons.UI/InterviewView.razor`. Tests:
`tests/Brilliant.Core.Tests/InterviewTests.cs` and `tests/Brilliant.Lessons.UI.Tests/InterviewViewTests.cs`.

**The run, from the log.** `InterviewRuns.From(events)` rebuilds every run: its preset, problems (with their stored choices), start
time, limit, whether it is finished, and its interview-mode answers (the first per step and part, ordered by time then event ID as the
projector orders them, so every device and the schedule agree on which answer counts). `Open` is the last
unfinished run; a malformed `InterviewStarted` is ignored. `InterviewRun.Left(now)` is `startedAt + limit − now`, never negative, and
`IsOver(now)` is finished or out of time.

**Choosing the set.** `PracticeSelector.Set(state, random, count)` draws problems as Practice does (weakest patterns first), taking a
pattern already in the set only once every learned pattern has one. Each problem's choices come from `PracticeSelector.Choices`.

**Recording.** `ProgressRecorder.StartInterview(preset, problems, limit?)` writes `InterviewStarted` unless a run is already open.
`FinishInterview(run, steps)` records each unanswered pick (with no `picked`) and each unanswered solve as wrong and `answered: false`,
with zero elapsed time so the summary's times are only what was spent, then writes `InterviewFinished`. Unanswered parts create and
move no review item (#129); the summary says "You didn't submit a solution." and, when anything was left, that it doesn't count against you. It does nothing for a finished or unknown run. Two
devices that both finish the same run before syncing may each record the gaps; the projector keeps the first answer per part.

**The card.** Tracks shows a *Timed interview* card under Practice: "1 to 5 problems against one clock", Practice's reason while
Practice is closed, "In progress: 18 min left." during a run, and "Time's up. See how you did." once an open run has run out. A run in
progress opens whatever Practice's state. In `CourseShell` the interview is a second Practice screen (`_interview` alongside
`_practising`): no bottom tabs, and Back returns to Tracks without touching the run.

**The screen.**
- *Setup*: the two presets, then the confirmation ("The clock starts now and can't be paused") with *Start the clock* or *Cancel*.
- *Running*: the clock (`m:ss`, `role="timer"`, urgent under 5 minutes), a tab per problem (marked once submitted), and *Finish early*,
  which asks first. A problem is `ProblemPrompt` and then `PatternPick` in its new `Blind` mode: the button says *Lock in*, and locking
  shows only "Locked in: …", with no verdict, cue or marks. Then the step view titled "Problem" with no hint ladder: fill-blank and Parsons
  say *Submit* (a new `CheckText` parameter), and the answer is taken without showing the verdict. Write-code keeps *Run tests* (the
  runner's output is allowed) and adds *Submit solution*, which runs the code as it stands and records whether every test passed
  (`WriteCodeStepView.RunAsync`; no Continue, via `OfferContinue`). Its draft is kept per run and step (`interview.<run>.<step>`). Fill-blank
  answers and Parsons arrangements not yet submitted are kept the same way (`interview.<run>.<step>.blanks` / `.lines`, as JSON, through
  the synced draft store), so switching problems, leaving or resuming on another device loses no work. A
  submit moves to the next unsubmitted problem; the last one ends the run.
- *Time per problem* is counted while the problem is on screen: the pick's elapsed is the time up to locking it, the solve's the rest.
  Resuming starts each problem from its recorded time (`InterviewRun.TimeOn`), so time spent without answering is kept.
- *The clock* is checked every second; at zero the run finishes and the summary shows. Opening the screen with an open run resumes it
  on its first unsubmitted problem, or, if its time is up, finishes it and shows the summary.
- *Summary*: per problem, "It was Pattern X." with the pick ("You picked it.", "You picked Y.", "You gave no answer."), whether the
  solution passed, and the time ("none" when the problem was never on screen). Then the weakest of the run's patterns (`PracticeText.Standing`)
  and "Your answers are now in Review."

## Practice-only problems (#127)

Issue [#127](https://github.com/joeizang/brilliantApp/issues/127). Practice used to draw only from lesson steps, so a pattern had as
many problems as its lessons happened to tag (four for eight of them). A lesson can now also own problems that exist only for Practice
and Interview.

**Authoring.** Put them in `practice.yaml` next to the lesson's `lesson.yaml`:

```yaml
problems:
  - id: step.grid-dp.practice.min-path-sum   # a step ID like any other, unique in the pack
    type: write-code                         # write-code, fill-blank or parsons
    title: The cheapest path down
    pattern: pattern.dp-grid                 # required
    patternCue: "\"Moving only right or down\" to a corner: ..."
    prompt: |
      ...
    entrypoint: cheapest
    solution: |
      ...
    tests: [...]
    hints: [...]
```

A problem is an ordinary step: the same fields, the same checks (the reference solution must pass its tests, hints at most four,
an optional walkthrough). On top of a lesson step's rules, the validator requires a `pattern` (Practice only draws problems by
pattern) and rejects any other step type; an empty `problems` list is an error, and the file is optional. The pattern rules are the
ones above: it must be taught by this lesson or an earlier one in the track, or by another track, and a prompt naming its pattern
is a warning. Practice doesn't use phone alternatives, so a practice problem doesn't need one.

**In the model.** `Lesson.Practice` holds them, apart from `Lesson.Steps`: they never appear in the lesson, don't count toward
completing it and don't change "new steps" or replay. `Lesson.AllSteps()` is the steps followed by the practice problems, and is what
everything keyed by step ID looks in: the Content Graph's index (so IDs stay unique across both), the projector's `pick.`/`resolve.`
items and pattern steps, flagged steps (a flagged practice problem sorts after its lesson's steps) and concept pages. The pack format
stays at version 1: `practice` is optional, so an older pack loads with none, and an older app ignores them. A pack whose practice
problem has no pattern is refused on load (`ContentGraph.ReferenceProblems`).

**In Practice.** Once the owning lesson is completed, its practice problems join their patterns alongside the tagged lesson steps
(`PatternMastery.Steps`, in content order, each lesson's practice problems after its steps), so the selector, interview sets, pattern mastery and Review treat them exactly
like lesson problems. Answers are recorded against the owning lesson's ID, as for a tagged lesson step.

**First batch.** One problem for each pattern that had only four: grid DP (cheapest path), string prefixes (ways to decode digits),
k-way merge (k-th smallest across sorted lists), memoisation (stairs with 1, 2 or 3 steps), tabulation (tiling with two colours),
two heaps (running lower middle), window deque (minimum of every window) and dummy head (drop every repeated value, Parsons).

Tests: `tests/Brilliant.Cli.Tests/PracticeProblemValidatorTests.cs`, the "Practice-only problems" section of
`tests/Brilliant.Core.Tests/PracticeTests.cs`, `FlaggedStepsTests` and `PracticeViewTests`.

## Practice streak and Today's suggestion (#128)

Part of issue [#128](https://github.com/joeizang/brilliantApp/issues/128).

**The streak.** `TodayState.PracticeStreak` is the number of consecutive days with at least one answered practice or interview part,
counted back from today, or from yesterday while today has no answer yet (so a streak isn't lost before the day is over).
`TodayState.PractisedToday` says whether today has one. Each answer's day is the local day it recorded (`AnswerDay.Of`, see
[review.md](review.md#practice-and-interview-answers)); a part an interview ended without an answer doesn't count, and an interview
answer counts like a practice one. It is separate from the daily-goal streak: practice still adds no steps toward the goal.

**Where it shows.** The Practice card on Tracks adds "3-day practice streak", followed by " · practise today to keep it" until today
has an answer; nothing shows without a streak.

**Today's suggestion.** Once nothing is due in Review and Practice is open, Today shows a Practice card above Up next: "Reviews are
done. Practise your weakest patterns: A and B." (`PracticeSelector.Suggestion`: the two learned patterns with problems, lowest
mastery first), with the practice streak under it. It opens Practice, and leaving Practice (the bar's button or Back) returns to Today
rather than Tracks. Today shows no card while reviews are due or before Practice unlocks.

Tests: `tests/Brilliant.Core.Tests/PracticeStreakTests.cs` and the "streak and Today's suggestion" section of `PracticeViewTests`.

## Not in this slice

- Standalone problems authored for Practice: done in [#127](#practice-only-problems-127).
- Difficulty levels (#128, a later slice).
- Per-problem times and a custom time limit: shipped in #128 (see the timed interview above).
- A practice streak and a Today suggestion: done in [#128](#practice-streak-and-todays-suggestion-128).
- Interview mode on a phone is supported, but code-heavy problems keep the existing "best on a larger screen" note ([responsive-layouts.md](responsive-layouts.md)).
- Tuning FSRS for practice answers. Done in [#129](https://github.com/joeizang/brilliantApp/issues/129): see [review.md](review.md#practice-and-interview-answers).

## Proposed slices

1. **Content model**: `patterns:` on lessons, `pattern:` on problem steps, validator rules and the warning, tags on the existing Tracks 1–2 content, and a note in the content-authoring docs.
2. **Practice core** (pure, Brilliant.Core): `PracticeSelector`, the `PracticeAnswered` event (with `picked`), the projector changes for `pick.` and `resolve.`, `PatternMastery` / `LearnerState.Patterns`, and the matching update to [mastery.md](mastery.md), with tests.
3. **Practice UI**: the card, the pick-then-solve flow, results, `pick.` items in Review.
4. **Interview mode**: `InterviewStarted` / `InterviewFinished`, the clock, resume, the summary.

Each lands as its own PR; slices 2–4 depend on 1.
