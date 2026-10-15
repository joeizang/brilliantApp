# Issue breakdown — C# app (senior → principal)

Vertical slices derived from the [C# PRD](prd-csharp.md) / [#137](https://github.com/joeizang/brilliantApp/issues/137), with decisions in [csharp-app-plan.md](csharp-app-plan.md). Created 2026-10-15.

- **AFK** (`ready-for-agent`): an agent can build it; Joseph reviews the PR.
- **HITL** (`ready-for-human`): needs Joseph (device runs, choosing the grading model).
- Content is drafted by Claude, gated by the validator, reviewed by Joseph in the PR (one PR per 2–3 lessons), and stays about one track ahead of where Joseph is studying.

## C0 — C# spikes

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#138](https://github.com/joeizang/brilliantApp/issues/138) | Spike: Roslyn compile + run + budget probe in the Release Mac app and a release Android APK | HITL | — |
| [#139](https://github.com/joeizang/brilliantApp/issues/139) | Spike: pack-time C# recording in the CLI (one trace, one lowered view, one benchmark) | AFK | — |

## C1 — Multi-app engine

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#140](https://github.com/joeizang/brilliantApp/issues/140) | App Manifest: the DSA app defined by a manifest | AFK | — |
| [#141](https://github.com/joeizang/brilliantApp/issues/141) | Flavored build: one host, one installable app per manifest | AFK | [#140](https://github.com/joeizang/brilliantApp/issues/140) |
| [#142](https://github.com/joeizang/brilliantApp/issues/142) | Content per app and a per-app step-type allow-list in the validator | AFK | [#140](https://github.com/joeizang/brilliantApp/issues/140) |
| [#143](https://github.com/joeizang/brilliantApp/issues/143) | Code runtime registry: Python behind ICodeRuntime in its own runtime project | AFK | [#141](https://github.com/joeizang/brilliantApp/issues/141) |
| [#144](https://github.com/joeizang/brilliantApp/issues/144) | Language-neutral content model and DSA content migration | AFK | [#142](https://github.com/joeizang/brilliantApp/issues/142), [#143](https://github.com/joeizang/brilliantApp/issues/143) |
| [#145](https://github.com/joeizang/brilliantApp/issues/145) | Server: scope packs, events and pairing by app ID | AFK | [#140](https://github.com/joeizang/brilliantApp/issues/140) |
| [#146](https://github.com/joeizang/brilliantApp/issues/146) | Per-lesson test-out | AFK | — |
| [#147](https://github.com/joeizang/brilliantApp/issues/147) | Device check: two flavors side by side on Mac and Galaxy | HITL | [#141](https://github.com/joeizang/brilliantApp/issues/141), [#145](https://github.com/joeizang/brilliantApp/issues/145) |

## C2 — C# app skeleton

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#148](https://github.com/joeizang/brilliantApp/issues/148) | C# app flavor with a pinned .NET 10 / C# 14 content pack | AFK | [#141](https://github.com/joeizang/brilliantApp/issues/141), [#142](https://github.com/joeizang/brilliantApp/issues/142), [#145](https://github.com/joeizang/brilliantApp/issues/145) |

## C3 — C# tooling & mechanics

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#149](https://github.com/joeizang/brilliantApp/issues/149) | Probe rewriter and the C# reference-solution gate in the CLI | AFK | [#144](https://github.com/joeizang/brilliantApp/issues/144), [#148](https://github.com/joeizang/brilliantApp/issues/148) |
| [#150](https://github.com/joeizang/brilliantApp/issues/150) | C# write-code on the Mac | AFK | [#138](https://github.com/joeizang/brilliantApp/issues/138), [#143](https://github.com/joeizang/brilliantApp/issues/143), [#149](https://github.com/joeizang/brilliantApp/issues/149) |
| [#151](https://github.com/joeizang/brilliantApp/issues/151) | Allocation-budget tests for C# write-code | AFK | [#150](https://github.com/joeizang/brilliantApp/issues/150) |
| [#152](https://github.com/joeizang/brilliantApp/issues/152) | Multi-file C# write-code exercises | AFK | [#150](https://github.com/joeizang/brilliantApp/issues/150) |
| [#153](https://github.com/joeizang/brilliantApp/issues/153) | Recorded C# traces played on every device | AFK | [#139](https://github.com/joeizang/brilliantApp/issues/139), [#149](https://github.com/joeizang/brilliantApp/issues/149) |
| [#154](https://github.com/joeizang/brilliantApp/issues/154) | Lowering service and the `lowered` step | AFK | [#139](https://github.com/joeizang/brilliantApp/issues/139), [#148](https://github.com/joeizang/brilliantApp/issues/148) |
| [#155](https://github.com/joeizang/brilliantApp/issues/155) | Benchmark recorder and the `benchmark-predict` step | AFK | [#139](https://github.com/joeizang/brilliantApp/issues/139), [#148](https://github.com/joeizang/brilliantApp/issues/148) |
| [#156](https://github.com/joeizang/brilliantApp/issues/156) | `allocations` step | AFK | [#151](https://github.com/joeizang/brilliantApp/issues/151) |
| [#157](https://github.com/joeizang/brilliantApp/issues/157) | Trace player: stack/heap reference view | AFK | [#153](https://github.com/joeizang/brilliantApp/issues/153) |

## C4 — C# core tracks 1–4

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#158](https://github.com/joeizang/brilliantApp/issues/158) | Content: C# Track 1 — Types and the compiler | AFK | [#150](https://github.com/joeizang/brilliantApp/issues/150), [#153](https://github.com/joeizang/brilliantApp/issues/153), [#154](https://github.com/joeizang/brilliantApp/issues/154), [#157](https://github.com/joeizang/brilliantApp/issues/157) |
| [#159](https://github.com/joeizang/brilliantApp/issues/159) | Trace player: async timeline view | AFK | [#153](https://github.com/joeizang/brilliantApp/issues/153) |
| [#160](https://github.com/joeizang/brilliantApp/issues/160) | Content: C# Track 2 — Async in depth | AFK | [#158](https://github.com/joeizang/brilliantApp/issues/158), [#159](https://github.com/joeizang/brilliantApp/issues/159) |
| [#161](https://github.com/joeizang/brilliantApp/issues/161) | Content: C# Track 3 — Memory and the GC | AFK | [#155](https://github.com/joeizang/brilliantApp/issues/155), [#156](https://github.com/joeizang/brilliantApp/issues/156), [#157](https://github.com/joeizang/brilliantApp/issues/157) |
| [#162](https://github.com/joeizang/brilliantApp/issues/162) | `interleave` step and interleaving player | AFK | [#153](https://github.com/joeizang/brilliantApp/issues/153) |
| [#163](https://github.com/joeizang/brilliantApp/issues/163) | Content: C# Track 4 — Concurrency and the memory model | AFK | [#162](https://github.com/joeizang/brilliantApp/issues/162) |

## C5 — Judgement & local feedback

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#164](https://github.com/joeizang/brilliantApp/issues/164) | `review-code` step | AFK | [#148](https://github.com/joeizang/brilliantApp/issues/148) |
| [#165](https://github.com/joeizang/brilliantApp/issues/165) | `design-tradeoff` step with rubric self-assessment | AFK | [#148](https://github.com/joeizang/brilliantApp/issues/148) |
| [#166](https://github.com/joeizang/brilliantApp/issues/166) | Local Ollama feedback on the Mac | AFK | [#165](https://github.com/joeizang/brilliantApp/issues/165) |
| [#167](https://github.com/joeizang/brilliantApp/issues/167) | Phone feedback through the server proxy (opt-in) | AFK | [#166](https://github.com/joeizang/brilliantApp/issues/166), [#145](https://github.com/joeizang/brilliantApp/issues/145) |
| [#168](https://github.com/joeizang/brilliantApp/issues/168) | "Ask Claude" button (explicit, last resort) | AFK | [#166](https://github.com/joeizang/brilliantApp/issues/166) |
| [#169](https://github.com/joeizang/brilliantApp/issues/169) | Grader evals and choosing the local model | HITL | [#166](https://github.com/joeizang/brilliantApp/issues/166) |
| [#170](https://github.com/joeizang/brilliantApp/issues/170) | Content: C# Track 8 — API and library design | AFK | [#164](https://github.com/joeizang/brilliantApp/issues/164), [#165](https://github.com/joeizang/brilliantApp/issues/165) |
| [#171](https://github.com/joeizang/brilliantApp/issues/171) | Content: C# Track 10 — Architecture | AFK | [#165](https://github.com/joeizang/brilliantApp/issues/165) |
| [#172](https://github.com/joeizang/brilliantApp/issues/172) | Content: C# Track 11 — Distributed systems in .NET | AFK | [#165](https://github.com/joeizang/brilliantApp/issues/165) |
| [#173](https://github.com/joeizang/brilliantApp/issues/173) | Content: C# Track 12 — Technical leadership | AFK | [#164](https://github.com/joeizang/brilliantApp/issues/164), [#165](https://github.com/joeizang/brilliantApp/issues/165) |

## C6 — C# remaining tracks

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#174](https://github.com/joeizang/brilliantApp/issues/174) | Content: C# Track 5 — Collections and LINQ internals | AFK | [#153](https://github.com/joeizang/brilliantApp/issues/153) |
| [#175](https://github.com/joeizang/brilliantApp/issues/175) | Content: C# Track 6 — Performance engineering | AFK | [#155](https://github.com/joeizang/brilliantApp/issues/155), [#156](https://github.com/joeizang/brilliantApp/issues/156) |
| [#176](https://github.com/joeizang/brilliantApp/issues/176) | Content: C# Track 7 — Metaprogramming | AFK | [#152](https://github.com/joeizang/brilliantApp/issues/152) |
| [#177](https://github.com/joeizang/brilliantApp/issues/177) | Content: C# Track 9 — ASP.NET Core and EF Core internals | AFK | [#153](https://github.com/joeizang/brilliantApp/issues/153) |

## Critical path

[#140](https://github.com/joeizang/brilliantApp/issues/140) manifest → [#141](https://github.com/joeizang/brilliantApp/issues/141) flavors → [#143](https://github.com/joeizang/brilliantApp/issues/143) runtime registry → [#144](https://github.com/joeizang/brilliantApp/issues/144) neutral model → [#148](https://github.com/joeizang/brilliantApp/issues/148) C# app → [#149](https://github.com/joeizang/brilliantApp/issues/149) rewriter + gate → [#150](https://github.com/joeizang/brilliantApp/issues/150) Mac write-code / [#153](https://github.com/joeizang/brilliantApp/issues/153) recorded traces → [#158](https://github.com/joeizang/brilliantApp/issues/158) Track 1. The spikes [#138](https://github.com/joeizang/brilliantApp/issues/138) and [#139](https://github.com/joeizang/brilliantApp/issues/139), test-out [#146](https://github.com/joeizang/brilliantApp/issues/146), and server scoping [#145](https://github.com/joeizang/brilliantApp/issues/145) can start at once, in parallel with [#140](https://github.com/joeizang/brilliantApp/issues/140).
