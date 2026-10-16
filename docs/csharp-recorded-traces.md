# Recorded C# traces

Issue [#153](https://github.com/joeizang/brilliantApp/issues/153), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)).

A C# `trace` step (or a solution walkthrough) is written exactly like a Python one: `code` plus `visuals`. The difference is
where it is run. Python records its trace on the device, in Pyodide. C# is not run on the device at all, so **the CLI records
it when the pack is built** and writes the recording into the pack. The trace player plays the recording, so it works on Mac and
Android alike with no C# runtime installed.

```
lesson.yaml  type: trace (language: csharp)
  │  brilliant validate / pack
  ▼
ReferenceSolutionChecker.CheckTraces ──► worker process (--csharp-trace-worker) ──► CSharpTraceRecorder.Record(code, visuals)
  │                                                                                      │ probe rewriter + budget, one snapshot per statement
  ▼                                                                                      ▼
TraceStep.Recorded / Walkthrough.Recorded  (a TraceResult, the same JSON the Python tracer produces)
  │  pack
  ▼
TraceRun: Recorded ?? Runtimes.Resolve(Language).TraceAsync(...)  ──►  TraceModel.Build  ──►  TracePlayer
```

## What is recorded

`CSharpTraceRecorder` (`src/Brilliant.Runtime.CSharp`) compiles the code with `ProbeRewriter`, which inserts a probe before each
statement, runs it on a dedicated thread under the usual `ProbeBudget`, and turns each probe into a `TraceFrame`:

| Frame field | C# source |
| --- | --- |
| `Event`, `Line` | `"line"` and the 1-based line about to run; the last frame is `"return"` with no line (the state after the last statement) |
| `Function` | the method, local function or lambda the probe is in; top-level code is `Main` |
| `Locals` | the variables in scope, sorted by name (ordinal) so the order never depends on declaration order |
| `Tracked` | each variable a `visuals` entry names: `array`, `stack` or `queue` drawings |
| `Stdout` | everything `Console.Write*` printed, with `\n` line endings |

Collections map onto the existing views by shape: an array or `List<T>` is a row of cells front to back, a `Stack<T>` is drawn bottom
to top, a `Queue<T>` front to back. Types are shown as C# names (`int[]`, `List<string>`, `Dictionary<string, int[]>`); see `TypeNames`.

### Limits

These mirror the Python tracer so the player behaves the same: 300 frames, 24 locals per frame, 40 cells per array (the rest are
counted in `More`), 60-character reprs (24 for a cell) and 4000 characters of stdout. A run that hits a limit, an infinite loop or
the step/time budget is recorded as `Truncated` with a message; a run that throws is recorded as `Error` with the frames up to the
throw. Both play like their Python equivalents.

Known gaps: loop conditions and `for` headers get no step of their own, and a top-level `return;` before the end of the code leaves
no closing frame. `async`, iterators, `unsafe`, `#define`/`#undef` and interactive console input are refused.

## Determinism

Re-recording the same source gives byte-identical output (and so a byte-identical pack): the assembly is built with `deterministic`,
locals are sorted, stdout is normalised, and nothing that depends on time, thread or address is recorded. The one exception is the 2-second wall-clock budget: an example that comes near it could record `Ok` on a fast machine and `Truncated` on a slow one. Only the step and frame limits are reproducible, so teaching examples must finish well inside it. Golden files in
`tests/Brilliant.Runtime.CSharp.Tests/Traces/` (`*.snippet` with its `*.trace.json`) pin the output for a fixed set of snippets.
After an intended change, regenerate them with `UPDATE_GOLDENS=1 dotnet test tests/Brilliant.Runtime.CSharp.Tests` and review the diff.

## In the pack

`TraceStep.Recorded` and `Walkthrough.Recorded` hold the `TraceResult`. They are never authored: `validate` records them and
`pack` attaches them. They are written with `TraceResultJson.Serialize` (camelCase, string enums, nulls omitted) inside the pack's
JSON like any other field. `TraceStepView` and `HintLadder` hand the recording to `TraceRun`, which plays it and never asks a runtime.
Step ids are unique across a pack, so recordings are matched to steps by `(step id, walkthrough or trace)`.

Recording runs in a child process (`dotnet Brilliant.Cli.dll --csharp-trace-worker`) that is killed if it overruns, the same containment
as the reference-solution gate ([csharp-reference-checks.md](csharp-reference-checks.md)); it only runs on content that is already
structurally valid. A trace that fails to record fails validation with the reason.

## Not yet recorded

Only variables, arrays, `List<T>`, `Stack<T>` and `Queue<T>` are recorded. Dictionary/set tables, linked lists, binary trees, heaps,
grids, graphs and the call stack need object identity in the probe's snapshot (so that two snapshots can tell that a node is the same
node). Until that lands, `validate` refuses those `visuals` for C# with "'X' drawings of C# code are not recorded yet…". That work is the
remaining part of #153 and is tracked in its own issue.
