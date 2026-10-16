# C# write-code on the Mac (#150)

C# `write-code` steps run natively on the Mac (Mac Catalyst) through `Brilliant.Runtime.CSharp`.
Android never carries Roslyn or reference assemblies: the step shows its authored phone alternative
at every window size, because the rule follows the host capability (`ScreenInfo.CSharpWriteCodeAvailable`),
not the width.

Content rule: every `write-code` step with `language: csharp` must set `largeScreenPreferred: true` and a
`phoneAlternative` (parsons or fill-blank); validation rejects it otherwise, since Android has no other way to
finish the step. An app that ships such alternatives lists `fill-blank` (or `parsons`) in its `stepTypes`.

## Pipeline

1. `CSharpSubmissionCompiler` (shared with the CLI's reference-solution gate, #149) compiles the
   learner's top-level functions in memory and probe-rewrites them under the pinned Roslyn/reference set.
   Compile errors return `TestRunStatus.Error` with the original line number; they are never failed tests.
   When the only errors are in a generated test call (for example the learner's signature doesn't match), there is
   no learner line: the message says which test call doesn't fit and the traceback names it `test N`, not a harness file.
2. `NativeCodePolicy` allow-lists the framework surface (collections, LINQ, math, strings, console output).
   Anything else fails before loading. Blocking calls such as `Thread.Sleep` are denied: they can't be unwound and would force a restart.
3. `CSharpRuntime` loads the image into a collectible `AssemblyLoadContext`, runs each test on its own thread under a
   `ProbeBudget` (2 s by default), and unloads the context afterward. `Console` output is captured per test
   (`ProbeConsole`), without replacing the process writers.
4. Results use the Python shape: passed count, first failure (call, expected, actual), printed output.
   Equality is structural (`CSharpValues`: collections, dictionaries, tuples, records).

## Runaway code

Probes stop loops and recursion cooperatively. A blocking framework call is cut off by an independent
watchdog and the run reports a time-out immediately (exercised in tests through an internal seam; learner code can't call `Thread.Sleep`). A thread that cannot unwind poisons the session, so the next run says
to quit and reopen the app (the draft is already saved). The session also stops after 50 assembly loads, as collectible
contexts are not guaranteed to be reclaimed by the runtime. Printing is capped at 16 KiB per test and is reported as an error, not a time-out.

## Allocation budgets (#151)

A C# test may declare `maxAllocatedBytes`, the most heap memory one call of the entrypoint may allocate (0 to 1,000,000):

```yaml
tests:
  - input: '4'
    expected: '8'
    maxAllocatedBytes: 0
```

After a test returns the right answer, `AllocationMeter` calls the entrypoint 50 times to warm up (static initialisation and cached lambdas
are not counted), then takes five samples of 50 calls with `GC.GetAllocatedBytesForCurrentThread()` on the test's own thread. The result is the
smallest sample's bytes per call, so a stray runtime allocation in one sample can't fail a method that allocates nothing. The call expression
`Entry(input)` is what is measured, so allocations made building the `input` arguments count too; use allocation-free inputs for a zero-byte
budget. Printing is discarded while measuring, and each call gets a fresh probe budget. Measurement uses the unoptimised test build, so
the numbers are stable, but they are not the numbers an optimised build would give.

Which runtime measured matters (#138): CoreCLR and Mono agree on zero, and on a boxed int (24 bytes), but differ on an `int[16]`
(88 against 96) and on a captured closure (88 against 152).

| Where | Runtime | Over budget |
|---|---|---|
| `validate`, `pack`, `publish` (reference solution) | CoreCLR | Fails the gate; the message shows the measured bytes, the budget and the runtime |
| Learner's Mac app | Mono interpreter | Advice only: the run still passes and the feedback says that the sizes differ between runtimes |

CoreCLR pack-time measurements are authoritative; every report names its runtime (`AllocationReport.Runtime`) and says whether it was enforced.
A call too slow to repeat is never run past the time limit: before each call the meter checks that the elapsed time plus the slowest call so far
still fits, and otherwise stops. At publishing (limit five seconds) that is a publishing error. On the Mac the limit is half the test's remaining time
budget, so a correct answer can't become a time-out; the outcome carries an `AllocationReport` with no bytes, and the feedback says that the allocation
wasn't checked, so a budget is never skipped silently. Android doesn't run C# (see above).

## Editor and drafts

`CodeEditor` takes a `Language` (default `python`); `csharp` selects the CodeMirror clike C# mode.
Drafts use the existing `ICodeDraftStore` autosave and sync path.

## Multi-file exercises (#152)

A C# write-code step can have 2–8 files, for exercises about types: an interface in one file, its implementations in others, and the function the
tests call in a third. Each file is compiled under its own name, so a compile error reads `C# compilation failed in Square.cs, line 3.`
A single-file step is unchanged and still reads `C# compilation failed.`

Authoring replaces the step's own `starter` and `solution` with `files`:

```yaml
- id: step.csharp.area
  type: write-code
  language: csharp
  entrypoint: TotalArea
  largeScreenPreferred: true
  files:
    - name: IShape.cs
      starter: |
        public interface IShape { int Area(); }
      solution: |
        public interface IShape { int Area(); }
    - name: Square.cs
      starter: |
        public record Square(int Side) : IShape { public int Area() => 0; }
      solution: |
        public record Square(int Side) : IShape { public int Area() => Side * Side; }
    - name: Program.cs
      starter: |
        int TotalArea(int n) => 0;
      solution: |
        int TotalArea(int n) => Enumerable.Range(1, n).Sum(i => new Square(i).Area());
  tests:
    - { input: '3', expected: '14' }
  phoneAlternative: { ... }
```

Rules, enforced by `validate`, `pack` and `publish` (and again by the compiler):

- 2–8 files, C# only, write-code steps only; no `walkthrough` (its trace rides on one source), and no `starter`/`solution` beside `files`.
- Names are plain `Name.cs` files (letters, digits, underscores; at most 32 characters), unique ignoring case. `reference.cs` and `test-N.cs`
  are the generated harness's own sources, so they are reserved.
- Exactly one file may hold top-level functions (the entrypoint's); the others hold types. Top-level functions in two files is an error naming both.
- `System`, `System.Collections.Generic` and `System.Linq` are implicitly imported in every file, as in a single-file exercise.
- Every file has a `solution`; the reference solution is compiled from all of them and run against the tests, as for one file. All other checks
  (denied APIs, the 64 KiB limit — now over all files together — probes, printing, allocation budgets) apply across the files, and policy errors name
  the file: `Reader.cs, line 3`.
- The step id plus the longest file name has to fit in 64 characters, so every file's draft key can sync.

In the app, a tab per file sits above the editor (`role="tablist"`). Each file autosaves as its own draft under `{stepKey}/{fileName}`, through the
same `ICodeDraftStore` and sync as any other step, so there is no server or sync change; a file equal to its starter is stored as no draft, and
"Reset to starter" restores every file. Run sends all files to `ICodeRuntime.RunTestsAsync(IReadOnlyList<CodeFile>, …)`. A runtime that doesn't
override it (Python) refuses more than one file. When the error is in a file that isn't open, its tab opens, the line is marked and highlighted, and a
dot on the tab says which file it is until the next run. Progress events record the files joined as `// ===== Name =====` sections. The editor's
JavaScript is unchanged: tabs switch by `GetCodeAsync` and `SetCodeAsync`.

## Verification

```sh
dotnet test tests/Brilliant.Runtime.CSharp.Tests
dotnet test tests/Brilliant.Lessons.UI.Tests
dotnet test tests/Brilliant.Cli.Tests
(cd src/Brilliant.Lessons.UI/editor && npm test)
dotnet build src/Brilliant.App -f net10.0-maccatalyst -p:BrilliantApp=csharp
dotnet build src/Brilliant.App -f net10.0-android -p:BrilliantApp=csharp
```
