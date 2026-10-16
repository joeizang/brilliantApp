# Spike: Roslyn in native Release apps (#138)

**Mac in-process: proceed with a bounded compilation lifetime. Android execution is feasible; keep the on-device feature for later.** Both Release apps compiled and ran emitted C#, stopped a runaway loop and stopped recursion at depth 128 on a 4 MiB thread stack. Native load contexts remained alive after forced GC, and allocation sizes differ from CoreCLR. Do not design around successful Mono unloading or apply CoreCLR byte thresholds as device pass/fail rules.

## Reproduction and environment

Measurements were taken on 16 October 2026 with SDK **10.0.302**, Roslyn **5.0.0**, Basic.Reference.Assemblies.Net100 **1.8.9**, target **net10.0**, language **C# 14**:

- Apple M4 Max, arm64, Mac Catalyst 26.6.0, native .NET 10.0.11. The universal Release build used `UseInterpreter=true`; `IsDynamicCodeSupported=true`, `IsDynamicCodeCompiled=false`. Only its arm64 slice was executed.
- Physical Samsung Galaxy **SM-S926U1**, Android 16/API 36, arm64-v8a, native .NET 10.0.11. Signed Release APK, default Android AOT settings; emitted assemblies ran successfully, with both dynamic-code flags true.
- CLI CoreCLR .NET 10.0.10, arm64, Release harness on the same Mac. Default tiering for timings; a second run disabled tiering to check allocation stability.

The [throwaway source snapshot](https://github.com/joeizang/brilliantApp/tree/cd9f3a48eb40b765e65d3e0548114415d7d1e223) is on `codex/138-roslyn-experiment`, based on merged #149. Its isolated flavor is `roslyn-spike`, ID `com.joeizang.brilliantapp.roslynspike`. This report and its measurement appendices are the only mergeable changes. DSA and C# app identities, installed progress and production source were not modified by the experiment.

```sh
dotnet run --project tools/Brilliant.Roslyn.Spike.Cli -c Release -- /tmp/coreclr.json
DOTNET_TieredCompilation=0 dotnet run --project tools/Brilliant.Roslyn.Spike.Cli -c Release -- /tmp/coreclr-no-tiering.json
scripts/build-app.sh roslyn-spike mac Release
scripts/build-apk.sh --app roslyn-spike
# Save the signed spike APK before the same output path is overwritten:
RoslynSpikeEnabled=false scripts/build-apk.sh --app roslyn-spike
```

Open the Mac bundle or install/start the spike APK. The throwaway page starts the harness during initialization and writes `roslyn-spike.json` to app data. Android also writes external cache; the physical result was pulled from `/sdcard/Android/data/com.joeizang.brilliantapp.roslynspike/cache/roslyn-spike.json`. Mac's result was read from its own container's `Data/Library/roslyn-spike.json`. These files contain execution results and budget failures, providing evidence beyond app startup. Ordinary lesson navigation, native editor Run and native pairing were not exercised.

## Compile, execute and stop (milliseconds)

| Measurement | CLI CoreCLR | Mac Release | Galaxy Release |
|---|---:|---:|---:|
| Cold compile | 445.581 | 375.264 | 930.585 |
| Warm compile, median of five | 2.548 | 12.711 | 2.851 |
| First loop run | 0.559 | 1.219 | 2.888 |
| Warm loop run, median of five | 0.209 | 0.744 | 0.670 |
| Stop runaway loop | 2.330 | 7.637 | 7.097 |
| Stop recursion | 1.522 | 1.568 | 7.350 |

“Cold” means the first compilation in a fresh process, including reference-set initialization, parse, diagnostics, rewrite and emit. It does not mean an empty OS file cache. Five fresh compilations in the same process give the warm median. Compilation uses a new assembly name each time. Load/delegate creation is measured separately in the JSON; thread creation is outside the execution clock.

The loop sums 0 through 9,999 and returns **49,995,000** on every platform. The infinite loop stops on the **100,001st probe** with the 100,000-step budget. This measures step-budget stopping, rather than independently proving a native wall-time deadline. Recursion throws `BudgetExceededException` when entering beyond the **128-frame instrumentation limit**, with the counter back at **zero** after unwinding. Every invocation uses a dedicated thread with an explicit **4,194,304-byte stack**. This validates the tested snippets and stack configuration, rather than arbitrary frame sizes.

## Allocations: CoreCLR remains authoritative

Bytes per call after 1,000 warm-up calls, across five samples of 1,000 calls:

| Snippet | CoreCLR steady samples | Mac Release | Galaxy Release |
|---|---:|---:|---:|
| Return a constant | 0 | 0 | 0 |
| Box an int into static Sink | 24 | 24 | 24 |
| Allocate `new int[16]` into Sink | 88 | 96 | 96 |
| Capture an int in `Func<int>` into Sink, then call it | 88 | 152 | 152 |

The measurement is `GC.GetAllocatedBytesForCurrentThread` around direct `Func<long>` calls on the dedicated thread. Compile, load, delegate creation, thread creation and warm-up are excluded; probes are included. Full GC occurs between warm-up and measurement. A static Sink makes the allocations observable, and a consumed checksum keeps calls observable. All native samples have the exact table values.

Default CoreCLR boxing samples were 24, 24, 24, 24 and 30.192 B/call: one sample contained 6,192 extra bytes. The controlled `DOTNET_TieredCompilation=0` run produced 24 in all five samples; other steady values were unchanged. This does not identify the extra allocation's cause. Record warm-up and runtime settings, and establish stable CoreCLR measurements for #151. **Pack-time CoreCLR budgets are authoritative; native measurements must name their runtime and remain advisory or CLI-only.** A strict 88-byte array/closure threshold would reject equivalent native code here.

## Unloading and retained memory

The 100-cycle phase compiles a fresh loop assembly, creates a collectible ALC, loads, runs, calls `Unload`, returns from a non-inlined helper and retains only a weak reference. Checkpoints at 1, 10, 25, 50, 75 and 100 cycles perform three rounds of Collect/WaitForPendingFinalizers/Collect and `GetTotalMemory(true)`; the final check repeats collection. The baseline follows eight setup load contexts from the preceding latency/budget/allocation phases.

| Runtime | Contexts alive after final GC | Managed heap change from cycle-0 baseline | Process RSS at cycles 1 → 100 |
|---|---:|---:|---:|
| CoreCLR | 0/100 | -437,208 B | 119,422,976 → 121,552,896 B |
| Mac | 100/100 | +45,376 B | 297,074,688 → 303,415,296 B |
| Galaxy | 100/100 | +43,280 B | 449,671,168 → 451,624,960 B |

CoreCLR collected all tracked contexts; both native runtimes retained all 100 even though `Unload()` returned. Treat these native contexts as non-collectible for the implementation. Managed heap and RSS also include compiler caches and app activity; their changes are not isolated per-assembly accounting. A small managed delta does not demonstrate unloading, and 100 small assemblies do not establish unlimited-session safety.

For #150, reuse a bounded cache of compiled submissions and impose an initial **50 emitted-assembly loads per process** lifetime, then save drafts/progress and require a clean app restart before compiling again. Count actual loads, including evictions/recompilations, rather than only cache entries; `Unload()` cannot recover that budget here. Fifty is a conservative operational starting point below the tested workload, not a proven memory ceiling for arbitrary submissions. Keep source/output size bounded and monitor session memory while validating real exercises. Do not force-kill a thread or the app as a cooperative-budget mechanism.

## APK cost

Same flavor, identity, content, signing key, Release settings and **arm64-v8a + x86_64** ABIs; compiler-enabled versus `RoslynSpikeEnabled=false`:

- Baseline: **42,365,973 bytes** (40.40 MiB).
- Roslyn/reference/probe-enabled: **60,884,149 bytes** (58.06 MiB).
- Difference: **+18,518,176 bytes** (**+17.66 MiB**, +43.7%).

The baseline restore graph excludes CodeAnalysis, Basic.Reference.Assemblies and the probe project. Compiler/reference assemblies were conservatively preserved with `TrimmerRootAssembly RootMode="All"`; this is a measured safe configuration, not the smallest possible APK. The Galaxy executed the saved compiler-enabled signed APK. The compiler-free size baseline was built separately and was not installed over another app. Hashes and ABI inventories are in [apk-sizes.json](csharp-roslyn-release/apk-sizes.json).

## Integration findings and recommendation

1. Embedded metadata must have a distinct MSBuild item path, such as `spike/probe-runtime.bin` with logical name `probe-runtime.dll`. Directly embedding the resolved project's DLL path made the Mac packaging pipeline place that DLL under Resources instead of MonoBundle, causing a load failure. Copying the bytes to a separate `.bin` item fixed it. Avoid `Assembly.Location` as the source of compiler metadata in packaged apps.
2. Android's trimming target requires explicit `RootMode` metadata on added root assemblies. `RootMode="All"` built and ran on the phone.
3. The first automated page used `OnAfterRenderAsync`, and the harness did not start. Initialization-based execution produced complete native result files. This workaround does not resolve or verify the separate baseline lesson-interaction automation limitation.
4. The raw `mono` field is only a `Type.GetType("Mono.Runtime")` visibility probe; it was false in these native builds and must not be used to identify the runtime. Native runtime identity comes from the platform/runtime build configuration and dynamic-code flags.

Proceed with **Mac in-process C#** in #150 using the demonstrated Release interpreter path, shared probes, dedicated stack, bounded compilation lifetime and persisted restart flow. Keep **Android on-device editing/execution for later**: technical feasibility passed on this Galaxy, but the compiler adds 17.66 MiB, native contexts were retained, and the planned phone flow can use static/prediction/recorded content. Revisit the stretch feature with an explicit lifetime and package-size decision.

Raw results: [CoreCLR default](csharp-roslyn-release/coreclr-default.json), [CoreCLR no tiering](csharp-roslyn-release/coreclr-no-tiering.json), [Mac Release](csharp-roslyn-release/mac-release.json), [Galaxy Release](csharp-roslyn-release/galaxy-release.json). The prototype stays on its throwaway branch; no runtime implementation is included in this report PR.
