# PRD — Brilliant-style DSA & Python Learning App (v1)

> Status: Draft · Owner: Joseph Izang · Date: 2026-10-08
> Source: planning session (grill-me) on 2026-10-08. Tracked as GitHub issue joeizang/brilliantApp#1. All plans, implementation summaries and related docs live in `docs/`.

## Problem Statement

I'm a working C# developer who is just starting to learn Python, and my data structures & algorithms (DSA) knowledge is very shaky. I regularly fail coding interviews and even practice tests — not because I can't program, but because I don't recognise which pattern a problem needs, I forget what I learned weeks ago, and passive resources (videos, articles, grinding random LeetCode problems) don't build lasting understanding.

Existing platforms don't fit:
- **Brilliant.org** has the right *style* (short, interactive, animated, "learn by doing") but no serious DSA-in-Python interview track, and it doesn't schedule long-term review of what I've learned.
- **LeetCode / NeetCode** give problems but little scaffolding for a beginner, and nothing that teaches *pattern recognition* deliberately or makes me revisit what I've forgotten.
- **Anki** gives spaced repetition but no interactive lessons, code execution or visualisations.

I want one tool, which I own and run myself, that combines Brilliant-style interactive lessons with spaced repetition and real Python execution, so that my time on task turns into durable mastery. I want to use it on my MacBook first and later on my Android phone/tablet, with progress kept in sync via my own home server. Later I want to reuse the same engine to teach my kids maths.

## Solution

A personal, offline-first learning app — built in .NET (MAUI Blazor Hybrid) — that runs on macOS first and Android later, backed by a small self-hosted ASP.NET Core server on my home network.

From my perspective:
- I open the app each day and see a **daily session**: first a short **Review** queue of things I'm about to forget (concepts, "which pattern fits this problem?", and problems I previously got wrong), then the next **Lesson** in my current **Track**.
- **Lessons** are short sequences of interactive **Steps**: explanations (often comparing Python to C#), multiple-choice, predict-the-output, fill-in-the-blank code, Parsons problems (drag lines into order), full write-the-code exercises that run real Python against tests, and an **animated trace player** that steps through code while showing arrays, dicts and pointers changing.
- When I'm stuck, a **hint ladder** (nudge → pattern hint → partial code → full solution walked through in the trace player) gets me unstuck, and items I needed help with come back for review sooner.
- I see **mastery per concept and per track**, a streak, and a daily goal.
- Everything works offline. When my devices are on home Wi-Fi they sync progress with my server and download any new or corrected lessons.
- Lessons are authored as files in git (drafted with Claude, reviewed by me), automatically validated — every reference solution must pass its tests — and published to the server as versioned **Content Packs**.

The first content is **Track 1: "Python for C# Devs → Arrays & Hashing"** (11 lessons), followed by tracks building towards dynamic programming.

## User Stories

### Daily learning loop
1. As a learner, I want the app to open on a "Today" screen showing my review queue size, next lesson, streak and daily-goal progress, so that I know exactly what to do with no decision fatigue.
2. As a learner, I want to start a daily session with one tap that runs Review first and then continues my current lesson, so that I build a consistent routine.
3. As a learner, I want to set a daily goal (e.g. minutes or number of steps), so that I can commit to a sustainable amount of time on task.
4. As a learner, I want to see a streak of consecutive days on which I met my daily goal, so that I'm motivated to stay consistent.
5. As a learner, I want to stop part-way through a session and resume exactly where I left off, so that short study windows aren't wasted.
6. As a learner, I want a summary at the end of a session (steps completed, accuracy, items reviewed, mastery changes), so that I can see that I'm making progress.

### Tracks, lessons and navigation
7. As a learner, I want to browse tracks and see the lessons in each, with completion and mastery indicators, so that I understand the path ahead.
8. As a learner, I want lessons to unlock in order within a track, while still being able to revisit any lesson I've completed, so that I build on prerequisites but can refresh anything.
9. As a learner, I want each lesson to take roughly 15–20 minutes, so that a lesson fits in one sitting.
10. As a learner, I want a progress indicator within a lesson, so that I know how many steps remain.
11. As a learner, I want lessons that were changed after I completed them to show "N new steps" rather than resetting my completion, so that content fixes don't erase my progress.
12. As a learner, I want to replay a completed lesson in a "review mode" without it affecting my lesson completion, so that I can re-read explanations.

### Step types
13. As a learner, I want Explain steps with text, code snippets and side-by-side C#↔Python comparisons, so that I can transfer what I already know from C#.
14. As a learner, I want multiple-choice and multi-select questions with immediate feedback, so that I check my understanding as I go.
15. As a learner, I want predict-the-output steps where I type or choose what code prints, so that I learn to execute code in my head.
16. As a learner, I want fill-in-the-blank code steps where I complete a missing line or expression, so that I can practise key ideas without writing a whole program.
17. As a learner, I want Parsons problems where I drag shuffled lines of code into the right order (and indentation), so that I can practise program structure without heavy typing.
18. As a learner, I want write-code steps with a real code editor where my Python runs against hidden tests, so that I practise solving problems for real.
19. As a learner, I want to see, for a failing write-code submission, the first failing test's input, expected output and my actual output, so that I can debug effectively.
20. As a learner, I want my code to stop with a clear message if it runs too long (e.g. an infinite loop), so that the app never freezes.
21. As a learner, I want print output and Python error tracebacks shown clearly, so that I learn to read Python errors.
22. As a learner, I want my in-progress code for an exercise to be saved automatically, so that I don't lose work if I leave.
23. As a learner, I want to reset an exercise to its starter code, so that I can start over cleanly.

### Trace player (animated visualisations)
24. As a learner, I want to step forwards and backwards through code execution, with the current line highlighted, so that I can see exactly how an algorithm works.
25. As a learner, I want arrays/lists to be drawn as cells with named pointers (`i`, `j`, `left`, `right`) moving between steps, so that index-based algorithms become visual.
26. As a learner, I want dicts and sets drawn as key→value tables that animate inserts, updates and lookups, so that I understand hashing patterns.
27. As a learner, I want to see local variable values at each step, so that I can follow the state of a program.
28. As a learner, I want play/pause and speed controls on the trace player, so that I can watch an algorithm run or go step by step.
29. As a learner on Mac, I want keyboard shortcuts (arrow keys to step, space to play/pause), so that I can use the trace player fluidly.

### Hints and feedback
30. As a learner, I want a four-level hint ladder (nudge → pattern hint → partial code → full solution), so that I can get exactly as much help as I need.
31. As a learner, I want the full solution to be shown animated in the trace player with an explanation of why it works, so that peeking still teaches me.
32. As a learner, I want feedback specific to common mistakes (e.g. "you returned the values, not the indices"), so that I understand *why* I was wrong.
33. As a learner, I want items where I used hints or got the answer wrong to come back for review sooner, so that "I had to peek" becomes "I own this".
34. As a learner, I want to flag a step as confusing or wrong, with an optional note, so that I can fix the content later.

### Review (spaced repetition) and mastery
35. As a learner, I want a daily Review queue of items scheduled by FSRS, so that I revisit material right before I'd forget it.
36. As a learner, I want review items to include concept questions, pattern-recognition questions ("which pattern fits this problem?") and re-solves of problems I got wrong, so that review targets my interview weaknesses.
37. As a learner, I want pattern-recognition items weighted heavily in Review, so that I train the skill I most lack in interviews.
38. As a learner, I want review items to unlock only after I complete the lesson that introduces them, so that I'm never reviewed on material I haven't learned.
39. As a learner, I want the app to infer review ratings from correctness, hint usage and response time, so that I don't have to grade myself.
40. As a learner, I want the daily review queue capped (default ~20), so that missing a day doesn't create an overwhelming backlog.
41. As a learner, I want to see mastery bars per concept and per track, so that I know what I truly know versus what I've merely seen.
42. As a learner, I want a concept detail view showing my history with that concept (lessons, reviews, accuracy), so that I can decide what to revisit.
43. As a learner, I want re-solve review items on phone to appear as Parsons or fill-in-the-blank where possible, so that review is practical without a keyboard.

### Platform experience
44. As a learner on Mac, I want a native-feeling window with a menu bar, sidebar navigation for wide windows and standard keyboard shortcuts (e.g. ⌘↩ to run code, 1–4 to pick answers), so that the app feels at home on macOS.
45. As a learner on Mac, I want a code editor with a VS Code-like keymap and dark theme, bracket matching, auto-indent and multiple cursors, so that writing code feels familiar.
46. As a learner on Android phone, I want a layout designed for small screens with touch-friendly drag-and-drop for Parsons problems, so that I can study on the go.
47. As a learner on Android phone, I want steps that require typing full code to be flagged "best on a larger screen" and offered as alternative step types where available, so that phone sessions aren't frustrating.
48. As a learner on Android tablet, I want a layout that uses the extra space (e.g. code and trace side by side), so that the tablet is as capable as the Mac.
49. As a learner, I want light and dark themes following the system setting, so that the app is comfortable at any time of day.
50. As a learner, I want the whole app — lessons, code execution, review — to work fully offline, so that I can study anywhere.

### Sync and server
51. As a learner, I want to pair a device with my home server once using a pairing code, so that setup is simple and secure enough for my home network.
52. As a learner, I want progress to sync automatically whenever a device is on my home network, so that my Mac and phone share the same state without manual steps.
53. As a learner, I want progress recorded on two devices while offline to merge without conflicts or lost work, so that I can study anywhere on either device.
54. As a learner, I want to see when a device last synced and trigger a sync manually, so that I can confirm my progress is safe.
55. As a learner, I want the app to keep working normally when the server is unreachable, so that being away from home never blocks study.
56. As the server owner, I want to revoke a paired device, so that I can remove a lost or retired device.
57. As the server owner, I want the server to run with Docker Compose on my Ubuntu laptop, so that deployment and updates are a couple of commands.
58. As the server owner, I want a nightly database backup, so that I never lose my learning history.
59. As the server owner, I want a simple health endpoint, so that I can check the server is running.

### Content authoring and publishing
60. As the content author, I want each lesson to be a folder of human-readable files in git, so that I can write and review lessons with Claude using normal tools.
61. As the content author, I want every step, review item and concept to have a stable ID, so that editing content never orphans progress.
62. As the content author, I want a validator command that checks schema, references and IDs, so that broken lessons never reach the app.
63. As the content author, I want the validator to run every reference solution against its tests in real Python, so that AI-drafted solutions are proven correct.
64. As the content author, I want the validator to check that fill-in-the-blank and Parsons steps have exactly one accepted answer (or an explicit list of accepted answers), so that correct answers are never marked wrong.
65. As the content author, I want the validator to run trace-player code and confirm it produces frames, so that visualisations never break at runtime.
66. As the content author, I want a publish command that builds versioned Content Packs and uploads them to my server, so that new content reaches my devices.
67. As the content author, I want the first track bundled in the app, so that a fresh install works immediately without the server.
68. As the content author, I want devices to download newer Content Packs on home Wi-Fi and cache them, so that updates arrive without reinstalling the app.
69. As the content author, I want each code exercise to declare its language (Python now), so that C# variants can be added later without a format change.
70. As the content author, I want to export the list of flagged steps with my notes, so that I can fix confusing content efficiently.
71. As the content author, I want each lesson to declare which concepts and patterns it teaches and which review items it introduces, so that mastery and Review are driven by content, not code.

### Install and operate
72. As the owner, I want to install the Android app by sideloading a signed APK, so that I don't need a Play Store account.
73. As the owner, I want to build and run the Mac app locally, so that I don't need an Apple Developer subscription.
74. As the owner, I want app data stored locally in SQLite, so that it's fast, offline and easy to inspect.

## Implementation Decisions

### Architecture
- **Client:** .NET MAUI **Blazor Hybrid** app. **macOS (Mac Catalyst) first**; the Android target (phone + tablet) is enabled in the sync milestone. The host app is deliberately thin (window, menus, storage paths, secure storage); almost all behaviour lives in shared libraries so the host can be swapped (Photino.Blazor is the fallback Mac host if Catalyst feels wrong). A native SwiftUI Mac app was rejected because it would double the work and abandon the shared C# core.
- **Server:** ASP.NET Core **minimal API** + **PostgreSQL** (EF Core), deployed with Docker Compose on a headless Ubuntu laptop. Compose also runs a nightly `pg_dump` backup job.
- **Network:** **home network only.** No Tailscale or public exposure. The app is **offline-first**; the server is used only for sync and content distribution, never for code execution.
- **Single user** for v1; no profiles.

### Modules (deep modules marked ★ — simple interfaces, lots of behaviour, testable in isolation)
1. **★ Content Model** — the domain vocabulary: Track, Lesson, Step (typed: Explain, Choice, PredictOutput, FillInBlank, Parsons, WriteCode, Trace), Concept, Pattern, Review Item, Hint Ladder, Mistake Feedback. Loads a Content Pack into an immutable, indexed graph. Interface: load pack → content graph; look up by stable ID.
2. **★ Content Validator & Packer (CLI tool)** — validates a content directory and builds Content Packs. Interface: `validate(contentRoot) → report`, `pack(contentRoot) → versioned pack`, `publish(pack, server)`. Shells out to CPython to run reference solutions against tests and to dry-run trace code. Shares the Content Model with the app.
3. **★ Answer Evaluator** — pure evaluation of non-code responses. Interface: `evaluate(step, response) → result (correct, matched mistake feedback)`. Handles normalisation (whitespace, quotes) for predict-output and fill-in-blank, and order/indent checking for Parsons.
4. **★ Progress Event Log** — append-only, immutable events (e.g. StepAnswered, StepCompleted, HintUsed, CodeSubmitted, ReviewAnswered, LessonCompleted, StepFlagged), each with a globally unique ID, device ID, timestamp and content IDs. Interface: append, read all/since cursor, merge external events (idempotent by event ID).
5. **★ Learner State Projector** — a pure, deterministic function from (events, content graph, now) → learner state: lesson progress, unlocked review items, FSRS card states, per-concept mastery, streak and daily-goal status. Both devices reach identical state after syncing because state is always derived from the merged log.
6. **★ FSRS Scheduler & Rating Inference** — pure. `inferRating(correct, hintsUsed, elapsed, stepType) → Again|Hard|Good|Easy`; `schedule(cardState, rating, now) → cardState`. Uses standard FSRS default parameters (no per-user optimisation in v1).
7. **★ Review Queue Builder** — pure. `build(learnerState, content, now, cap, device) → ordered queue`. Applies the cap, weights pattern-recognition items, and chooses phone-friendly forms of re-solve items on small screens.
8. **★ Python Runtime (interop)** — C# interface over Pyodide running in a Web Worker, bundled with the app for offline use. Interface: `run(code, stdin, timeout) → output`, `runTests(code, tests, timeout) → per-test results`, `trace(code, entrypoint, input) → frames`. Timeouts terminate and recreate the worker. Tracing uses `sys.settrace` to record line number, locals and tracked data structures per frame.
9. **Trace Model & Trace Player** — Trace Model (C#) turns raw frames into renderable states (arrays with pointer annotations, dict/set tables, variables, current line). Trace Player is a Razor component rendering animated SVG with step/back/play/speed controls and keyboard shortcuts. Lesson files declare which variables to visualise and how (e.g. "`nums` as array with pointers `i`, `j`").
10. **Code Editor (interop)** — **CodeMirror 6 everywhere** behind a C# `ICodeEditor` interface (set/get code, read-only ranges, blank regions, line highlight, error markers, keymap/theme). VS Code keymap and theme on Mac. Monaco was rejected (heavy, multi-instance unfriendly, worker loading issues under BlazorWebView, no real Python IntelliSense anyway); it could be added behind the same interface later if ever needed.
11. **Lesson Player UI (Razor Class Library)** — step renderers for every step type, lesson flow, hint ladder UI, feedback, session runner (Review → Lesson), Today screen, track/concept views. Responsive layouts: desktop sidebar, tablet split view, phone single column.
12. **Local Store** — SQLite via EF Core on device: event log, cached Content Packs, code drafts, settings, sync cursor, device token.
13. **★ Sync Client / Sync API** — event-log sync: client pushes events the server hasn't seen and pulls events since its cursor. Idempotent by event ID; no conflict resolution needed. Code drafts are the single last-write-wins exception. Sync runs on app start, periodically and on demand when the server is reachable.
14. **Content Distribution API** — list available Content Packs with versions; download a pack. The client compares versions and downloads newer packs on home Wi-Fi.
15. **Device Pairing & Auth** — the server prints/displays a one-time pairing code; the app exchanges it (plus server address) for a long-lived device token stored in platform secure storage; every API call carries the token; devices are revocable server-side.
16. **Host App(s)** — MAUI app: Mac Catalyst (menus, shortcuts, window sizing) now; Android target later.

### API contract (high level)
- `POST /pair` — pairing code → device token.
- `POST /sync/events` — push a batch of events (idempotent).
- `GET /sync/events?since={cursor}` — pull events after a cursor; returns events + new cursor.
- `PUT /drafts/{exerciseId}` / `GET /drafts` — code drafts, last-write-wins by timestamp.
- `GET /content/packs` — available packs and versions; `GET /content/packs/{id}/{version}` — download.
- `DELETE /devices/{id}` — revoke a device. `GET /health`.
- All except `/pair` and `/health` require the device token. **Plain HTTP restricted to the LAN** in early milestones; HTTPS with a local CA (certificate installed on devices) in M4.

### Content format
- A single repository holds the app source, the `content` directory and tools.
- Content is organised as track → lesson folders, each with a lesson definition file (YAML) plus code files (starter code, reference solution, tests, trace scripts).
- Every step, review item, concept and pattern has a **stable ID**; progress attaches to IDs, not positions. Edited steps keep history; deleted steps retire their review items; new steps show as "new" without un-completing a lesson.
- Lessons declare: concepts taught, patterns taught, review items introduced, per-step hint ladders, mistake-specific feedback, and a "large screen preferred" flag where relevant.
- Code exercises declare `language` (Python only in v1).
- Content Packs are versioned zip archives of normalised JSON + assets. Track 1 is bundled into the app.

### Track 1 curriculum — "Python for C# Devs → Arrays & Hashing"
1. Python through C# eyes · 2. Control flow & functions · 3. Lists · 4. Strings · 5. Dicts & sets · 6. Big-O by feel · 7. Pattern: hash-map lookup · 8. Pattern: counting & frequency · 9. Pattern: grouping by key · 10. Pattern: prefix sums · 11. Capstone mix (unlabelled problems; identify the pattern first).
Planned follow-on tracks: two pointers & sliding window → stacks, queues & linked lists → recursion & trees → graphs (BFS/DFS) → heaps & binary search → dynamic programming.
Content is drafted with Claude and reviewed by the learner; the validator is the safety net for correctness.

### Milestones (≈ 8–10 h/week, built with Claude Code; estimates, not commitments)
- **M0 — Mac walking skeleton (~2 wks):** MAUI Blazor Hybrid on Mac Catalyst; Content Model + bundled pack; Explain/Choice/PredictOutput steps; event log in SQLite; basic validator. **Plus a half-day spike proving Pyodide loads and runs offline in BlazorWebView on an Android phone.** Lessons 1–2 drafted.
- **M1 — Code steps (~3 wks):** Python Runtime (Pyodide worker, timeouts), CodeMirror editor, FillInBlank, Parsons, WriteCode with tests; validator runs reference solutions. Lessons 1–4. **Daily study starts.**
- **M2 — Review + Trace Player (~4 wks):** Projector, FSRS, rating inference, Review Queue, mastery bars, streak/daily goal, hint ladder, Trace Player. Lessons 5–11.
- **M3 — Server, sync & Android (~4 wks):** minimal API + Postgres in Docker, pairing, event sync, content distribution, nightly backup; enable Android target, responsive phone/tablet layouts, touch Parsons.
- **M4 — Practice & hardening (~3 wks):** Practice mode with timed interview simulation, local-CA HTTPS, Track 2.
Main risks: Pyodide inside BlazorWebView (especially Android WebView) and Trace Player complexity.

## Testing Decisions

- **What makes a good test:** test external behaviour through a module's public interface — given these events/content/inputs, expect this state/result — never internal implementation details. Tests should survive refactors. Pure modules are tested without UI, database or network.
- **Content validator as a hard gate:** no Content Pack can be published unless validation passes: schema and reference integrity, unique and stable IDs, reference solutions passing their tests in CPython, Parsons and fill-in-blank answers well-defined, trace scripts producing frames.
- **TDD (red-green-refactor) for the domain core:** Answer Evaluator, Progress Event Log merge semantics, Learner State Projector, FSRS Scheduler & Rating Inference, Review Queue Builder, and sync idempotency. These are where subtle bugs would silently corrupt progress.
- **Content Model:** tests for loading packs, ID lookup, and content-change semantics (edited, deleted and added steps vs existing progress).
- **Sync API:** integration tests against a real PostgreSQL (e.g. Testcontainers) covering pairing, auth, idempotent push, pull-since-cursor and device revocation.
- **UI:** light bUnit tests for step renderers (correct and incorrect answer flows, hint ladder progression). No full automated MAUI UI testing.
- **Python Runtime and Trace:** a small set of automated checks of the trace frame format, plus manual verification on each platform.
- **Manual smoke checklist** on Mac (and Android from M3) before each build is used for study.
- **Prior art:** none yet — this is a greenfield repository. Conventions established in M0 become the prior art.

## Out of Scope (v1)

- Kids' version, maths content (grades 5–6, high school) and multiple learner profiles — the engine stays subject-agnostic so this can branch off later.
- DSA content in C# and on-device C# execution (the format is language-tagged to allow it later).
- Access from outside the home network (Tailscale, public exposure, cloud hosting).
- Server-side code execution.
- In-app content authoring or editing.
- AI tutor / "explain my mistake" with Claude — optional, online-only, server-proxied feature for M4 or later, off by default.
- Interactive-manipulation steps (e.g. clicking cells in the order binary search visits them).
- Gamification beyond streak and daily goal (gems, leaderboards, social features).
- Play Store / App Store distribution, iOS, Windows.
- Per-user FSRS parameter optimisation.
- Monaco editor.

## Further Notes

- **Learner context:** the learner is a Python beginner with a C# background and weak DSA, aiming for interview-level mastery through sustained daily practice. Content should constantly connect Python idioms to C# equivalents (comprehensions vs LINQ, `dict` vs `Dictionary<,>`, etc.) and emphasise pattern recognition.
- **Build vs study tension:** every hour building is an hour not studying, so milestones are ordered to get daily study going on the Mac by around week 5.
- **Future reuse:** keep the Content Model, step types and Projector free of programming-specific assumptions so maths steps (e.g. number lines, fractions) can be added for the kids' version.
- **Issue tracker:** no tracker is configured yet; this PRD lives in `docs/`. The next step is breaking the milestones into issues (`to-issues`), which carry the `ready-for-agent` label once a tracker exists.
- **Glossary:** Track, Lesson, Step, Concept, Pattern, Review Item, Hint Ladder, Mistake Feedback, Content Pack, Progress Event, Learner State, Mastery, Trace, Frame, Session.
