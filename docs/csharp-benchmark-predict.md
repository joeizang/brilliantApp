# The `benchmark-predict` step and the benchmark recorder

Issue [#155](https://github.com/joeizang/brilliantApp/issues/155), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)).

"Which is faster?" is a question learners answer with folklore. A `benchmark-predict` step makes them commit to a ranking first, then
shows what BenchmarkDotNet measured, with the machine it ran on.

This ships in two pull requests: the model, evaluator, validator and recorder (this document's first half), then the view, the UI
wiring and the C# manifest entry.

## Authoring

```yaml
- id: step.csharp.sum.bench
  type: benchmark-predict
  title: Loop or LINQ
  prompt: Rank them, fewest allocations first.
  measure: allocations        # time (default) or allocations
  setup: 'int[] data = Enumerable.Range(0, 1000).ToArray();'   # optional: members shared by every candidate
  candidates:
    - id: loop                # lowercase letters, digits, hyphens; unique in the step
      label: for loop         # what the learner sees
      code: |                 # a method body that returns an int
        var total = 0;
        for (var i = 0; i < data.Length; i++) total += data[i];
        return total;
    - id: linq
      label: LINQ chain
      code: return data.Where(x => x % 2 == 0).Select(x => x * 2).ToList().Count;
```

- 2 to 5 candidates. Each `code` is the body of `public int CandidateN()` in one generated class with `setup` pasted in as its members
  (fields, helper methods). Returning an `int` (a count, a checksum, a length) keeps boxing out of the measurement.
- The same implicit usings as the rest of the C# app apply (`System`, `System.Collections.Generic`, `System.Linq`).
- **The right order is never authored.** Authors write no `accepted`, `options` or `mistakes`; the pack build measures and the learner
  is graded on what the measurement showed. Hints are allowed; a walkthrough is not (there is no solution to walk through).
- `language` may be left out or must be `csharp`; `pack.yaml` must pin `targetFramework` and `langVersion`, as for every C# step.
- Lessons should say the numbers were recorded on the machine shown (Apple Silicon, ARM64) and claim an order or a ratio ("about 4x"),
  never a quoted figure: another machine gives other numbers.

## The recorder

`tools/Brilliant.Cli/CSharpBenchmark.cs`:

1. The candidates are written into one `[MemoryDiagnoser]` class and compiled in **Release** with the pinned compiler. `#line` directives
   map every compile error back to the author's own text: `candidate 'broken', line 2: CS0029 …` or `the setup, line 1: …`.
2. The assembly is loaded from memory and handed to BenchmarkDotNet's **in-process, no-emit toolchain**, so there is no per-benchmark
   `dotnet build` (the spike measured the default out-of-process path as far too slow for a pack build). Job: BenchmarkDotNet's own
   `ShortRun` (about 12 s for two candidates).
3. Per candidate it keeps mean time (ns), standard deviation, and bytes allocated per call. For the whole step it keeps the machine
   label (CPU, OS, architecture, e.g. `Apple M4 Max, macOS Tahoe 26.6.2, Arm64`), the runtime (`RuntimeInformation.FrameworkDescription`)
   and the job. All of it ends up on `BenchmarkPredictStep.Recorded` in the pack.

**Validate versus pack.** `brilliant validate` only checks that the candidates compile; measuring takes seconds per step and gives
different numbers each time, so only a pack build (`pack`, `publish`) runs the benchmark. A pack with a benchmark step is therefore
not byte-identical between two builds: the numbers are committed with the pack that was published, and regenerated on purpose.

**Refusing a coin-flip.** For a `time` ranking, neighbours whose means are within 1.25x of each other are refused at pack time
(`'A' (310 ns) and 'B' (350 ns) are too close to call`), because a rerun could swap them and the learner would be graded on noise.
Allocation counts are exact, so candidates with equal bytes tie and may be answered in either order.

## The evaluator

`AnswerEvaluator.Evaluate(BenchmarkPredictStep, IReadOnlyList<string> ranking)`: `ranking` is candidate ids, best first. It is correct
when it names every candidate once and each place holds a candidate with the same recorded value as the recorded order puts there (so
ties may swap). A wrong answer says how many places are right (`1 of 3 in the right place.`) without giving the order away. A step
with no recording (a pack source that was only validated) throws rather than grading against nothing.

## Tests

- `BenchmarkPredictTests` (Core, written first): order by time and by bytes, ties, missing/duplicate/unknown ids, no recording, JSON
  round trip.
- `BenchmarkRecorderTests` runs BenchmarkDotNet for real and checks **shape only**: every candidate has a positive mean, a
  non-negative deviation and allocation count, and the recording names a machine and a .NET runtime. One test uses the real job to show
  an allocation-free loop reads 0 B (a very short job leaves stray bytes in the count). No test asserts a time.
- `BenchmarkPredictStepTests` cover authoring errors, the compile-error text, validate-versus-pack, and the manifest allow-list.

## Recording conditions

- Record on a quiet machine. BenchmarkDotNet reads allocations process-wide, so background load can add a stray few bytes to a candidate. Two candidates that should tie may then differ; re-pack when that happens.
- A time ranking is refused when a candidate's mean is under 10 ns (`MinimumMeanNs`). That is timer noise, usually because the JIT folded the work away. Give the candidate more work (a larger input, a loop) or rank by allocations.
- A time ranking is also refused when neighbouring candidates are within 1.25x of each other ("too close to call").

## The view

`BenchmarkPredictStepView` lists the candidates (label and code) in a fixed shuffled order that is never already the answer, with up/down
buttons (and Alt+arrow keys on a desktop) to move each one. **Check** grades the ranking with `AnswerEvaluator`: a wrong order says how many
places are right and lets the learner keep their order and try again; a right order shows a results table (mean time with its standard
deviation, allocated bytes, in ranked order, with the ranked measure in bold) and a line saying which machine and runtime it was measured on,
and that it was one run. A pack with no recording shows "the results aren't in this pack" instead of a question nobody can answer.

It is wired into the lesson viewer (the ranking is recorded as `ranking` in the `StepAnswered` event, and nothing is recorded on a replay),
Review, test-out and the practice prompt, and enabled for the `csharp` app only (the DSA and flavor-check manifests do not list it).
