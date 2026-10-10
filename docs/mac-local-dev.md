# Building and running on Mac (local)

## Prerequisites

- macOS with Xcode installed (`xcodebuild -version`)
- .NET 10 SDK
- The MAUI Mac Catalyst workload (needs admin rights, once):

  ```bash
  sudo dotnet workload install maui-maccatalyst
  ```

## Layout

| Path | Purpose |
|---|---|
| `src/Brilliant.Core` | Content Model: records, `ContentGraph` (lookup by stable ID), Content Pack reader/writer |
| `src/Brilliant.Data` | Local store (placeholder; SQLite arrives with the event log) |
| `src/Brilliant.Lessons.UI` | Razor Class Library: step renderers (`ExplainStepView`, `LessonViewer`) |
| `src/Brilliant.App` | Thin MAUI Blazor Hybrid host (Mac Catalyst) |
| `tools/Brilliant.Cli` | Validator & packer CLI |
| `content/` | Lesson source (YAML) — `pack.yaml`, `tracks/<track>/track.yaml`, `tracks/<track>/<lesson>/lesson.yaml` |
| `tests/` | xUnit tests for Core and the CLI |

## Workflow

```bash
# run tests (no MAUI workload required for the test projects)
dotnet test tests/Brilliant.Core.Tests && dotnet test tests/Brilliant.Cli.Tests

# validate content; prints readable errors (file + message) and exits 1 on bad content
dotnet run --project tools/Brilliant.Cli -- validate content

# validate + build the versioned Content Pack into the app's bundled assets
scripts/pack-content.sh

# run the app
dotnet build src/Brilliant.App -t:Run -f net10.0-maccatalyst
```

## Content rules enforced by the validator

- Every track, lesson and step has a stable `id` (`track.…`, `lesson.…`, `step.…`; lowercase words joined by `-` or `.`), unique across the pack.
- `pack.yaml` has an `id` and a `version` like `1.2.3`.
- Step types so far: `explain`, `choice`, `predict-output`, `write-code`, `fill-blank` and `parsons`.
  - `explain` needs `title` and `body` (markdown); `snippets` and a C#↔Python `comparison` are optional but must be complete if present.
  - `choice` needs `title`, `prompt` and at least 2 `options` (`text`, `correct`, optional `feedback`). Exactly one option must be correct unless `multiSelect: true`.
  - `predict-output` shows a `code` snippet (optional `language`, default `python`) and a `prompt`, in one of two variants (exactly one is required):
    - **typed**: `accepted` lists the correct outputs. Optional `mistakes` give specific feedback; each needs `feedback` plus `answers` (exact matches) and/or a `regex`, and must not repeat an accepted answer.
    - **multiple choice**: `options` as for `choice`, with exactly one `correct`; `mistakes` are not allowed (use per-option `feedback`).
    - Answers are compared after normalising: runs of spaces/tabs collapse, lines and ends are trimmed, curly quotes are straightened and `"`/`'` are treated alike. Case still matters. Regexes run against the normalised response.
  - `fill-blank` shows code that is read-only except at its blanks. It needs `prompt`, a `template` and `blanks` (optional `language`, only `python` for now):
    - In the `template`, `{{blank-id}}` marks a blank (lowercase words joined by `-`); everything else is read-only code. Every marker must name a declared blank, and every blank must appear exactly once. Literal double braces (an f-string's `{{`) aren't supported in a template.
    - Each blank has an `id`, one or more `accepted` answers (single-line, no duplicates after normalising) and optional `mistakes` with the same shape and rules as `predict-output` mistakes (`answers` and/or `regex`, plus `feedback`).
    - Answers are normalised exactly as for `predict-output` (runs of spaces collapse, quotes unify, case still matters), so list spacing variants the learner might reasonably type (`range(1,n)` and `range(1, n)`) or accept them all with a `regex`.
    - Each blank is judged on its own: the learner sees which are right and any mistake feedback, keeps the right ones and fixes the rest. Attempts are recorded as `StepAnswered` with the text of every blank.
    - Not yet supported: checking the finished code by running tests, and multi-line blanks.
  - `parsons` shows the lines of a short program shuffled; the learner puts them in order and gives each the right indentation. It needs `prompt` and a `solution` (optional `language`, only `python` for now):
    - `solution` is the program as it should end up, written normally. The validator reads the order and indentation from it, so the lines and their levels are never listed separately. Blank lines are dropped and every other line becomes one piece. Indent with spaces, in a consistent unit (2 or 4 spaces, say).
    - The validator checks the solution is structurally sound, since there is no syntax check for it: at least 2 lines and at least 2 distinct ones, the first line not indented, a line ending in `:` (a trailing `# comment` is ignored) followed by a line one level deeper, and no line deeper than the one before it unless that one ends in `:`.
    - Lines are compared by text and level, row by row, so two identical lines can be swapped freely. The learner sees which rows are right, and whether a wrong one has the right line with the wrong indentation or needs a different line. They keep their arrangement and fix what's wrong.
    - Moving: drag a line, use the ▲▼ buttons, or press <kbd>⌥</kbd><kbd>↑</kbd> / <kbd>⌥</kbd><kbd>↓</kbd> on the selected line. Indenting: the ⇤⇥ buttons or <kbd>⌥</kbd><kbd>←</kbd> / <kbd>⌥</kbd><kbd>→</kbd>. Lines can't be indented deeper than the solution's deepest level.
    - Attempts are recorded as `StepAnswered` with the verdict and the arrangement (each row's solution-line index and level).
    - Not yet supported: distractor lines that don't belong, and several valid orderings (e.g. independent statements that can swap).
- A lesson may declare, alongside `steps`:
  - `concepts`: ideas it teaches (`id: concept.…`, `title`).
  - `reviewItems`: questions Review will resurface later (`id: review.…`, `concept` declared in the same lesson, `step` = a `choice` or `predict-output` step of the same lesson, which is reused as the question).
  - Question steps (`choice`, `predict-output`) may carry `hints`: up to 4, gentlest first (nudge → pattern hint → partial → full walkthrough). Hints and review items are authored and validated now; the app shows/schedules them in later issues.
- Lessons in a track are ordered by folder name (`01-…`, `02-…`) and unlock in that order.
- Unknown YAML fields are errors, so typos aren't silently ignored.

## Progress storage

Progress is an append-only event log (`StepAnswered`, `StepCompleted`, `LessonCompleted`) in SQLite at
`~/Library/Containers/com.joeizang.brilliantapp/Data/Library/brilliant.db`. Database triggers reject UPDATE/DELETE.
`LessonCompleted` is written once, when a lesson's last step is completed. Lock/completion state is derived from the log
(a lesson is unlocked when the previous one in its track is complete). **Review mode** (replaying a completed lesson) records nothing, so it can't change completion.
The current step is derived from the log, so relaunching resumes where you left off. To start a lesson over, quit the app and delete that file:

```bash
sqlite3 ~/Library/Containers/com.joeizang.brilliantapp/Data/Library/brilliant.db "select Seq, Type, StepId, Data from Events"
```

## Theming

Colours live as CSS custom properties in `src/Brilliant.Lessons.UI/wwwroot/theme.css` (served at `_content/Brilliant.Lessons.UI/theme.css`).
Dark is the default; the light variant applies under `prefers-color-scheme: light`, so the app follows the macOS appearance and updates live.
Components must use the tokens (`var(--accent)` etc.), never raw colours. Shared button styles (`primary`, `secondary`, `link`) are global in the same file.

## Code editor and Python runtime (issue #9)

- **Editor:** CodeMirror 6, bundled into `src/Brilliant.Lessons.UI/wwwroot/code-editor.js` (committed). Sources are in
  `src/Brilliant.Lessons.UI/editor/`; after changing them run `scripts/build-editor.sh` (needs Node) and commit the bundle.
- **Python:** the bundled Pyodide runs in a Web Worker (`wwwroot/python-worker.js`, bridged by `python-runtime.js`, exposed to the UI as
  `IPythonRuntime`). The test harness is `wwwroot/python/harness.py`; check it under CPython with
  `python3 -I -m unittest discover -s tests/python`.
- Runs are limited to 5 seconds (`PyodideRuntime.RunTimeout`). A run that exceeds it is stopped by terminating the worker; the next run starts a fresh one.
  JS bridge logic is tested with `node --test tests/js/python-runtime.test.mjs`.

## Reference solutions (issue #11)

Every `write-code` step in `content/` needs a `solution:` (authoring-only, never packed). `validate` and `pack` run each solution
against the step's tests with the same `harness.py` under CPython (`python3`, or the interpreter in `BRILLIANT_PYTHON`) and refuse to
produce a pack if any fails, reporting lesson file, step, test, expected and actual.

## Code drafts (issue #12)

What a learner types in a write-code step is saved automatically, so leaving the step, the lesson or the app doesn't lose it.

- **Where:** `drafts.db` in the app data directory, one row per step ID (`Drafts` table, `SqliteCodeDraftStore`). It is deliberately a separate file from `brilliant.db`: progress is an append-only event log, drafts are mutable and the latest write wins (the one exception to the event-log rule, per the PRD, and what draft sync in #30 will exchange).
- **When:** the editor reports changes after 500 ms of no typing, and once more as the editor goes away, so navigating immediately after typing still saves. Running the tests does not touch the draft.
- **Opening a step:** restores the draft if there is one, otherwise the starter.
- **Reset to starter:** asks for confirmation inline ("Yes, reset" / "Keep my code"), then puts the starter back, clears the previous test results and error markers, and discards the draft. The row is kept as a tombstone (`Code` null, new `UpdatedAt`) so a later sync can tell the reset happened after another device's edit.
- **Edited back to the starter:** stored as "no draft", so a later improvement to the starter still reaches learners who never really changed it.
- **Editor bundle:** `code-editor.js` was rebuilt (`scripts/build-editor.sh`) for the change notification; edits made by `setCode` (reset) are not reported back.

To wipe drafts while testing, quit the app and delete `drafts.db` from the app data directory (see "Progress storage").
