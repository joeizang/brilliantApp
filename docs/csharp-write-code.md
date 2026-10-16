# C# write-code on the Mac (#150)

C# `write-code` steps run natively on the Mac (Mac Catalyst) through `Brilliant.Runtime.CSharp`.
Android never carries Roslyn or reference assemblies: the step shows its authored phone alternative
at every window size, because the rule follows the host capability (`ScreenInfo.CSharpWriteCodeAvailable`),
not the width.

## Pipeline

1. `CSharpSubmissionCompiler` (shared with the CLI's reference-solution gate, #149) compiles the
   learner's top-level functions in memory and probe-rewrites them under the pinned Roslyn/reference set.
   Compile errors return `TestRunStatus.Error` with the original line number; they are never failed tests.
2. `NativeCodePolicy` allow-lists the framework surface (collections, LINQ, math, strings, console output).
   Anything else fails before loading.
3. `CSharpRuntime` loads the image into a collectible `AssemblyLoadContext`, runs each test on its own thread under a
   `ProbeBudget` (2 s by default), and unloads the context afterward. `Console` output is captured per test
   (`ProbeConsole`), without replacing the process writers.
4. Results use the Python shape: passed count, first failure (call, expected, actual), printed output.
   Equality is structural (`CSharpValues`: collections, dictionaries, tuples, records).

## Runaway code

Probes stop loops and recursion cooperatively. A blocking framework call (`Thread.Sleep`) is cut off by an independent
watchdog and the run reports a time-out immediately. A thread that cannot unwind poisons the session, so the next run says
to quit and reopen the app (the draft is already saved). The session also stops after 50 assembly loads, as collectible
contexts are not guaranteed to be reclaimed by the runtime. Printing is capped at 16 KiB per test.

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
