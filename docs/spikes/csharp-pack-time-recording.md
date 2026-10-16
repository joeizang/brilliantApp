# Spike: pack-time C# recording in the CLI (#139)

**Proceed, with three small gaps to close in the real recorder.** From one C# snippet the spike produced (1) a trace the existing trace player plays unmodified, (2) lowered C# and IL that show the async state machine and the `List<T>` enumerator, and (3) a two-method benchmark with mean, allocated bytes and machine info. Trace and lowering output are byte-for-byte reproducible and take about half a second each in a cold CLI process. Benchmarks are the only non-deterministic artefact and cost about thirteen seconds. The gaps: the final state needs a synthesized `return`, loop conditions never get a step, and snapshots do not name the method they came from (see [Gaps](#gaps-the-real-recorder-must-close)).

## Reproduction and environment

Measured on 16 October 2026: Apple M4 Max, macOS 26.6.2, .NET SDK **10.0.302**, CoreCLR runtime **10.0.10**, Roslyn **5.0.0**, Basic.Reference.Assemblies.Net100 **1.8.9**, ICSharpCode.Decompiler **11.1.0.9782**, BenchmarkDotNet **0.15.8**, C# 14.

The [throwaway source snapshot](https://github.com/joeizang/brilliantApp/tree/b4908afeaeb7e2755f3c7363122f1aa9629ed30c) is on `codex/139-recording-experiment`; this report is the only mergeable change. Every artefact quoted below is a file in that snapshot under `tools/Brilliant.Recording.Spike/samples/`.

```sh
dotnet run --project tools/Brilliant.Recording.Spike -- trace out/    # trace.json, rewritten.cs, example.cs
dotnet run --project tools/Brilliant.Recording.Spike -- lower out/    # lowered.cs, lowered.il
dotnet run -c Release --project tools/Brilliant.Recording.Spike -- bench out/   # bench.json
dotnet test tests/Brilliant.Lessons.UI.Tests --filter RecordedTraceSpikeTests   # plays the trace in TracePlayer
```

Each mode ran twice, each in a fresh process. Times are therefore cold-process times: each includes Roslyn's one-off start-up, which a CLI building a whole pack pays once, not per snippet.

| Artefact | Phase | Run 1 | Run 2 |
|---|---|---:|---:|
| Trace | parse + rewrite (includes building the semantic model) | 331 ms | 322 ms |
| | emit | 195 ms | 195 ms |
| | run under `ProbeBudget` | 2 ms | 2 ms |
| | snapshots → `TraceResult` | 1.6 ms | 1.6 ms |
| Lowering | emit (Release) | 509 ms | 502 ms |
| | decompile | 304 ms | 276 ms |
| | IL disassembly | 15 ms | 13 ms |
| Benchmark | whole run, `ShortRun` job | 12.7 s | 12.9 s |

(The lowering emit is slower than the trace emit only because lowering was the first Roslyn user in its process.)

## 1. Recorded trace

**Pipeline.** The production pieces already do most of the work, so the recorder is about 120 lines of glue:

1. Parse the snippet and append `return;` to the entry method (see gap 1).
2. `ProbeRewriter.Rewrite(tree, captureLocals: true, semanticModel)` inserts `ProbeBudget.Current!.Snapshot(line, names, values)` before every statement. The semantic model must come from the tree being rewritten.
3. Emit to memory, load into a collectible context, run the entry method once on a dedicated 4 MiB-stack thread with a `ProbeBudget` (100,000 steps, 2 s, 500 snapshots).
4. Convert each frozen `ProbeSnapshot` into a `TraceFrame`: scalars become `TraceLocal(name, type, repr)`; arrays and `List<T>` also become a `TrackedArray` with `drawings: ["array"]`.

**Example artefact.** The snippet is an insertion sort over `{ 5, 2, 4, 1 }` ([example.cs](https://github.com/joeizang/brilliantApp/blob/b4908afeaeb7e2755f3c7363122f1aa9629ed30c/tools/Brilliant.Recording.Spike/samples/example.cs)). The output is the JSON `TraceResult` the existing views read ([trace.json](https://github.com/joeizang/brilliantApp/blob/b4908afeaeb7e2755f3c7363122f1aa9629ed30c/tools/Brilliant.Recording.Spike/samples/trace.json), 25 frames, 20,364 bytes, 664 bytes gzipped). Its shape, abridged:

```json
{
  "status": 0,
  "frames": [
    { "event": "line", "line": 6, "function": "Run",
      "locals":  [ { "name": "a", "type": "int[]", "repr": "[5, 2, 4, 1]" } ],
      "tracked": { "a": { "type": "int[]", "cells": ["5","2","4","1"], "more": 0, "drawings": ["array"] } } },
    "… 23 more line frames …",
    { "event": "return", "line": null, "function": "Run",
      "locals":  [ { "name": "a", "type": "int[]", "repr": "[1, 2, 4, 5]" } ],
      "tracked": { "a": { "type": "int[]", "cells": ["1","2","4","5"], "more": 0, "drawings": ["array"] } } }
  ],
  "stdout": "", "message": null, "traceback": null, "errorLine": null
}
```

(The real file also carries the null `tables`/`lists`/`calls`/`trees`/`grids`/`graphs`/`heaps` members the format already has.) A `line` frame is the state just before that line runs; the closing `return` frame with a null `line` is the state the method finished in, the same convention the Python tracer uses.

The rewritten source the compiler actually saw is in [rewritten.cs](https://github.com/joeizang/brilliantApp/blob/b4908afeaeb7e2755f3c7363122f1aa9629ed30c/tools/Brilliant.Recording.Spike/samples/rewritten.cs); it is the same rewrite the Mac runtime already runs for learners.

**Plays in the existing player.** `RecordedTraceSpikeTests` parses the JSON with `TraceResultJson.Parse`, builds states with `TraceModel.Build(result, [Visual("a", Array, pointers ["i","j"])])` and renders the unmodified `TracePlayer`, exactly as `TraceRun.razor` does after a live run. It asserts "Step 1 of 25", steps to "Step 25 of 25" and reads the final cells `1 2 4 5`. It passes. The pointer names come from the pack's `Visual` declaration, not from the recording, so authors declare them as they do for Python traces.

**Not demonstrated: pasting into a pack.** `TraceStep` has no field for a recorded trace yet, and `CSharpRuntime.TraceAsync` returns "C# traces use recorded content; live tracing is not available". The spike therefore feeds the JSON to the player directly. Storing the recording in the pack and routing C# trace steps to it is #153.

## 2. Lowered C# and IL

**Pipeline.** Emit the snippet to an in-memory PE in Release mode (the async state machine is then a struct), open it with `ICSharpCode.Decompiler` and print the whole module, then print IL with `ReflectionDisassembler`. The settings that matter:

- `new DecompilerSettings(LanguageVersion.CSharp1)` turns off async/await reconstruction and most other reconstructions, which is what leaves the state machine visible.
- It does **not** turn off `foreach`, `using` or `lock`; those need `ForEachStatement = false`, `UsingStatement = false`, `LockStatement = false` explicitly. With only `CSharp1`, the `List<int>` loop came back as a `foreach` and the enumerator was hidden.
- `AutoLoadAssemblyReferences = false` and `ThrowOnAssemblyResolveErrors = false`; reference types still resolved against the runtime directory via `UniversalAssemblyResolver`.

**Example artefacts.** The snippet has `async Task<int> FetchAsync(int n)` (one `await Task.Delay(1)`) and `int Total(List<int> items)` (a `foreach` sum). Lowered C# ([lowered.cs](https://github.com/joeizang/brilliantApp/blob/b4908afeaeb7e2755f3c7363122f1aa9629ed30c/tools/Brilliant.Recording.Spike/samples/lowered.cs), 107 lines), abridged:

```csharp
[AsyncStateMachine(typeof(<FetchAsync>d__0))]
public static Task<int> FetchAsync(int n)
{
    <FetchAsync>d__0 stateMachine = default(<FetchAsync>d__0);
    stateMachine.<>t__builder = AsyncTaskMethodBuilder<int>.Create();
    stateMachine.n = n;
    stateMachine.<>1__state = -1;
    stateMachine.<>t__builder.Start(ref stateMachine);
    return stateMachine.<>t__builder.Task;
}

// inside struct <FetchAsync>d__0 : IAsyncStateMachine
private void MoveNext()
{
    int num = <>1__state;
    int result;
    try
    {
        TaskAwaiter awaiter;
        if (num != 0)
        {
            awaiter = Task.Delay(1).GetAwaiter();
            if (!awaiter.IsCompleted)
            {
                num = (<>1__state = 0);
                <>u__1 = awaiter;
                <>t__builder.AwaitUnsafeOnCompleted(ref awaiter, ref this);
                return;
            }
        }
        else { awaiter = <>u__1; <>u__1 = default(TaskAwaiter); num = (<>1__state = -1); }
        awaiter.GetResult();
        result = n * 2;
    }
    catch (Exception exception) { <>1__state = -2; <>t__builder.SetException(exception); return; }
    <>1__state = -2;
    <>t__builder.SetResult(result);
}

public static int Total(List<int> items)
{
    int num = 0;
    List<int>.Enumerator enumerator = items.GetEnumerator();
    try
    {
        while (enumerator.MoveNext())
        {
            int current = enumerator.Current;
            num += current;
        }
        return num;
    }
    finally
    {
        ((IDisposable)enumerator/*cast due to constrained. prefix*/).Dispose();
    }
}
```

IL ([lowered.il](https://github.com/joeizang/brilliantApp/blob/b4908afeaeb7e2755f3c7363122f1aa9629ed30c/tools/Brilliant.Recording.Spike/samples/lowered.il), 241 lines), the `Total` loop:

```
.try {
    IL_0009: br.s IL_0017
    IL_000b: ldloca.s 1
    IL_000d: call instance !0 valuetype [System.Collections]System.Collections.Generic.List`1/Enumerator<int32>::get_Current()
    IL_0012: stloc.2
    IL_0013: ldloc.0
    IL_0014: ldloc.2
    IL_0015: add
    IL_0016: stloc.0
    IL_0017: ldloca.s 1
    IL_0019: call instance bool valuetype [System.Collections]System.Collections.Generic.List`1/Enumerator<int32>::MoveNext()
    IL_001e: brtrue.s IL_000b
    IL_0020: leave.s IL_0030
} finally { … Dispose … }
```

**Cleanup the pack step would need.** The whole-module output includes assembly attributes (`CompilationRelaxations`, `Debuggable`, `AssemblyVersion`), the `[NullableContext]`/`[Nullable]` attributes, and ILSpy comments on explicit interface implementations. Showing only the learner's members means decompiling per type or per method and dropping the attribute block. Release mode drops local names (`num`, `current`); that is the honest lowering, but a Debug emit would keep `sum` and `item` for readability at the cost of a class-shaped state machine. Which is better for teaching is a content decision for #154.

## 3. Benchmark

**Pipeline.** A BenchmarkDotNet run of two methods over the same 1,000-element `int[]`: a hand-written `for` loop (baseline) against `Where(x => x % 2 == 0).Sum()`. It uses `Job.ShortRun` with `InProcessNoEmitToolchain` (no per-benchmark `dotnet build`) and `MemoryDiagnoser`, in a Release build. The result is [bench.json](https://github.com/joeizang/brilliantApp/blob/b4908afeaeb7e2755f3c7363122f1aa9629ed30c/tools/Brilliant.Recording.Spike/samples/bench.json); run 1:

| Method | Mean | StdDev | Allocated | Iterations |
|---|---:|---:|---:|---:|
| ForLoop (baseline) | 308.7 ns | 3.9 ns | 0 B | 3 |
| Linq | 1,178.5 ns | 167.0 ns | 48 B | 3 |

Run 2 gave 309.2 ns ± 2.3 and 1,269.9 ns ± 58.6, with the same 0 B and 48 B. The JSON shape is `{ rows: [{ method, meanNs, stdDevNs, allocatedBytes, iterations }], host, runtime, job, totalMs }`. Its `host` string is BenchmarkDotNet's own: "BenchmarkDotNet v0.15.8, macOS Tahoe 26.6.2 (25G83) [Darwin 25.6.0]; Apple M4 Max, 1 CPU, 16 logical and 16 physical cores; .NET SDK 10.0.302". Its `runtime` field holds the SDK version, not the runtime's (10.0.10); the real recorder should read `RuntimeInformation.FrameworkDescription` instead.

**Not tested: benchmarking a learner snippet.** The two benchmarked methods here are compiled into the spike project. Benchmarking code a pack author pastes would mean emitting the snippet and handing BenchmarkDotNet a type loaded from those bytes (or letting it generate and build a project, which is the slow out-of-process path). The spike did not try either, so this remains an open question for the pack-time benchmark track.

## Determinism

| Output | Byte-stable across runs? | Evidence |
|---|---|---|
| Trace JSON, rewritten source | **Yes** | Two fresh processes, `cmp` identical. The trace holds no timings, addresses or thread ids. |
| Lowered C#, IL | **Yes** | Identical text across runs; emitted PE bytes also identical (SHA-256 `128761D8…`) |
| Benchmark `meanNs`, `stdDevNs` | **No** | Linq mean differed by 7.8% between runs (1,178 vs 1,270 ns); its std dev differed 2.9×. `ShortRun` has only 3 iterations, so the error bars are wide. |
| Benchmark `allocatedBytes` | **Yes** (in practice) | 0 B and 48 B both runs; allocation per operation is a count, not a timing |
| Benchmark `host`/environment | Per machine | Differs across machines, OS updates and SDK updates |

Two details were needed to get the stable rows. Roslyn's `CSharpCompilationOptions` does **not** default to deterministic output (the MSBuild targets set it); until `deterministic: true` was passed the same source produced a different PE every run, though the decompiled text was the same either way. And a random suffix in the assembly name, which the production runtime adds per compile, must be fixed for the recorder or it leaks into any output that shows the name. Run-to-run order of locals is stable because the converter sorts them by name.

Consequence for packs: trace and lowering can be regenerated in CI and diffed, so a stale recording is a failing check. Benchmarks cannot be diffed byte-for-byte. They should be committed once per pack build with their machine info, shown with that provenance, and regenerated only on purpose. A learner-visible "faster by about 4×" claim is safe; a quoted nanosecond figure is not.

## Gaps the real recorder must close

1. **The final state is not captured by probes alone.** Probes run before a statement, so the state after the last statement is never seen. The spike appends `return;` to the entry method so the last snapshot becomes the closing `return` frame. The real recorder should do this itself (and handle expression-bodied and non-void entries) rather than require authors to write it.
2. **Loop conditions and `for` headers get no step.** Only statements are probed. In the trace, line 11 (`while (j >= 0 && a[j] > key)`) and the `i++` of the `for` never appear; the player jumps from the end of the body straight to the next statement. For insertion sort this is acceptable and the 25 steps are easy to follow, but a lesson about "when does the loop test run" would need probes added to conditions.
3. **Snapshots carry a line but no method name.** `ProbeSnapshot` is `(Line, Locals)`, so every frame in the spike is labelled with the entry method. A snippet that calls helper methods would show their lines under the wrong function name, and there is no call-depth information for the player's call-stack view (`calls`). The rewriter knows the enclosing method when it inserts a probe, so adding the name is a small change to `ProbeRewriter`/`ProbeBudget`, but it is a change to production code and belongs in the recorder issue, not this spike.

Smaller, known limits: only arrays and `List<T>` are frozen as sequences (at most 128 items, 32 shown by the spike's converter), other objects become a type name; `Console.Out` capture was not tried, so `stdout` is always empty here; the spike records one run of one entry method, with the 500-snapshot cap turning anything longer into a truncated recording that should be reported as such.

## Recommendation

Build the recorder on the existing `ProbeRewriter` and `ProbeBudget` rather than anything new, add method names to snapshots, and make trace and lowering CI-checked artefacts. Keep benchmarks as a separate, explicitly provenance-labelled artefact, and settle how learner code reaches BenchmarkDotNet before committing to that track.
