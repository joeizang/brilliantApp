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
A call too slow to repeat (more than five seconds at publishing) is a publishing error. On the Mac the measurement is skipped when it won't fit in
the test's remaining time budget. Android doesn't run C# (see above).

## Editor and drafts

`CodeEditor` takes a `Language` (default `python`); `csharp` selects the CodeMirror clike C# mode.
Drafts use the existing `ICodeDraftStore` autosave and sync path.

## Verification

```sh
dotnet test tests/Brilliant.Runtime.CSharp.Tests
dotnet test tests/Brilliant.Lessons.UI.Tests
dotnet test tests/Brilliant.Cli.Tests
(cd src/Brilliant.Lessons.UI/editor && npm test)
dotnet build src/Brilliant.App -f net10.0-maccatalyst -p:BrilliantApp=csharp
dotnet build src/Brilliant.App -f net10.0-android -p:BrilliantApp=csharp
```
