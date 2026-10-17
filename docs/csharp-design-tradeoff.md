# The `design-tradeoff` step

Issue [#165](https://github.com/joeizang/brilliantApp/issues/165), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)),
in the Judgement phase. Architecture, API design and technical leadership content builds on it.

A principal engineer is paid for choosing between reasonable options and saying why. A `design-tradeoff` step gives a scenario and a few
options. The learner picks one and writes a justification. The option is graded by the app. The justification is graded by the learner,
against a rubric the author wrote, and that self-assessment is what drives Review. Nothing is compiled or run, so it works offline on
every device. Model-written feedback on the justification is separate work ([#166](https://github.com/joeizang/brilliantApp/issues/166),
[#167](https://github.com/joeizang/brilliantApp/issues/167), [#168](https://github.com/joeizang/brilliantApp/issues/168)).

## What the learner does

1. Reads the scenario, **picks one option** and **writes why** in the text box. **Check** is enabled once an option is picked and the box
   is not blank. On a Mac the number keys pick an option.
2. After Check the pick is marked right or wrong with the option's own feedback, and the learner sees their written answer, the **model
   answer** and the rubric.
3. The learner ticks **which rubric points their answer made**, and (if the step has any) which **red flags** it claimed: common
   plausible-but-wrong statements such as "`volatile` makes `count++` atomic". Then **Done**. Return also finishes.
4. The result shows the summary and score. **Continue** moves on (Return too).

The written text is hidden from grading: the app never tries to read it. That keeps grading deterministic and offline, and puts the honesty
on the learner, who is told that the ticks decide how soon the question returns.

## How it is graded

`DesignTradeoffEvaluator.Evaluate(step, option, pointsMade, redFlagsClaimed)` is pure.

`Score = clamp(0.4 × [option is right] + 0.6 × coverage − 0.1 × redFlagsClaimed, 0, 1)`, where `coverage` is the ticked rubric weight over
the total weight. An answer is **correct** when the option is right, `coverage ≥ 0.6` and no red flag was claimed. Correct is what Review
scheduling and the lesson test-out use: it goes through the same `RatingInference` as every other step, so a wrong pick or a thin
justification is *Again* and a correct one is Easy, Good or Hard by time. The score is shown to the learner and stored in the event.
Out-of-range and repeated ticks are ignored.

Rating time thresholds are 90 s (Easy) / 300 s (Hard); test-out difficulty is 5, the highest, since the step asks the learner to weigh
options and defend a choice rather than recall a fact.

## What is recorded

A finished answer records `StepAnswered { correct, score, option, justification, pointsMade, redFlags }`. The justification text is kept
in the event log (and so synced) on purpose: model feedback can be requested later, on the Mac, for an answer given on the phone.

## Authoring

```yaml
- id: step.csharp.races.tradeoff-counting
  type: design-tradeoff
  title: Counting hits from many threads
  prompt: ...
  options:                     # 2 to 5, exactly one correct, each with feedback
    - { text: ..., correct: true, feedback: ... }
  rubric:                      # 2 to 6 points; points: is a weight of 1 to 5, default 1
    - point: What a good justification says
      points: 2
  modelAnswer: ...             # required
  redFlags: [ ... ]            # optional, at most 4
  hints: [ ... ]               # as for other questions
```

`pack validate` rejects a missing prompt or model answer, a step without exactly one correct option or without feedback on each option,
a rubric outside 2 to 6 points, a weight outside 1 to 5, more than four red flags, and the fields that belong to other step types.
The step type must be listed in the app manifest (`design-tradeoff` is on for the C# app, off for DSA).
One example is in Track 5, lesson 6 (pack 0.7.2).
