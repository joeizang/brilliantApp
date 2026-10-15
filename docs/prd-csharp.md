# PRD — C# App: Senior → Principal (on the Brilliant engine)

> Status: Draft · Owner: Joseph Izang · Date: 2026-10-15
> Source: planning + grill-me sessions 2026-10-12 to 2026-10-14. Tracked as GitHub issue joeizang/brilliantApp#137. Decisions recorded in `docs/csharp-app-plan.md`. All plans, implementation summaries and related docs live in `docs/`.

## Problem Statement

I'm an experienced C# developer. I'm solid on the type system (structs, classes, records), but I couldn't confidently explain how async/await works under the hood, what the GC is doing with my allocations, or why a piece of threaded code is or isn't correct. Those are the things that separate a senior engineer from a principal one, along with judgement: designing APIs that don't break, choosing between architectures, and reviewing other people's code well.

The resources I've used don't build that depth. Blog posts and conference talks are passive and I forget them. Books are thorough but nothing makes me apply them or brings them back before I forget. And no tool lets me *see* the mechanics: what my code compiles into, what allocates, how a race actually happens.

I've already built a learning app for DSA and Python that works the way I want to learn: short interactive lessons, spaced repetition, real code execution and an animated trace player, offline-first and synced through my own server. I want the same experience for C#, as a **separate app** with its own icon, queue and streak, built from the same engine so that improvements reach both. I also want the repo to make adding more subject apps cheap later on.

## Solution

The repo becomes a home for several learning apps built from one engine. A single host app is built in **flavors**: each flavor is defined by an **App Manifest** (identity, icon, accent colour, content pack, enabled code runtimes, enabled step types) and installs as its own app on my Mac and Android phone. The existing DSA app becomes the first flavor and behaves exactly as before. The C# app is the second.

From my perspective, the C# app:

- Opens to the same daily flow as the DSA app: Today, Review and the next Lesson in my current Track, with mastery, a streak and a daily goal. It has its own progress, separate from the DSA app.
- Teaches **12 Tracks**: Types and the compiler (short), Async in depth, Memory and the GC, Concurrency and the memory model, Collections and LINQ internals, Performance engineering, Metaprogramming, API and library design, ASP.NET Core and EF Core internals, Architecture, Distributed systems in .NET, and Technical leadership.
- Lets me **test out** of any lesson I already know. 3–4 hard questions from the lesson's own review items complete it if I get them all right, and those items enter Review as already known.
- Shows me the **mechanics**:
  - **lowered views**: what `async`, `foreach`, `yield`, closures, `using`, pattern matching and records compile into, as C# and IL;
  - **recorded traces** of C# code in the trace player;
  - **allocation** questions ("how many bytes does this allocate?");
  - **benchmark** questions where I rank implementations and then see measured results;
  - an **interleaving player** where I find the thread schedule that breaks an invariant.
- Trains **judgement**:
  - **review-code** steps where I flag the problems in a diff;
  - **design-tradeoff** steps where I pick an option and justify it.

  I grade my written justification against an authored rubric. A **local model on my Mac (Ollama)** gives advisory feedback. Claude is only asked when I tap a button.
- Runs **real C# write-code exercises on my Mac**. On my phone those steps use their tap-friendly alternative. Everything else, including traces, works on the phone.
- Pins its content to **.NET 10 / C# 14**. Upgrades are deliberate and go from one LTS to the next.

## User Stories

### Multi-app engine

1. As the developer, I want one host project built in flavors selected by a build property, so that adding a new subject app needs no new host code.
2. As the developer, I want each app defined by an App Manifest (ID, display name, icon, accent colour, pack, runtimes, step types, grader model), so that an app's identity and capabilities live in one reviewable file.
3. As a learner, I want the DSA app and the C# app installed side by side on my Mac and phone with their own icons and names, so that I can open the subject I want directly.
4. As a learner, I want each app to keep its own progress, streak, daily goal, review queue and drafts, so that studying one subject never disturbs the other.
5. As the developer, I want each flavor to ship only the code runtimes its manifest enables, so that the C# app doesn't carry Pyodide and the DSA app doesn't carry Roslyn.
6. As the developer, I want one build script that takes an app and a platform, so that building any app for Mac or Android is a single command.
7. As a learner, I want the DSA app to behave exactly as it does today after the engine is extracted, so that the refactor costs me nothing.
8. As the developer, I want each app's content in its own folder in this repo with its own pack definition, so that content PRs stay in the same workflow and CI as the engine changes they rely on.
9. As the developer, I want the validator to reject a pack that uses a step type its app manifest doesn't enable, so that an app never receives content it can't show.
10. As the developer, I want code runtimes behind one interface keyed by language, so that write-code and trace steps work the same way whatever language the exercise declares.
11. As the developer, I want code tests written as source text in the exercise's language, so that C# exercises use C# expressions and C# equality.
12. As a content author, I want side-by-side comparisons to be a list of labelled snippets, so that a lesson can show "before/after", "naive/optimised" or "C# 7 vs C# 14", not only C# vs Python.
13. As a learner, I want on-screen text to name the exercise's language rather than always saying "Python", so that the C# app reads naturally.

### Server and sync

14. As the developer, I want the server to scope packs, progress event streams and device pairings by app ID, so that one server and one database serve every app without mixing their data.
15. As a learner, I want to pair each app on each device separately, so that a device can run both apps against the same server.
16. As a learner, I want each app to download only its own content packs on home Wi-Fi, so that updates stay small and relevant.
17. As the developer, I want existing DSA data migrated to the DSA app ID, so that no progress is lost when scoping is introduced.
18. As the developer, I want the nightly backup to keep covering all apps without changes, so that adding apps adds no ops work.

### Test-out

19. As a learner, I want an optional "I know this — test me" at the start of every lesson, so that I don't sit through material I already know.
20. As a learner, I want test-out to ask 3–4 hard questions drawn from the lesson's own review items, so that passing really means I know it.
21. As a learner, I want a perfect test-out to mark the lesson complete, so that my track progresses.
22. As a learner, I want the review items of a tested-out lesson to enter Review as already known, with long intervals, so that I'm checked occasionally rather than drilled.
23. As a learner, I want a failed test-out to cost nothing and drop me into the lesson, so that trying is never risky.
24. As a learner, I want test-out in the DSA app too, so that both apps benefit.
25. As the developer, I want test-out recorded as an event in the progress log, so that it syncs and replays like everything else.
26. As a learner, I want a lesson's mastery to reflect that I tested out, so that the mastery bars stay honest.

### C# content and tooling

27. As the developer, I want the C# pack to declare its target framework and language version, so that every recorded artefact is produced against one pinned .NET.
28. As the developer, I want the validator to compile and run every C# reference solution against its tests, so that no broken C# exercise is ever published.
29. As a content author, I want trace steps' C# code run and recorded when the pack is built, so that traces play on every device, including the phone, with no on-device runtime.
30. As a learner, I want C# traces to use the existing trace views (arrays, dictionaries, stacks, queues, linked lists, trees, heaps, grids, graphs and the variables table), so that I get the same visual quality as in the DSA app.
31. As a content author, I want lowered C# and IL generated from a snippet when the pack is built, so that I never hand-write compiler output.
32. As a content author, I want benchmarks run with BenchmarkDotNet when the pack is built and their results stored in the pack, so that benchmark questions show real measured numbers.
33. As a learner, I want benchmark results labelled with the machine they were measured on, so that I know they're from Apple Silicon.
34. As the developer, I want upgrading to the next LTS to be one PR that re-records everything and shows what changed, so that I can see what a .NET release changed.
35. As the developer, I want recorded artefacts in the pack to be reproducible from source, so that a reviewer can regenerate them and get the same result (benchmarks excepted).

### C# write-code (Mac)

36. As a learner on my Mac, I want to write C# in the code editor and run it against hidden tests, so that I practise for real.
37. As a learner, I want the same feedback I get in the DSA app (passed count, first failing test, expected vs actual, printed output, compile errors with line numbers), so that the experience is familiar.
38. As a learner, I want compile errors shown as errors in my code, not as failed tests, so that I can tell syntax problems from logic problems.
39. As a learner, I want infinite loops stopped by time/step budgets and runaway recursion stopped by a call-depth limit with a clear message, so that the app remains responsive without exhausting its stack.
40. As a learner, I want test results compared structurally (arrays, lists, records, tuples by value), so that a correct answer isn't marked wrong for reference inequality.
41. As a learner, I want allocation-budget tests ("this method must allocate zero bytes"), so that performance exercises are graded objectively.
42. As a learner, I want multi-file C# exercises where they help, so that design exercises can have more than one type.
43. As a learner on my phone, I want write-code steps to offer their phone alternative (Parsons or fill-blank), so that I can still progress on the go.
44. As a learner, I want my C# code drafts saved and synced, as in the DSA app, so that I can continue on another device.

### New step types

45. As a learner, I want **lowered** steps that show C# beside its lowered form or IL and ask me questions about it, so that I learn what the compiler does.
46. As a learner, I want **allocations** steps that ask how much a snippet allocates, or ask me to rewrite it to allocate nothing, so that I learn to spot hidden allocations.
47. As a learner, I want **benchmark-predict** steps where I rank implementations before seeing measured results, so that I build calibrated performance intuition.
48. As a learner, I want **interleave** steps where I step two threads by hand and find the schedule that breaks an invariant, so that I understand races instead of memorising rules.
49. As a learner, I want **review-code** steps where I flag lines in a diff with a category (bug, performance, concurrency, API, security), so that I practise reviewing the way a principal engineer does.
50. As a learner, I want partial credit and per-defect feedback in review-code, so that finding three of four problems counts.
51. As a learner, I want **design-tradeoff** steps where I pick an option for a scenario and justify it in writing, so that I practise explaining decisions.
52. As a learner, I want the option in a design-tradeoff graded automatically and the justification graded by me against an authored rubric, so that grading is fair and works offline.
53. As a learner, I want the new step types to support hints, review items, phone alternatives and practice like the existing types, so that they are first-class.
54. As a learner, I want new step types to work well on my phone wherever possible, so that I can do judgement practice anywhere.

### Free-text feedback (local model)

55. As a learner, I want my self-assessment against the rubric to set the review rating, so that Review stays deterministic and works offline.
56. As a learner on my Mac, I want advisory feedback from a local model in Ollama after I self-assess, so that I learn what I almost said without sending my answer off my machine.
57. As a learner, I want the feedback to say, for each rubric point, whether my answer made it, with a short note, so that it's specific.
58. As a learner, I want the feedback to flag common wrong-but-plausible claims in my answer (the rubric's red flags), so that confident mistakes are caught.
59. As a learner on my phone, I want feedback through my home server from the Mac's Ollama when I've switched that on, so that I get feedback on the go at home.
60. As a learner, I want a clear "feedback unavailable right now" when no model can be reached, so that I still finish the step.
61. As a learner, I want to request feedback later, from my Mac, for an answer I gave on my phone, so that nothing is lost.
62. As a learner, I want an explicit "Ask Claude" button as a last resort, and Claude never called automatically, so that nothing leaves my network unless I choose it.
63. As the developer, I want the app manifest to name the grading model, so that switching models is a one-line change.
64. As the developer, I want every rubric to carry sample answers with expected marks, and a CLI command that scores candidate models against them, so that I can choose between Gemma 26B, Qwen 27B and Gemma 12B with evidence.

### Curriculum

65. As a learner, I want Track 1 (Types and the compiler) kept short and focused on what I don't already know (lowering, variance, nullability flow, pattern-matching internals), so that I reach async quickly.
66. As a learner, I want Track 2 (Async in depth) to cover the state machine, contexts, `ConfigureAwait`, deadlocks, `ValueTask`, cancellation, async streams and channels, with an async timeline visual, so that I can reason about any async code.
67. As a learner, I want Track 3 (Memory and the GC) to make hidden allocations visible and teach `Span<T>`, pooling and disposal, so that I write efficient code on purpose.
68. As a learner, I want Track 4 (Concurrency) to teach the memory model through interleavings I can play, so that I can prove code correct or find the race.
69. As a learner, I want Tracks 5–9 to cover collection and LINQ internals, performance engineering, metaprogramming, API design and ASP.NET Core/EF Core internals, so that my knowledge covers the stack I use daily.
70. As a learner, I want Tracks 10–12 to cover architecture, distributed systems and technical leadership (code review, ADRs, tradeoffs, postmortems), so that I practise the judgement a principal engineer is hired for.
71. As a learner, I want current C# taught as the norm, with older forms mentioned only where I'll meet them in existing code, so that I learn the modern way first.
72. As the developer, I want C# content drafted by Claude and reviewed by me in PRs, kept about one track ahead of where I'm studying, so that content never blocks me and never piles up unreviewed.

## Implementation Decisions

### Modules

**App Manifest (engine).** Loads an app's manifest and answers capability questions: is this step type, runtime or grading model enabled? The host (what to register), the CLI (what to allow in a pack) and the server (which app IDs exist) all use it. It's a deep module with a tiny interface: parse, then query.

**Code runtime registry (engine).** Replaces the Python-specific runtime interface with `ICodeRuntime`, keyed by language. It keeps the existing result shapes (test run result, trace result). The Pyodide runtime moves into its own runtime project, unchanged in behaviour. Write-code and trace views look up the runtime by the step's language.

**Language-neutral content model.** Code tests carry source text in the exercise's language. Comparisons become a list of `{ label, language, code }`. Visual type names come from the runtime. Existing DSA content gets migrated mechanically.

**Step types.** They stay a closed list in the content model. New types: `lowered`, `allocations`, `benchmark-predict`, `interleave`, `review-code` and `design-tradeoff`. `write-code` gains multi-file support and an allocation budget on tests. Each new type gets an evaluator in the engine and a view in the shared UI. Every app manifest lists its allowed types, and the validator enforces the list.

**Step evaluators (engine).**
- `review-code`: matches flagged lines and categories against authored defects, with partial credit and per-defect feedback.
- `benchmark-predict`: compares the learner's ranking with the recorded ranking.
- `interleave`: checks the learner's schedule against the authored invariant, with authored schedules replayed in the trace player.
- `allocations`: either a choice question, or write-code with a byte budget.

All four produce the same answer outcome type the existing evaluators use, so Review, hints and Practice treat them uniformly.

**Test-out (engine).** Selects 3–4 questions from the lesson's review items, evaluates them, and emits a `LessonTestedOut` progress event. The Review projector seeds a tested-out lesson's items with a high initial stability (already known). Mastery treats them as learned. A failed attempt emits nothing that affects scheduling.

**Answer grader (engine).** `IAnswerGrader` takes a rubric (points, model answer, red flags) and an answer, and returns per-point `{ point, hit, note }` plus the red flags it found. Providers are selected per device:
1. Mac: local Ollama, called directly, then none (self-assess only). The Mac does not fall through to the server proxy.
2. Phone: the server's grading proxy (opt-in), then none. The phone has no local Ollama provider.

Claude is a separate provider that runs only on an explicit request. Ollama is called with a JSON-schema `format` for structured output. Feedback is advisory; the learner's own self-assessment sets the review rating.

**Probe rewriter (C# runtime).** A pure Roslyn syntax rewriter that inserts budget probes at loop back-edges and method entry, plus optional line-plus-locals probes. Method-entry instrumentation increments a call-depth counter, checks a fixed maximum and decrements in `finally`. The runner uses a dedicated thread with an explicit stack size, and Phase 0 validates that the maximum fits that stack, stopping deep recursion before an uncatchable `StackOverflowException` terminates the app. The rewriter excludes `BudgetExceededException` from every learner catch clause using an exception filter (preserving existing filters); bare catches are rewritten to typed, filtered catches. Probes also run inside `finally` blocks. Tests must exercise these escape paths, rather than assuming an exception cannot be caught. The Mac runtime and CLI trace recorder both use this rewriter.

**C# runner (C# runtime).** Compiles against the pinned reference assemblies, loads the result into a collectible load context, runs the entrypoint per test under the probe budget, compares results structurally, measures allocated bytes per call where a test sets a budget, and unloads only where Phase 0 verifies runtime support. The CLI uses CoreCLR; the MAUI app uses Mono with SGen GC and, on Mac Release, the interpreter. CoreCLR pack-time measurements are authoritative for curriculum allocation budgets. Device measurements name their runtime explicitly; if the spike finds disagreement, device budget feedback is advisory or those tests run only in the CLI. It returns the existing test run result shape, with compile errors reported as `Error` with line numbers. It is shipped only in flavors that enable `csharp`, and on Mac only unless the spike justifies Android.

**CLI recorders.**
- C# reference-solution runner (the publishing gate).
- Trace recorder: rewriter + run, producing snapshots in the existing trace format.
- Lowering service: Roslyn + ICSharpCode.Decompiler, producing lowered C# and IL.
- Benchmark recorder: BenchmarkDotNet, recording results plus machine info.
- Grader eval runner: scores each rubric's sample answers against configured models and reports agreement.

All recorded artefacts are written into the pack at pack time.

**Flavored host.** One MAUI project. A build property selects the manifest, which sets the application ID, title, icon, accent and the referenced runtime projects. A script wraps building any app for any platform. Each app has its own local database file and its own secure-storage namespace.

**Server.**
- An app ID on packs, event streams and device pairings, with existing data migrated to the DSA app ID. Pairing and pack download are scoped by app.
- A grading proxy endpoint that forwards to a configured Ollama URL (the Mac's LAN address) and fails fast when it can't be reached.

### Decisions carried from planning

- **Single learner**: no learner identity on the server.
- **The kids' math app is a separate repo**, and nothing here is designed for it.
- **Traces, lowered views and benchmarks are recorded at pack time**; live tracing of the learner's own code is deferred.
- **Phone**: everything except running new code. Android on-device Roslyn is a stretch goal, decided by the spike.
- **Pinned to .NET 10 / C# 14**; upgrades go from LTS to LTS.
- **Ollama runs on the Mac** (M4 Max, 48 GB). Phone access requires the opt-in Ollama network change on the Mac.

### Phase 0 spike (before building the runtime)

- Measure Roslyn compile + run + budget-probe timeout inside the Release Mac Catalyst app, which runs with the interpreter enabled.
- Compare `GC.GetAllocatedBytesForCurrentThread()` on known zero-allocation, boxing, array and closure snippets between the pinned CLI (CoreCLR) and Release Mac app (Mono interpreter). Record warm-up, measurement boundaries, runtime and measured bytes. If results differ, keep CoreCLR pack-time budgets authoritative and make device checks advisory or CLI-only.
- Verify collectible `AssemblyLoadContext` unloading and memory over approximately 100 compile/run/unload cycles, including weak-reference collection checks and retained-memory growth. Do not assume Mono unloads assemblies as CoreCLR does; record a safe lifetime/restart recommendation if it cannot.
- Validate the method-entry call-depth limit on the dedicated thread's explicit stack size; deep recursion must stop with a budget result without terminating the app.
- Measure the same in a release Android APK, plus the app-size difference.
- A CLI proof of concept: one recorded trace, one lowered view, one benchmark.

The results decide whether the Mac runtime runs in-process as designed, and whether Android gets on-device C#.

## Testing Decisions

- **Good tests check behaviour through a module's public interface**: given a manifest, pack, code or answer, assert the result. They don't assert internal calls or structure. Fakes stand in only at real boundaries (the Ollama HTTP API, the clock, the device runtime).
- **Test-driven development** for:
  - App Manifest (parsing, capability queries, invalid manifests);
  - test-out (question selection, the event, the projector seeding stability, mastery);
  - the new step evaluators (review-code partial credit, ranking, interleave invariants, allocation budgets);
  - the answer grader (prompt built from the rubric, structured-response parsing, malformed or partial responses, provider-chain fallthrough, Claude never called implicitly), against a fake HTTP backend;
  - the probe rewriter (budget stops infinite loops; deep recursion stops cleanly at the call-depth limit on the explicitly sized thread; depth resets after exceptions; typed, bare and already-filtered catches cannot swallow budget failures; finally-block loops are bounded; line probes report the right lines and locals);
  - the C# runner (pass/fail/compile error/timeout, structural equality, allocation budgets, unloading).
- **Integration tests** for server app-ID scoping (two apps' packs, events and pairings stay apart; existing data migrates to the DSA app) and the grading proxy (forwards to Ollama, fails fast when unreachable).
- **Golden-file tests** for the CLI recorders: recorded traces and lowered output for fixed snippets, compared with checked-in expected files. Benchmarks are exempt; they're only checked for shape.
- **Validator tests** for the step-type allow-list, the C# reference-solution gate and the pinned-framework declaration.
- **Light bUnit tests** for the new step views, the test-out flow and the self-assess + feedback panel.
- **Regression guard**: the full existing DSA test suite must pass unchanged through the engine extraction, and DSA content must validate after migration.
- **Manual smoke test** per flavor and platform: build, install side by side, pair, sync, complete a lesson.
- **Prior art**: the existing Core tests (FSRS, mastery, event replay), CLI tests (trace-step validation, reference solutions), Server tests (API integration), Lessons.UI bUnit tests, and the Python tracer tests.

## Out of Scope

- The kids' math app and anything designed for it (multiple learners, profiles, parent views, math manipulatives).
- People-management topics (mentoring, influence, career levelling).
- Live tracing of the learner's own C# code (it's deferred; recorded traces only).
- On-device C# execution on Android, unless the spike shows it's cheap.
- Running benchmarks on the device.
- Automatic Claude calls of any kind. Claude is used only on an explicit tap.
- A learner-facing app switcher or merged dashboard across apps.
- C# versions other than the pinned LTS.
- Publishing the apps beyond my own devices (no Play Store or App Store).

## Further Notes

- Detailed rationale for each decision is in `docs/csharp-app-plan.md` §2.
- Phase order:
  - 0: spike
  - 1: engine extraction (manifest, runtime registry, language-neutral model, flavors, content move, server app ID, step-type allow-list, test-out)
  - 2: C# app skeleton
  - 3: C# pack-time tooling + Mac runtime
  - 4: Track 1
  - 5: Tracks 2–3
  - 6: Track 4
  - 7: judgement steps + grader + Tracks 8, 10–12

  Tracks 5–7 and 9 follow as content.
- Mac Catalyst Release now runs with the interpreter enabled (it was turned on to fix an EF Core crash at launch). That makes in-process Roslyn plausible on the Mac, but performance is unmeasured.
- Phone access to Ollama requires an opt-in change on the Mac (`OLLAMA_HOST=0.0.0.0` plus a firewall rule allowing port 11434 only from the Brilliant server's IP address). Ollama is unauthenticated, so other LAN clients must remain blocked. This is off until I opt in.
