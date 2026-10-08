# Issue breakdown — v1

Vertical slices (tracer bullets) derived from the [PRD](prd.md) / [#1](https://github.com/joeizang/brilliantApp/issues/1). Created 2026-10-09.

- **AFK** (`ready-for-agent`): an agent can build and merge it; Joseph reviews the PR.
- **HITL** (`ready-for-human`): needs Joseph — on-device testing, certificate install, or a design decision.
- Content slices are drafted by Claude, gated by the validator, and reviewed by Joseph in the PR.


## M0 — Mac skeleton

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#2](https://github.com/joeizang/brilliantApp/issues/2) | Spike: Pyodide offline inside BlazorWebView on Mac Catalyst and Android | HITL | — |
| [#3](https://github.com/joeizang/brilliantApp/issues/3) | Tracer bullet: Mac app renders an Explain step from a bundled Content Pack | AFK | — |
| [#4](https://github.com/joeizang/brilliantApp/issues/4) | Choice step with feedback, recorded in the SQLite Progress Event Log | AFK | [#3](https://github.com/joeizang/brilliantApp/issues/3) |
| [#5](https://github.com/joeizang/brilliantApp/issues/5) | Predict-output step and Answer Evaluator with mistake feedback | AFK | [#4](https://github.com/joeizang/brilliantApp/issues/4) |
| [#6](https://github.com/joeizang/brilliantApp/issues/6) | Track and lesson navigation with ordered unlocking | AFK | [#4](https://github.com/joeizang/brilliantApp/issues/4) |
| [#7](https://github.com/joeizang/brilliantApp/issues/7) | Light/dark theme following the system setting | AFK | [#3](https://github.com/joeizang/brilliantApp/issues/3) |
| [#8](https://github.com/joeizang/brilliantApp/issues/8) | Content: Track 1 lessons 1–2 (Python through C# eyes; control flow & functions) | AFK | [#5](https://github.com/joeizang/brilliantApp/issues/5) |

## M1 — Code steps

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#9](https://github.com/joeizang/brilliantApp/issues/9) | WriteCode step: CodeMirror editor + Pyodide worker running tests | AFK | [#2](https://github.com/joeizang/brilliantApp/issues/2), [#4](https://github.com/joeizang/brilliantApp/issues/4) |
| [#10](https://github.com/joeizang/brilliantApp/issues/10) | Code execution timeouts and worker recovery | AFK | [#9](https://github.com/joeizang/brilliantApp/issues/9) |
| [#11](https://github.com/joeizang/brilliantApp/issues/11) | Validator runs reference solutions against tests in CPython | AFK | [#9](https://github.com/joeizang/brilliantApp/issues/9) |
| [#12](https://github.com/joeizang/brilliantApp/issues/12) | Code drafts autosave and reset to starter code | AFK | [#9](https://github.com/joeizang/brilliantApp/issues/9) |
| [#13](https://github.com/joeizang/brilliantApp/issues/13) | Fill-in-the-blank code step | AFK | [#9](https://github.com/joeizang/brilliantApp/issues/9) |
| [#14](https://github.com/joeizang/brilliantApp/issues/14) | Parsons step with desktop drag-and-drop | AFK | [#5](https://github.com/joeizang/brilliantApp/issues/5) |
| [#15](https://github.com/joeizang/brilliantApp/issues/15) | Mac polish: VS Code keymap/theme, shortcuts and menu bar | AFK | [#9](https://github.com/joeizang/brilliantApp/issues/9) |
| [#16](https://github.com/joeizang/brilliantApp/issues/16) | Content: Track 1 lessons 3–4 (lists; strings) | AFK | [#11](https://github.com/joeizang/brilliantApp/issues/11), [#13](https://github.com/joeizang/brilliantApp/issues/13), [#14](https://github.com/joeizang/brilliantApp/issues/14) |

## M2 — Review + trace player

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#17](https://github.com/joeizang/brilliantApp/issues/17) | Learner State Projector: progress derived from the event log | AFK | [#6](https://github.com/joeizang/brilliantApp/issues/6) |
| [#18](https://github.com/joeizang/brilliantApp/issues/18) | Review tracer: review items, FSRS scheduling and Review screen | AFK | [#17](https://github.com/joeizang/brilliantApp/issues/17) |
| [#19](https://github.com/joeizang/brilliantApp/issues/19) | Review Queue Builder: daily cap, pattern weighting, item types | AFK | [#18](https://github.com/joeizang/brilliantApp/issues/18) |
| [#20](https://github.com/joeizang/brilliantApp/issues/20) | Hint ladder with rating impact | AFK | [#18](https://github.com/joeizang/brilliantApp/issues/18) |
| [#21](https://github.com/joeizang/brilliantApp/issues/21) | Mastery bars and concept detail view | AFK | [#18](https://github.com/joeizang/brilliantApp/issues/18) |
| [#22](https://github.com/joeizang/brilliantApp/issues/22) | Daily session runner, daily goal, streak and Today screen | AFK | [#19](https://github.com/joeizang/brilliantApp/issues/19) |
| [#23](https://github.com/joeizang/brilliantApp/issues/23) | Trace player tracer: arrays with pointers, step forward/back | AFK | [#9](https://github.com/joeizang/brilliantApp/issues/9) |
| [#24](https://github.com/joeizang/brilliantApp/issues/24) | Trace player: dict/set tables, playback controls, validator dry-run | AFK | [#23](https://github.com/joeizang/brilliantApp/issues/23) |
| [#25](https://github.com/joeizang/brilliantApp/issues/25) | Full-solution walkthrough in the trace player | AFK | [#20](https://github.com/joeizang/brilliantApp/issues/20), [#24](https://github.com/joeizang/brilliantApp/issues/24) |
| [#26](https://github.com/joeizang/brilliantApp/issues/26) | Flag confusing steps and export flagged list | AFK | [#4](https://github.com/joeizang/brilliantApp/issues/4) |
| [#27](https://github.com/joeizang/brilliantApp/issues/27) | Content: Track 1 lessons 5–11 (dicts & sets → capstone) | AFK | [#19](https://github.com/joeizang/brilliantApp/issues/19), [#24](https://github.com/joeizang/brilliantApp/issues/24) |

## M3 — Server, sync & Android

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#28](https://github.com/joeizang/brilliantApp/issues/28) | Server tracer: minimal API + Postgres in Docker Compose, device pairing | AFK | [#3](https://github.com/joeizang/brilliantApp/issues/3) |
| [#29](https://github.com/joeizang/brilliantApp/issues/29) | Event-log sync between devices and server | AFK | [#17](https://github.com/joeizang/brilliantApp/issues/17), [#28](https://github.com/joeizang/brilliantApp/issues/28) |
| [#30](https://github.com/joeizang/brilliantApp/issues/30) | Code drafts sync (last-write-wins) | AFK | [#12](https://github.com/joeizang/brilliantApp/issues/12), [#29](https://github.com/joeizang/brilliantApp/issues/29) |
| [#31](https://github.com/joeizang/brilliantApp/issues/31) | Content distribution: publish packs and download updates | AFK | [#17](https://github.com/joeizang/brilliantApp/issues/17), [#28](https://github.com/joeizang/brilliantApp/issues/28) |
| [#32](https://github.com/joeizang/brilliantApp/issues/32) | Nightly Postgres backup in Docker Compose | AFK | [#28](https://github.com/joeizang/brilliantApp/issues/28) |
| [#33](https://github.com/joeizang/brilliantApp/issues/33) | Android target: app runs on phone with pairing and sync; signed APK | HITL | [#2](https://github.com/joeizang/brilliantApp/issues/2), [#29](https://github.com/joeizang/brilliantApp/issues/29) |
| [#34](https://github.com/joeizang/brilliantApp/issues/34) | Phone and tablet layouts, touch Parsons, phone-friendly review | AFK | [#33](https://github.com/joeizang/brilliantApp/issues/33), [#19](https://github.com/joeizang/brilliantApp/issues/19) |

## M4 — Practice & hardening

| Issue | Slice | Type | Blocked by |
|---|---|---|---|
| [#35](https://github.com/joeizang/brilliantApp/issues/35) | Local-CA HTTPS for server and devices | HITL | [#33](https://github.com/joeizang/brilliantApp/issues/33) |
| [#36](https://github.com/joeizang/brilliantApp/issues/36) | Practice mode with timed interview simulation (design first) | HITL | [#22](https://github.com/joeizang/brilliantApp/issues/22) |
| [#37](https://github.com/joeizang/brilliantApp/issues/37) | Content: Track 2 — two pointers & sliding window | AFK | [#27](https://github.com/joeizang/brilliantApp/issues/27) |

## Critical path

#3 → #4 → #9 → (#23 trace player, #17 projector → #18 review) → … ; #2 (Pyodide spike) runs in parallel with #3 in week 1.
