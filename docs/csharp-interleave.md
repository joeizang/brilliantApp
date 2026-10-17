# The `interleave` step and the interleaving player

Issue [#162](https://github.com/joeizang/brilliantApp/issues/162), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)).
Track 4 (concurrency and the memory model, [#163](https://github.com/joeizang/brilliantApp/issues/163)) is built on it.

Race conditions and deadlocks are about *order*, and an order is hard to see in a block of code. An `interleave` step shows two or three
threads side by side. The learner runs one line at a time, in any order the threads allow, to reach a state that breaks an authored
invariant (a lost update, a deadlock). Grading checks the order they built against the invariant, so nothing is compiled or run on a device.

## What the learner does

- Each thread is a card with its lines. The next line is marked; **Run A** runs only that line. A shared-state panel shows every shared
  variable, each thread's own copies (`t = 0`) and who holds each lock.
- A thread whose next line needs a lock another thread holds is **waiting** and its button is disabled. A thread that has run all its
  lines is finished.
- **Undo** takes back the last line, **Start over** clears the run. Keys: `1`–`3` run a thread, `←` undoes, Return checks.
- **Check** is enabled only when every thread has finished or the ones left are all stuck, so every submission is a real attempt.
  It is graded by `InterleaveMachine.Evaluate` against the invariant. A miss says why ("Every thread finished and counter ended at 2: the
  invariant held in that order.") and lets the learner undo from the end of their order and try another.
- **Watch an order** replays authored schedules one line at a time (Back / Next line / Restart). Once the learner has broken the invariant,
  the authored breaking order is added as **The break**.
- On a phone the threads stack, the shared panel sticks to the top so it stays in view while scrolling the thread cards, and the buttons
  use the touch-target size.

A correct submission records a `StepAnswered` event with `{ correct, schedule }` (the thread names in the order run). Interleave steps
work as review questions, in Review, Practice-style prompts and the lesson test-out. Rating time thresholds are 30 s (Easy) / 120 s (Hard).

## Authoring

```yaml
- id: step.csharp.interleave.lost-update
  type: interleave
  title: Lose an update
  prompt: Both threads add one to the counter. Find an order that leaves it at 1.
  shared:
    - { name: counter, value: 0 }
  threads:
    - name: A                       # 1-8 letters or digits; 2 or 3 threads, 1-8 ops each
      ops:
        - { code: "var t = counter;",     do: "t = counter" }       # what the learner reads / what the machine runs
        - { code: "counter = t + 1;",     do: "counter = t + 1" }
    - name: B
      ops:
        - { code: "var t = counter;",     do: "t = counter" }
        - { code: "counter = t + 1;",     do: "counter = t + 1" }
  invariant:
    kind: final-value               # or no-deadlock
    description: counter is 2 once both threads have finished
    variable: counter               # final-value only: a shared variable ...
    expected: 2                     # ... and the value it must end at
  breakingSchedule: [A, B, A, B]    # thread names, one per line run; must break the invariant
  replays:                          # optional, shown from the start: do not put the answer in them
    - label: One after the other
      schedule: [A, A, B, B]
  explanation: Both threads read 0 before either wrote, so both wrote 1.
  hints: [What if B reads before A has written?]
```

### The machine language (`do`)

Every `do` is one indivisible step, so a C# line such as `counter++` is authored as the load and the store it really is.

| `do` | meaning |
|---|---|
| `t = counter` | load the shared variable into the thread's own `t` |
| `counter = t` / `counter = t + 2` / `counter = t - 1` | store a loaded local (optionally plus or minus a number) |
| `counter = 5` | store a constant |
| `lock name` / `unlock name` | take / release a lock |

`code` is only what is displayed. A thread blocks only when the lock it wants is held by another thread.

### What the pack build proves

- every `do` parses, a local is loaded before it is stored, locks are non-reentrant and every thread releases what it takes;
- **every one-thread-after-another order finishes without breaking the invariant**, so the puzzle really is about interleaving;
- `breakingSchedule` is playable and breaks the invariant, and every replay is playable;
- `final-value` names a shared variable and an expected value; `no-deadlock` needs neither.

Like the other question steps, an interleave step may carry `hints`, may not carry a `walkthrough`, and cannot carry `accepted`, `options`,
`mistakes`, `code` or `tests`. `language` may be left out or must be `csharp`. The C# app's manifest enables the `interleave` step type;
the DSA app does not.

## Pieces

| Piece | Where |
|---|---|
| Model (`InterleaveStep` and friends) | `src/Brilliant.Core/Content/ContentModel.cs` |
| Evaluator and stepper (`InterleaveMachine`) | `src/Brilliant.Core/Content/InterleaveMachine.cs` |
| Loader and validator | `tools/Brilliant.Cli/ContentSource.cs` (`LoadInterleave`) |
| Player | `src/Brilliant.Lessons.UI/InterleaveStepView.razor` |
| Recording | `ProgressRecorder.ScheduleSubmitted` |
