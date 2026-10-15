# Plan — a C# app on the Brilliant engine (senior → principal)

> Status: Decisions settled (grill-me, 2026-10-13/14) · Owner: Joseph Izang
> Next step: PRD → issues, the same way as `docs/prd.md` → `docs/issues.md`.

## 1. Goal

Build a second app from this repo, separate from the Python/DSA app, that takes an experienced C# developer from **senior to principal** level.
It keeps everything that already works: the lesson player, Review (FSRS + mastery), Practice, Today, hints, the trace player, sync, packs and the validator.
It adds what a C# curriculum at this level needs: C# execution, views of runtime internals, and step types that teach judgement as well as syntax.

Principal-level C# is mostly about four things, not syntax:

- **Mechanics**: what the compiler lowers code into, what the runtime does with it (GC, JIT, async state machines, the memory model).
- **Performance**: allocations, boxing, `Span<T>`, pooling, measuring rather than guessing.
- **Concurrency**: correctness under interleaving, async all the way down, cancellation.
- **Design judgement**: API design, versioning, failure handling, architecture tradeoffs, reviewing code.

**Learner:** Joseph. He is an experienced C# developer, solid on the type system (struct/class/record), and not yet confident on async internals, the GC/allocations or threading correctness. Those three are the core of the app.

## 2. Decisions

| # | Decision |
|---|---|
| 1 | **One repo, many apps.** This repo becomes the home for several learning apps on one shared engine. (The kids' math app is *not* one of them. It will be its own repo, so nothing here is designed for it.) |
| 2 | **One MAUI host project built in flavors.** A build property (`-p:BrilliantApp=csharp`) selects an **app manifest** folder (`apps/<app>/`) with the app ID, display name, icon, accent color, pack, enabled runtimes and enabled step types. Each flavor installs as its own app. The host references only the runtimes its flavor enables. Adding an app means adding a folder and content, with no new code. |
| 3 | **Single learner.** No `learnerId` and no profiles. The server scopes packs, event streams and pairings by `appId` only. |
| 4 | **Step types stay a closed list.** New C# step types are added to `ContentModel`. Each app manifest lists the step types its packs may use, and the validator enforces that list. |
| 5 | **Content in this repo, one folder per app:** today's `content/` moves to `content/dsa/`, and C# lives in `content/csharp/`, each with its own `pack.yaml`. |
| 6 | **Start level: "experienced, solid on types, not yet on the runtime."** Hard-core order: **Types and the compiler (short) → Async → Memory/GC → Concurrency.** |
| 7 | **Per-lesson test-out** (an engine feature, so both apps get it). An optional "I know this — test me" at the start of a lesson asks 3–4 hard questions from the lesson's own review items. Getting all of them right completes the lesson and seeds its review items as already known (long intervals). Missing any means you do the lesson with no penalty. New event: `LessonTestedOut`. |
| 8 | **All 12 tracks.** Track 12 is about *technical* leadership only (code review, ADRs, tradeoffs, incident analysis); people management is out. |
| 9 | **Phone role: everything except writing new code.** The phone does Review, reading, predict and judgement steps, and plays traces. Write-code steps use their `phoneAlternative` on the phone. On-device Roslyn on Android is a stretch goal, decided by the spike. |
| 10 | **Traces, lowered views and benchmarks are recorded when the pack is built**, by the CLI on the Mac, so every device replays them. Live runtime tracing ("trace my own code") is a later feature. |
| 11 | **Pinned to .NET 10 / C# 14** in `content/csharp/pack.yaml` (`targetFramework`, `langVersion`). Upgrades go from LTS to LTS (next: .NET 12), each as one deliberate PR in which the validator re-records everything and shows the diff. Benchmarks are recorded on Apple Silicon (ARM64) and lessons say so. |
| 12 | **Free-text grading: rubric self-assessment always, with local-LLM feedback on top.** Your self-assessment sets the review rating, so it is deterministic and works offline. Feedback is advisory and comes from **Ollama on the Mac** (candidates: Gemma 26B, Qwen 27B, Gemma 12B). Claude is used only when you tap an explicit "Ask Claude" button, never as an automatic fallback. |
| 13 | **How devices reach Ollama:** the Mac app calls `localhost:11434` directly, which works anywhere, including offline. The phone goes through the Brilliant server to the Mac's Ollama. This is an opt-in setting and needs `OLLAMA_HOST=0.0.0.0` plus a firewall rule allowing port 11434 only from the Brilliant server's IP address on the Mac. When the Mac can't be reached, the phone says "feedback unavailable right now", and you can ask for feedback later from the Mac. |
| 14 | **Model choice by evals, not guesswork:** each rubric carries graded sample answers (strong, partial, wrong-but-confident). The CLI runs them against each candidate model, and the app manifest names the winner. |
| 15 | **Start now.** The DSA app is caught up (M5 and #127–#129 merged), so Phase 1 runs straight through. |

## 3. What carries over and what is Python-specific

| Area | Reusable as-is | Python-specific today |
|---|---|---|
| Content model (`Brilliant.Core/Content`) | Tracks, lessons, concepts, patterns, review items, hints, phone alternatives, most step types | `Comparison(CSharp, Python)`; `CodeTest` inputs/expected are Python expressions; `Visual` type names are Python recorder names |
| Review / Practice / Progress / Sync / Drafts | All of it | — |
| `Brilliant.Data`, `Brilliant.Server` | All of it | No `appId` scope on packs, events or pairing |
| `Brilliant.Lessons.UI` | Shell, Today, Review, Practice, Parsons, fill-blank, choice, predict-output, hint ladder, every trace view | Write-code and trace views call `IPythonRuntime` directly; some copy says "Python" |
| `Brilliant.Core/Python` | `TestRunResult`, `TraceResult` shapes | `IPythonRuntime`, `harness.py`, `tracer.py` |
| App host | MAUI shell, secure storage, pairing | `PyodideRuntime`, `wwwroot/pyodide`, worker JS |
| CLI validator | Schema checks, packing, publishing, Parsons checks | `CPythonRunner` for reference solutions and traces |

## 4. Architecture

### 4.1 Layout

```
apps/
  dsa/manifest.yaml           # id, name, icon, accent, pack, runtimes: [python], stepTypes: [...]
  csharp/manifest.yaml        # runtimes: [csharp], grader: { ollamaModel: ... }
src/
  Brilliant.Core              # engine (subject-agnostic)
  Brilliant.Data
  Brilliant.Lessons.UI        # shared Razor UI
  Brilliant.Server            # one server, scoped by appId; Ollama proxy
  Brilliant.Runtime.Python    # Pyodide runtime, tracer, harness (moved out of Core/App)
  Brilliant.Runtime.CSharp    # Roslyn compile/run for write-code on the Mac
  Brilliant.App               # the single MAUI host, built per flavor
content/
  dsa/                        # today's content, moved
  csharp/
tools/Brilliant.Cli           # validates every app; per-language reference runners; trace/lowering/benchmark recorders; grader evals
scripts/build-app.sh <app> <platform>
```

### 4.2 The language seam

- `IPythonRuntime` becomes `ICodeRuntime`, keyed by language (`WriteCodeStep.Language` already says which one an exercise needs).
- `CodeTest` becomes language-neutral: input and expected are source text in the exercise's language, compared with that language's equality (C#: structural comparison for arrays, records and tuples).
- `Comparison` becomes a list of labelled snippets (`[{ label, language, code }]`), for "before/after", "naive/optimised" or "C# 7 / C# 14".
- `Visual` type names become per-runtime.
- User-facing copy uses the exercise's language rather than saying "Python".

### 4.3 C# at pack time (the CLI, on the Mac)

- **Reference solutions:** compiled with Roslyn against the pinned reference assemblies and run against each step's tests. This is a publishing gate, as it is for Python.
- **Traces:** a Roslyn syntax rewriter inserts probes (line + locals) and runs the code. The recorded snapshots go into the pack in the same shape the existing trace views read.
- **Lowered views:** Roslyn + ICSharpCode.Decompiler produce lowered C# and IL for a snippet (`async`, `foreach`, `yield`, closures, `using`, pattern matching, records).
- **Benchmarks:** BenchmarkDotNet runs are recorded into the pack, so steps can ask for a prediction and then reveal the measured numbers.
- **Grader evals:** sample answers are run against the candidate Ollama models, and agreement with the authored marks is reported.

### 4.4 C# on the device (write-code)

On the Mac: Roslyn compiles in memory and attempts to load the assembly into a collectible `AssemblyLoadContext`; Phase 0 must establish that unloading works before repeated runs are enabled. MAUI Mac Catalyst and Android use **Mono**, with SGen GC, rather than the CLI's CoreCLR. Mac Catalyst Release runs with `UseInterpreter=true` (PR #130), so the spike measures compile/run latency under the Mono interpreter and memory over approximately 100 compile/run/unload cycles.

The rewriter's `Tick()` probe enforces step and time budgets (an in-process thread cannot be killed). Method-entry probes also enforce a fixed call-depth limit, incrementing on entry and decrementing in `finally`. Learner code runs on a dedicated thread with an explicit stack size; the spike must verify the depth limit fits that stack. Catch clauses exclude `BudgetExceededException`, including rewritten bare catches, and `finally` blocks are probed so learner exception handling cannot simply swallow a budget failure.

The curriculum's allocation budgets and recorded GC/JIT examples describe **CoreCLR**, measured at pack time by the pinned .NET CLI. Phase 0 compares `GC.GetAllocatedBytesForCurrentThread()` for known zero-allocation, boxing, array and closure snippets between CoreCLR and the Release Mac app. Device measurements must identify Mono/interpreter explicitly. If results disagree, CoreCLR remains the authoritative budget gate; the device result is advisory or the budget test runs only in the CLI. No device measurement is presented as a CoreCLR measurement.

Android: phone alternatives (decision 9). On-device Roslyn is only turned on if the spike shows it's cheap.

### 4.5 Free-text grading

There is one `IAnswerGrader`, with providers selected per device: **Mac: local Ollama → none (self-assess only)**; **phone: server proxy to the Mac's Ollama (opt-in) → none**. The Mac does not fall through to its own server proxy, and the phone has no local Ollama provider. **Claude** runs only on the explicit button.
Each rubric in the pack holds the points, a model answer, **red flags** (common wrong-but-plausible claims, such as "`volatile` makes `count++` atomic") and eval samples.
Ollama is called with a JSON-schema `format`, so it returns `{ point, hit, note }` per rubric point plus any red flags it found.

### 4.6 Server

It gets an `appId` on packs, event streams and device pairings, and an endpoint that proxies grading requests to a configured Ollama URL. Backups are unchanged.

## 5. Curriculum

| # | Track | Core content | Signature visual / step |
|---|---|---|---|
| 1 | **Types and the compiler** (short) | Skip the struct/class/record basics (test-out). Covers lowering, `in`/`ref`/`out`, boxing, nullability flow analysis, generics (constraints, variance, static abstract members), pattern-matching internals | Lowered view; stack/heap reference diagram |
| 2 | **Async in depth** | State-machine lowering, `Task` vs `ValueTask`, `SynchronizationContext`/`ExecutionContext`, `ConfigureAwait`, deadlocks, cancellation, `IAsyncEnumerable`, `Channel<T>`, async disposal | Async timeline (continuations, threads) |
| 3 | **Memory and the GC** | Generations, LOH/POH, hidden allocations (closures, LINQ, params, boxing, strings), `Span<T>`/`Memory<T>`, `stackalloc`, `ArrayPool`, `ref struct`, `IDisposable`/finalizers/`SafeHandle` | Heap view; "how many bytes?" steps |
| 4 | **Concurrency and the memory model** | Thread pool, `lock`/`Lock`/`SemaphoreSlim`, `Interlocked`, `volatile` and reordering, concurrent collections, `Parallel`/PLINQ, finding races | Interleaving player |
| 5 | **Collections and LINQ internals** | `List<T>` growth, `Dictionary` buckets and hashing, frozen/immutable collections, deferred execution, iterator lowering, `IQueryable` and expression trees | Existing array/dict trace views |
| 6 | **Performance engineering** | BenchmarkDotNet, `dotnet-counters`, `dotnet-trace`, tiered JIT, PGO, inlining, AOT and trimming, `Vector<T>` basics | Benchmark-predict |
| 7 | **Metaprogramming** | Reflection costs, attributes, source generators, analyzers + code fixes, interceptors | Write a small generator (large screen) |
| 8 | **API and library design** | Framework Design Guidelines, exceptions vs results, overload/default-parameter traps, binary vs source breaking changes, versioning, Options/DI lifetimes | "Is this a breaking change?" |
| 9 | **ASP.NET Core and EF Core internals** | Hosting, middleware pipeline, DI scopes, Kestrel, minimal APIs, change tracking, query translation, N+1, compiled models | Request-pipeline trace |
| 10 | **Architecture** | Modular monolith, DDD tactical patterns, boundaries, testing strategy, slices vs layers, when *not* to abstract | Design-tradeoff |
| 11 | **Distributed systems in .NET** | Timeouts, retries, circuit breakers, idempotency, outbox, messaging, caching, OpenTelemetry | Failure scenarios |
| 12 | **Technical leadership** | Reviewing code at scale, ADRs and design docs, tradeoff analysis, incident postmortems, technical strategy | Review-code; ADR critique |

## 6. New step types

| Step type | What you do | How it's graded |
|---|---|---|
| `review-code` | Flag lines in a diff or file with a category (bug, perf, concurrency, API, security), and optionally say why | Against authored defects, with partial credit; the "why" is graded like free text (§4.5) |
| `lowered` | Read C# beside its lowered form or IL and answer questions about it | Choice/predict on the pair |
| `allocations` | Predict allocations, or rewrite code to allocate nothing | Choice, or write-code with a byte-budget test |
| `benchmark-predict` | Rank implementations, then see the measured results | Against recorded BenchmarkDotNet data |
| `interleave` | Pick the thread schedule that breaks an invariant | Authored schedules, replayed in the trace player |
| `design-tradeoff` | Pick an option and justify it | Option graded automatically; justification self-assessed against the rubric, with local-LLM feedback |
| `write-code` (extended) | Multi-file exercises with C# assertion tests | Roslyn on the Mac; phone alternative on Android |

The existing step types (explain, choice, predict-output, fill-blank, Parsons, trace) work for C# once the language seam is in place.

## 7. Phases

| Phase | Outcome |
|---|---|
| **0. Spike** (throwaway branch) | Roslyn compile + run + `Tick()` timeout in the Mac Catalyst Release app (interpreter), with latency numbers; compare known-snippet allocation measurements against CLI CoreCLR, verify collectible-ALC unloading and memory over ~100 compile/run cycles, and validate the call-depth limit on an explicitly sized thread stack; the same on a release Android APK, with app-size delta; CLI proof of concept for one recorded trace, one lowered view and one benchmark |
| **1. Engine extraction** | `ICodeRuntime` + `Brilliant.Runtime.Python`; generalised `Comparison`/`CodeTest`/`Visual`; flavors + app manifests + `scripts/build-app.sh`; `content/` → `content/dsa/`; server `appId`; per-manifest step-type allow-list; per-lesson test-out. The DSA app must behave exactly as before. |
| **2. C# app skeleton** | `apps/csharp` flavor on the Mac, then Android; `content/csharp` pack pinned to .NET 10 / C# 14; pairing with `appId` |
| **3. C# pack-time tooling + Mac runtime** | CLI: reference solutions, trace recorder, lowering, benchmarks. App: Roslyn write-code on the Mac |
| **4. Track 1** | Types and the compiler, with `lowered` steps |
| **5. Tracks 2–3** | Async timeline, heap view, `allocations`, `benchmark-predict` |
| **6. Track 4** | `interleave` + interleaving player |
| **7. Judgement** | `review-code`, `design-tradeoff`, `IAnswerGrader` (Ollama on the Mac, server proxy, Claude button), grader evals; Tracks 8, 10–12 |
| Ongoing | Tracks 5–7, 9; content stays about one track ahead of where you are studying |
