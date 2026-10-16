# C# reference checks (#149)

`validate`, `pack` and `publish` compile every C# write-code reference solution and
run its hidden tests before producing a pack. Python exercises retain CPython.
Default dispatch is per exercise language, including mixed-language authoring packs;
an explicitly supplied test runner remains authoritative and must match the language.
Solutions are authoring-only and are omitted from packed lessons.

```sh
dotnet run --project tools/Brilliant.Cli -- validate --app apps/csharp/manifest.yaml
dotnet test tests/Brilliant.Code.CSharp.Tests
dotnet test tests/Brilliant.Cli.Tests
```

Pack pins are `targetFramework: net10.0` and `langVersion: '14'`. Core's `CSharpPins`
shares these values between the validator and compiler. Roslyn is fixed at 5.0.0;
Basic.Reference.Assemblies.Net100 is fixed at 1.8.9 and embeds the framework reference
assemblies. Compilation never discovers reference assemblies from installed SDKs or
uses the current process's implementation assemblies as framework references.

A solution declares top-level local functions. It may include leading usings and
supporting types after the functions:

```yaml
language: csharp
entrypoint: Double
starter: 'static int Double(int n) => 0;'
solution: 'static int Double(int n) => n * 2;'
tests:
  - input: '4'
    expected: '8'
```

`input` is argument source, including an empty string for no arguments; `expected`
is a C# expression. Calls and expectations compile together with the solution, so
records and helper types are available in both. Equality compares arrays, lists,
dictionaries (independent of entry order), records including nested collections,
and tuples structurally. Scalar values use CLR equality. Diagnostics bound traversal
and output, preserve compile error source lines, and identify lesson, step, test,
expected and actual values. A compile failure, budget failure or worker failure blocks
packing and publishing.

The reusable `Brilliant.Code.CSharp` library contains a Roslyn syntax rewriter and
thread-local budget. Method, local function, constructor, accessor, operator and
lambda bodies enter a call-depth budget and release it in `finally`. Loop bodies tick
before each iteration, including iterations reached by `continue` and loops inside
learner `finally` blocks. Typed, bare and filtered catches gain an exception filter
excluding `BudgetExceededException`; existing filters retain their original behavior
for ordinary exceptions. Budget exhaustion stays sticky even when the CLR discards
a probe exception thrown inside a filter; method exit and the runner recheck it. Optional line probes use the original semantic model to
capture definitely assigned, boxable locals and parameters on original source lines,
including unbraced branches and expression bodies. Snapshots freeze arrays, framework
lists and object fields without invoking learner getters; captures are bounded to
16 levels, 128 elements per collection and 1,000 visited values per local. Cycles and traversal limits are represented explicitly; collection captures stop
at 128 elements.

The CLI runs compiled code on a dedicated 4 MiB CoreCLR thread, with a maximum depth
of 128, 100,000 probes and a two-second cooperative time budget per test. Each solution
runs in a separate worker process with a 15-second outer watchdog, which kills the
process tree if a library call blocks without hitting a probe. The assembly uses a
collectible load context and unload is requested after checking. This does not claim
verified unload/memory behavior on Mono or the Mac interpreter; #138 owns that spike.

This gate currently accepts synchronous safe C#. Async/iterator/unsafe code, `goto`,
stack allocation, finalizers, source-local `#define`/`#undef`, constructor initializer arguments, and calls/object
construction in field/property initializers fail closed. Expression-tree lambdas
cannot be converted to block lambdas and therefore fail compilation after rewriting.
These limits avoid silently accepting constructs without the required budget rules.
The gate executes trusted authored content; the worker is execution containment, not
an OS security sandbox. It does not restrict filesystem or network access.

#150 will connect native Mac execution. #153 will turn the probe snapshots into
recorded trace packs. This issue deliberately does not register a C# runtime in the
native app; native Run/trace and physical device interaction are not verified here.
