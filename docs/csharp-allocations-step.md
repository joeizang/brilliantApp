# The `allocations` step

Issue [#156](https://github.com/joeizang/brilliantApp/issues/156), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)).
PRD story 46: "allocations steps that ask how much a snippet allocates, or ask me to rewrite it to allocate nothing".

One authoring type, `allocations`, with two variants chosen by `variant:`. Both build on the allocation budget of
[#151](csharp-write-code.md): one call is measured with `GC.GetAllocatedBytesForCurrentThread()`, after a warm-up, as the smallest of five
samples.

| Variant | The learner | Packed as | On a phone |
|---|---|---|---|
| `predict` | Picks how many bytes one call allocates | a multiple-choice `predict-output` step | works as it is; nothing runs on the device |
| `rewrite` | Edits code until it meets a byte budget | a C# `write-code` step | needs the usual `phoneAlternative` (Android can't run C#) |

`allocations` is an authoring key: an app lists it in `stepTypes` (`apps/csharp` does; the Python app does not). The pack carries the two
existing step records, so the engine, the review queue, test-out and the views needed no new code.

## `variant: predict`

```yaml
- id: step.csharp.boxing.alloc
  type: allocations
  variant: predict
  title: Boxing
  prompt: How much does this allocate?
  entrypoint: Boxed             # the method that is called
  input: '5'                    # its arguments as C# source ("" for none); default ""
  code: |
    static int Boxed(int n)
    {
        object o = n;
        return (int)o + 1;
    }
  options:                      # 2 to 6, each a different byte count (0 to 1,000,000)
    - bytes: 0
    - bytes: 24
    - bytes: 48
      feedback: That would be two boxes.   # optional: said when this one is picked
```

- **The right option is never authored.** `pack`, `publish` and `validate` call `Boxed(5)` in the same worker that grades budgeted
  exercises (CoreCLR), read the bytes it counted and mark the option that says so. No option naming the measured number is an error that
  prints the number, so a snippet edited after its options were written can't ship a step with no right answer.
- An option gives only `bytes` (and optionally `feedback`); `text`, `correct`, `accepted`, `mistakes` and the write-code fields are refused.
  The learner sees "0 bytes", "24 bytes", and so on.
- The prompt gets a last line, `Measured call: Boxed(5)`, so the question is exact. What the call does with its arguments counts, as it does
  for a budget: `Sum(new[] { 1, 2 })` includes the array.
- A wrong option's default feedback is "Not quite: one call allocates 24 bytes, not 0 bytes."; the right option's feedback ends with the
  measurement and its runtime. The Mac app runs Mono's interpreter, where a few sizes differ (`int[16]` is 88 bytes on CoreCLR and 96 on
  Mono); the lesson goes by CoreCLR and says so.
- The snippet must be repeatable (the probe compares the call with itself), compile under the app's pinned C# and not exceed 1,000,000
  bytes per call.
- The entrypoint must return a value that compares equal across calls: a number, string, array or record. A `void` method does not compile
  in the probe, and a plain class (`new StringBuilder()`, a non-record class) compares by reference, so the step reports it as not
  measurable. To ask about such an object, return something derived from it (its `Length`, say) and let the allocation happen inside.

## `variant: rewrite`

```yaml
- id: step.csharp.sum.rewrite
  type: allocations
  variant: rewrite
  title: Sum without a list
  prompt: Rewrite SumTo so it allocates nothing.
  entrypoint: SumTo
  largeScreenPreferred: true
  starter: 'static int SumTo(int n) => Enumerable.Range(1, n).ToList().Sum();'
  solution: |
    static int SumTo(int n) { var total = 0; for (var i = 1; i <= n; i++) total += i; return total; }
  tests:
    - input: '4'
      expected: '10'
      maxAllocatedBytes: 0       # required on at least one test
  phoneAlternative:
    id: step.csharp.sum.fill
    type: fill-blank
    ...
```

It is a `write-code` step in every way (language `csharp`, multiple files, hints, a walkthrough, patterns), with two additions: at least one
test must declare `maxAllocatedBytes`, because the budget is what the learner is graded on, and `language` may be left out. The reference
solution is proved against the budget at `validate`/`pack`/`publish`, on CoreCLR; a solution over budget fails with the bytes it allocated.
In the Mac app the budget is advice, as for any budgeted exercise (see [csharp-write-code.md](csharp-write-code.md)).

## Why these two choices

- *Measured, not authored, answers.* The same reason as `benchmark-predict`: a number typed into YAML drifts from the code beside it.
- *Packed as existing steps.* The predict variant is a choice and the rewrite variant is a budgeted exercise; giving them new records would
  have meant a second evaluator, view and set of review/test-out wiring for no behaviour the existing ones lack.
