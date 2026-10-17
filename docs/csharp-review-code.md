# The `review-code` step

Issue [#164](https://github.com/joeizang/brilliantApp/issues/164), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)),
in the Judgement phase. Later content (API and library design, technical leadership) builds on it.

Seniors spend more time reading code than writing it. A `review-code` step shows a diff or a file; the learner flags the lines they would
push back on and picks a category for each. Grading compares the flags with defects the author planted, so nothing is compiled or run and it
works on every device. Free-text "why" grading is out of scope here; it belongs with the design-tradeoff step and the local feedback work
([#165](https://github.com/joeizang/brilliantApp/issues/165), [#166](https://github.com/joeizang/brilliantApp/issues/166)).

## What the learner does

- The code is shown as numbered lines. **Tap a line** to open five category buttons under it (Bug, Performance, Concurrency, API design,
  Security); choosing one flags the line and shows a badge. Tap a flagged line to change the category or **Remove flag**.
- **Submit review** is enabled once at least one line is flagged. Submitting locks the review and shows the result.
- On a Mac, Return submits, then continues. Lines and category buttons are ordinary buttons, so Tab and Space work too.
- On a phone each line is a full-width tap target at the touch size, and long lines scroll sideways inside their row.
- A `diff` colours `+` lines green and `-` lines red.

## How it is graded

`ReviewCodeEvaluator.Evaluate(step, flags)` is pure.

| Situation | Credit |
|---|---|
| A flag on any line of a defect, with its category | 1 |
| A flag on the defect, with another category | 0.5 ("right line, wrong kind") |
| A defect with no flag | 0, and it is revealed with its explanation |
| A flag on a line with no defect (a *false flag*) | −0.25 each, once per line |

`Score = clamp((credit − 0.25 × falseFlags) / defects, 0, 1)`. A review is *correct* only when every defect is found under its category and
nothing is false flagged; that is what "correct" means for Review scheduling and the lesson test-out. The partial score is shown to the
learner and stored in the event. Flags outside the code or with an unknown category are ignored. Two flags inside one defect do not count as
false flags, and the best category wins.

After submitting, defect lines are marked found or missed, false flags are struck through, and each false flag shows the author's decoy note
if there is one ("A `bool` result for a taken seat is the ordinary `Try...` pattern") or "has nothing wrong with it".

A submission records a `StepAnswered` event with `{ correct, score, flags: [{ line, category }] }`. Review steps work as review questions, in
the lesson test-out and in Practice-style prompts. Rating time thresholds are 60 s (Easy) / 240 s (Hard); test-out difficulty is 4.

## Authoring

```yaml
- id: step.csharp.races.review-booking
  type: review-code
  title: Review the seat booking
  prompt: Many requests call TryBook at once. Flag what you would push back on.
  language: csharp                  # optional, default csharp; use diff for a unified diff
  code: |
    public sealed class SeatBooking
    {
        ...
    }
  defects:
    - line: 8                       # 1-based line of the code above
      endLine: 9                    # optional; at most 5 lines. Flagging any line in the range finds it
      category: concurrency         # bug | performance | concurrency | api | security
      explanation: Why it is wrong, shown whether or not the learner found it.
  decoys:                           # optional: lines that look wrong but are fine
    - line: 6
      explanation: Why the line is fine.
  hints: [...]                      # same hint ladder as the other questions
```

The pack build rejects a step when:

- `code` or `defects` is missing, there are more than 6 defects, or the code is over 60 lines (it must read on a phone);
- a defect's `line`/`endLine` is outside the code, reversed, longer than 5 lines or all blank;
- a `category` is not one of the five, or an `explanation` is missing;
- two defects overlap, or a decoy sits on a defect, repeats a line or has no explanation;
- the step has `accepted`, `options`, `mistakes`, `tests`, `threads` or a top-level `explanation` (each defect carries its own).

The app manifest must list `review-code` under `stepTypes`; the C# app does. A step may be `largeScreenPreferred`.

## Writing good reviews

- Plant a few real problems (2 to 4), of more than one category where the code allows it, and make each unambiguous. A reviewer who
  flags something that is arguably a problem is punished by the grading, so keep the code free of other genuine issues.
- Use decoys for the line a reviewer is most likely to flag by mistake. They turn a false flag into a lesson.
- Explain *why* in each defect; the explanation is the lesson for anyone who missed it.

Content so far: one review at the end of the "Finding races and deadlocks" lesson (Track 4).
