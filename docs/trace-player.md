# Trace Player: arrays, dicts and sets, stepping and playback

PRD stories 24, 25 and 27 ([#23](https://github.com/joeizang/brilliantApp/issues/23)) and 26, 28, 29 and 65 ([#24](https://github.com/joeizang/brilliantApp/issues/24)).
A *trace* step runs a small example once, records every line it executes, and lets the learner walk through it: the code with the current
line highlighted, the watched lists drawn as cells with named pointers that slide as they move, the watched dicts and sets drawn as
tables whose rows appear, flash and fade, and the local variables with what just changed marked. It steps by button or arrow key and
plays by itself at 0.5×, 1× or 2×.

```
lesson.yaml  type: trace            ──►  TraceStep { Code, Visuals }
TraceStepView                       ──►  IPythonRuntime.TraceAsync(code, visuals)
  Pyodide worker  tracer.py (sys.settrace)  ──►  JSON  TraceResult { Status, Frames[], Stdout, … }
TraceModel.Build(result, visuals)   ──►  TraceState[]   (one per frame, with Changed / Moved / RowChange flags)
TracePlayer                         ──►  read-only CodeMirror + SVG cells/pointers + TraceTable(s) + locals + Previous / Play / Next
```

Nothing about a trace is stored in the Content Pack: the example is recorded when the step is shown (Python is bundled, so this works
offline and takes a few milliseconds once Pyodide has started). `content validate` proves the example runs, in CPython, before it can ship.

## Authoring

```yaml
- id: step.lists.walk
  type: trace
  title: Walking a list
  body: |
    Watch `i` move one cell at a time. `nums[i]` is whatever cell `i` sits on.
  code: |
    nums = [4, 8, 15]
    total = 0
    i = 0
    while i < len(nums):
        total += nums[i]
        i += 1
  visualise:
    - variable: nums        # a list or tuple, drawn as cells
      as: array
      pointers: [i]         # int variables drawn as labelled markers on the cell they index
    - variable: counts      # a dict (or dict subclass), drawn as key/value rows
      as: dict
      pointers: [word]      # variables holding a key: the row they look up is marked
    - variable: seen        # a set or frozenset, drawn as a run of members
      as: set
```

`as` is `array`, `dict` or `set`. A pointer on a dict or set is any variable, of any type, whose value is a key: the row with that key
is marked with the pointer's name, and when no row has that key the player says *Looking up `word` finds no such key.*

`body` and `code` are required, and there must be at least one visual. A trace step takes no `hints` and can't be a review item.
The validator rejects, with the file and step in the message:

- an unknown `as`, a missing `variable`, a name that isn't a Python identifier, the same variable drawn twice, or a pointer listed twice;
- code that raises (the message has the line and the traceback) or runs past the recorder's limit;
- code with no executable line (empty or comment-only code), which would give the learner nothing to step through;
- a `variable` that is never the kind of value its `as` draws (a list or tuple, a dict, a set) in the trace, a pointer on an array that is
  never an `int`, and a pointer on a dict or set that is never a variable at all. Each would draw nothing.

## The recorder (`wwwroot/python/tracer.py`)

`trace_code(code, watch_json)` runs the code with `sys.settrace` and returns JSON. It is plain stdlib Python so the CLI runs the same file
under CPython. Each frame is the state *just before* a line runs (`event: "line"`), or the state the module finished in (`event: "return"`,
no line). A frame holds the function name, the locals (name, type, clipped repr) and, per watched variable, either `tracked` (a list or
tuple: the first cells' reprs) or `tables` (a dict or set: `kind`, the Python `type` name, the first rows as `{id, key, value}` and `hits`).
A dict keeps its insertion order. A set has none, so its members are sorted (by value, falling back to sorted by repr for mixed types) to
keep them from jumping about between steps.

A row's `key` is only a label: its repr, clipped to 24 characters, so two different long keys can read the same. What tells rows apart is
`id`, a number the recorder gives each distinct key (or member) the first time it meets it in the run and then reuses at every step. Keys
are told apart the way a dict tells them apart, so `1`, `1.0` and `True` share an id, as they share a row.

`hits` is the recorder's answer to "which variables are looking something up here": for every variable the frame lists, `value in table`,
by Python's own equality, and if so that key's id. `key = 1.0` hits the row for `1`; an unhashable value, or one whose `__eq__` raises, is
simply not a hit. A hit can carry the id of a row that was cut off by the 20-row limit, so it names no shown row.

Limits keep a runaway example from hanging or flooding the UI: 300 frames (then `status: "truncated"`), 24 locals, 40 cells per list and 20 rows per
dict or set (the rest are counted as *+N more*; sets over 500 members are shown in iteration order rather than sorted at every step), reprs clipped, 4000 characters of output. Modules, functions, classes and dunder names are hidden from the
locals. An exception ends the trace with `status: "error"`, keeping the frames recorded so far and the traceback, so the learner still sees
how far it got. The JS bridge (`python-runtime.js`) applies the same run timeout as tests: a hang terminates and recreates the worker. Only the request that timed out gets its own kind of timeout result; any other request pending on the same worker is rejected ("Python was restarted because another run took too long") rather than handed a result of the wrong shape.

## The Trace Model (`Brilliant.Core/Python/Trace.cs`)

`TraceModel.Build` turns frames into `TraceState`s and is pure, so it is where the unit tests are. It compares each frame **only with the
one before it, and only when both ran in the same function**; entering or leaving a call marks nothing, because a different frame's locals
being "new" isn't a change the learner caused.

- `LocalView.Changed`: the name is new or its repr differs.
- `CellView.Changed`: the cell is new or its text differs.
- `RowView.Id`: what a row is keyed by (and what the Razor `@key` uses), taken from the recorder's `id`, never from the clipped label. In a
  trace recorded before ids existed it falls back to the label.
- `RowView.Change` (dicts and sets): `Added` when the id is new, `Updated` when its value differs (dicts only), `Removed` for a key the
  previous step had and this one does not (matched by id, so two keys with the same label are not confused). A removed row is kept as a ghost for that one step so the learner sees it go, and is never
  compared again. The first step marks nothing, and neither does the first step in a new function.
- `RowView.LookedUpBy`: the pointer variables the recorder reported as hitting that row's id. `TableView.Misses` are the pointers that
  exist but hit nothing at all (a hit on a row cut off by the 20-row limit is neither a lookup nor a miss, since the key may well be there).
  The model never compares reprs; a trace without `hits` marks no lookups and reports no misses.
- A table is drawn only when the recorded value really is the kind the visual asks for (a list named as `dict` draws nothing).
- `PointerView.Index`: the cell the pointer's variable indexes. Null when the variable is missing, isn't an `int`, or is outside the
  recorded cells (negative, or past the end). `Moved` is true when the index differs from the previous state's.

## The player

- `TracePlayer.razor`: a read-only `CodeEditor` (new `ReadOnly` parameter) with the current line highlighted through the existing
  `HighlightLineAsync`; Previous, Play/Pause and Next; *Step n of m*; the speed buttons; one SVG per watched list; one `TraceTable` per
  watched dict or set (after the lists, whatever order the lesson names them in); a *Variables* table; and, on the last step only,
  what the run printed, the error that ended it, or a note that it was cut short (`TraceEnding.razor`).
- **Animation is CSS.** A pointer is a `<g>` positioned with `style="transform: translate(…)"` and keyed by variable and name, so Blazor
  patches the same element and the browser transitions it from the old cell to the new one. A changed cell is keyed by step too, so it is
  a fresh element that replays its flash on every step it changes in. Changed rows of the variables table do the same, and so do the
  added, updated and removed rows of a `TraceTable` (added rows grow in, updated rows flash, removed rows are struck through).
  `prefers-reduced-motion` turns all of it off (theme.css).
- Pointers sharing a cell are stacked on rows below it so every label stays readable. A pointer that isn't on a cell isn't drawn; the
  player says *i is not on a cell right now* under the list instead.
- Colours are the theme tokens, so it follows light/dark. Everything is in `rem`, and a long list scrolls sideways inside its own box.
- A trace step is finished with *Continue* (or Return), like an explanation: it records `StepCompleted`, nothing else.

### Playback

Play starts a `TimeProvider` timer that moves one step on each tick: every second at 1×, two seconds at 0.5×, half a second at 2×. It
stops by itself on the last step, where the button becomes *Replay* (which starts again from step 1). The rules that keep it from
fighting the learner:

- Stepping by hand (the buttons or the arrow keys) pauses playback.
- Changing the speed while playing re-times the same timer, so the place is kept; while paused it only sets the speed.
- A new trace (new `States`) stops playback and starts at step 1; rendering again with the same trace changes nothing.
- A one-step trace has nothing to play (Play is disabled). Leaving the step disposes the timer and the shortcut subscription.

The timer comes from the injected `TimeProvider` (`TimeProvider.System` in the app, a manual clock in the tests), so playback is tested one
tick at a time.

### Keyboard

`shortcuts.js` classifies `ArrowRight`, `ArrowLeft` and `Space` as `next`, `previous` and `playpause`, **only while a `.trace-player` is on the
page**, and `CourseShell.OnShortcut` turns them into `StepCommand.Next / Previous / PlayPause` on the `StepShortcuts` bus, which the
player subscribes to. The guards:

- never with ⌘, Ctrl, Alt or Shift held, so browser and OS shortcuts are untouched;
- not while the learner is typing (a text box or the code editor's own cursor), except in the player's own read-only editor, which is
  not a place anyone types, so the arrows work wherever the focus is inside the player;
- Space is ignored when a button has focus, because the browser clicks it; on macOS WebKit a mouse click doesn't focus a button, so
  Space still works right after clicking Play;
- a held key (`repeat`) steps with the arrows, but Space only toggles once per press;
- IME composition is ignored.

There are no Mac menu bar items for the arrows (the system already uses them for text navigation).

## Not in this slice

Per-variable speed or scrubbing, drawing nested structures (a list of lists, a dict of lists), and reading a trace aloud.
`Visual.As` is a string and the model builds one `ArrayView` per `array` visual and one `TableView` per `dict` or `set` visual, so another kind
slots in beside them.

## Tests

`tests/python/test_tracer.py` (recorder), `tests/Brilliant.Core.Tests/TraceTests.cs` (model and JSON),
`tests/js/python-runtime.test.mjs` (bridge), `tests/Brilliant.Cli.Tests/TraceStepValidatorTests.cs` (YAML rules and the CPython check),
`tests/Brilliant.Lessons.UI.Tests/TracePlayerTests.cs` and `TraceStepViewTests.cs` (rendering, stepping, playback on a manual clock, shortcuts,
line highlight, lesson wiring), `tests/js/shortcuts.test.mjs` (key classification) and `ShortcutTests.cs` (the page listener's mapping).
bUnit can't observe a CSS transition or element reuse, so the sliding, flashing and the ghost row's fade are checked by looking at the running app.

Known limit: the recorder keeps only the first 20 rows, so a pointer on a later row is shown as nothing (neither marked nor a miss) beside
*+N more*.
