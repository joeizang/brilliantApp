# Trace Player: arrays with pointers, step forward and back

PRD stories 24, 25 and 27 ([#23](https://github.com/joeizang/brilliantApp/issues/23)). A *trace* step runs a small example once, records
every line it executes, and lets the learner walk through it: the code with the current line highlighted, the watched lists drawn as cells
with named pointers that slide as they move, and the local variables with what just changed marked.

```
lesson.yaml  type: trace            ──►  TraceStep { Code, Visuals }
TraceStepView                       ──►  IPythonRuntime.TraceAsync(code, visuals)
  Pyodide worker  tracer.py (sys.settrace)  ──►  JSON  TraceResult { Status, Frames[], Stdout, … }
TraceModel.Build(result, visuals)   ──►  TraceState[]   (one per frame, with Changed / Moved flags)
TracePlayer                         ──►  read-only CodeMirror + SVG cells/pointers + locals + Previous / Next
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
      as: array             # the only kind so far (dicts and sets come later)
      pointers: [i]         # int variables drawn as labelled markers on the cell they index
```

`body` and `code` are required, and there must be at least one visual. A trace step takes no `hints` and can't be a review item.
The validator rejects, with the file and step in the message:

- an unknown `as`, a missing `variable`, a name that isn't a Python identifier, the same variable drawn twice, or a pointer listed twice;
- code that raises (the message has the line and the traceback) or runs past the recorder's limit;
- a `variable` that is never a list or tuple in the trace, and a pointer that is never an `int` in it. Both would draw nothing.

## The recorder (`wwwroot/python/tracer.py`)

`trace_code(code, watch_json)` runs the code with `sys.settrace` and returns JSON. It is plain stdlib Python so the CLI runs the same file
under CPython. Each frame is the state *just before* a line runs (`event: "line"`), or the state the module finished in (`event: "return"`,
no line). A frame holds the function name, the locals (name, type, clipped repr) and, per watched variable, the first cells' reprs.

Limits keep a runaway example from hanging or flooding the UI: 300 frames (then `status: "truncated"`), 24 locals, 40 cells per list (the
rest are counted as *+N more*), reprs clipped, 4000 characters of output. Modules, functions, classes and dunder names are hidden from the
locals. An exception ends the trace with `status: "error"`, keeping the frames recorded so far and the traceback, so the learner still sees
how far it got. The JS bridge (`python-runtime.js`) applies the same run timeout as tests: a hang terminates and recreates the worker. Only the request that timed out gets its own kind of timeout result; any other request pending on the same worker is rejected ("Python was restarted because another run took too long") rather than handed a result of the wrong shape.

## The Trace Model (`Brilliant.Core/Python/Trace.cs`)

`TraceModel.Build` turns frames into `TraceState`s and is pure, so it is where the unit tests are. It compares each frame **only with the
one before it, and only when both ran in the same function**; entering or leaving a call marks nothing, because a different frame's locals
being "new" isn't a change the learner caused.

- `LocalView.Changed`: the name is new or its repr differs.
- `CellView.Changed`: the cell is new or its text differs.
- `PointerView.Index`: the cell the pointer's variable indexes. Null when the variable is missing, isn't an `int`, or is outside the
  recorded cells (negative, or past the end). `Moved` is true when the index differs from the previous state's.

## The player

- `TracePlayer.razor`: a read-only `CodeEditor` (new `ReadOnly` parameter) with the current line highlighted through the existing
  `HighlightLineAsync`; Previous / Next with *Step n of m*; one SVG per watched list; a *Variables* table; and, on the last step only,
  what the run printed, the error that ended it, or a note that it was cut short (`TraceEnding.razor`).
- **Animation is CSS.** A pointer is a `<g>` positioned with `style="transform: translate(…)"` and keyed by variable and name, so Blazor
  patches the same element and the browser transitions it from the old cell to the new one. A changed cell is keyed by step too, so it is
  a fresh element that replays its flash on every step it changes in. Changed rows of the variables table do the same.
  `prefers-reduced-motion` turns all of it off (theme.css).
- Pointers sharing a cell are stacked on rows below it so every label stays readable. A pointer that isn't on a cell isn't drawn; the
  player says *i is not on a cell right now* under the list instead.
- Colours are the theme tokens, so it follows light/dark. Everything is in `rem`, and a long list scrolls sideways inside its own box.
- A trace step is finished with *Continue* (or Return), like an explanation: it records `StepCompleted`, nothing else.

## Not in this slice

Dicts and sets (story 26), play/pause and speed (28), and arrow-key shortcuts (29). `Visual.As` is a string and the model builds one
`ArrayView` per `array` visual, so another kind slots in beside it.

## Tests

`tests/python/test_tracer.py` (recorder), `tests/Brilliant.Core.Tests/TraceTests.cs` (model and JSON),
`tests/js/python-runtime.test.mjs` (bridge), `tests/Brilliant.Cli.Tests/TraceStepValidatorTests.cs` (YAML rules and the CPython check),
`tests/Brilliant.Lessons.UI.Tests/TracePlayerTests.cs` and `TraceStepViewTests.cs` (rendering, stepping, line highlight, lesson wiring).
bUnit can't observe a CSS transition or element reuse, so the sliding and flashing are checked by looking at the running app.
