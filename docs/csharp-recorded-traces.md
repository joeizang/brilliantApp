# Recorded C# traces

Issue [#153](https://github.com/joeizang/brilliantApp/issues/153), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)).

A C# `trace` step (or a solution walkthrough) is written exactly like a Python one: `code` plus `visuals`. The difference is
where it is run. Python records its trace on the device, in Pyodide. C# is not run on the device at all, so **the CLI records
it when the pack is built** and writes the recording into the pack. The trace player plays the recording, so it works on Mac and
Android alike with no C# runtime installed.

```
lesson.yaml  type: trace (language: csharp)
  │  brilliant validate / pack
  ▼
ReferenceSolutionChecker.CheckTraces ──► worker process (--csharp-trace-worker) ──► CSharpTraceRecorder.Record(code, visuals)
  │                                                                                      │ probe rewriter + budget, one snapshot per statement
  ▼                                                                                      ▼
TraceStep.Recorded / Walkthrough.Recorded  (a TraceResult, the same JSON the Python tracer produces)
  │  pack
  ▼
TraceRun: Recorded ?? Runtimes.Resolve(Language).TraceAsync(...)  ──►  TraceModel.Build  ──►  TracePlayer
```

## What is recorded

`CSharpTraceRecorder` (`src/Brilliant.Runtime.CSharp`) compiles the code with `ProbeRewriter`, which inserts a probe before each
statement, runs it on a dedicated thread under the usual `ProbeBudget`, and turns each probe into a `TraceFrame`:

| Frame field | C# source |
| --- | --- |
| `Event`, `Line` | `"line"` and the 1-based line about to run; the last frame is `"return"` with no line (the state after the last statement) |
| `Function` | the method, local function or lambda the probe is in; top-level code is `Main` |
| `Locals` | the variables in scope, sorted by name (ordinal) so the order never depends on declaration order |
| `Tracked` | each variable a `visuals` entry names: `array`, `stack` or `queue` drawings |
| `Tables`, `Heaps`, `Grids` | the `dict`, `set`, `heap` and `grid` drawings, see below |
| `Lists`, `Trees` | the `linked_list` and `tree` drawings, see below |
| `Graphs` | the `graph` drawings, see below |
| `Calls` | the `call_stack` drawing, and with it a step for each return, see below |
| `Stdout` | everything `Console.Write*` printed, with `\n` line endings |

Collections map onto the existing views by shape: an array or `List<T>` is a row of cells front to back, a `Stack<T>` is drawn bottom
to top, a `Queue<T>` front to back. Types are shown as C# names (`int[]`, `List<string>`, `Dictionary<string, int[]>`); see `TypeNames`.

### Limits

These mirror the Python tracer so the player behaves the same: 300 frames, 24 locals per frame, 40 cells per array (the rest are
counted in `More`), 60-character reprs (24 for a cell) and 4000 characters of stdout. A run that hits a limit, an infinite loop or
the step/time budget is recorded as `Truncated` with a message; a run that throws is recorded as `Error` with the frames up to the
throw. Both play like their Python equivalents.

Known gaps: `PriorityQueue<,>` is not drawn as a heap, loop conditions and `for` headers get no step of their own, and a top-level `return;` before the end of the code leaves
no closing frame. `async`, iterators, `unsafe`, `#define`/`#undef` and interactive console input are refused.

## Determinism

Re-recording the same source gives byte-identical output (and so a byte-identical pack): the assembly is built with `deterministic`,
locals are sorted, stdout is normalised, and nothing that depends on time, thread or address is recorded. The one exception is the 2-second wall-clock budget: an example that comes near it could record `Ok` on a fast machine and `Truncated` on a slow one. Only the step and frame limits are reproducible, so teaching examples must finish well inside it. Golden files in
`tests/Brilliant.Runtime.CSharp.Tests/Traces/` (`*.snippet` with its `*.trace.json`) pin the output for a fixed set of snippets.
After an intended change, regenerate them with `UPDATE_GOLDENS=1 dotnet test tests/Brilliant.Runtime.CSharp.Tests` and review the diff.

## In the pack

`TraceStep.Recorded` and `Walkthrough.Recorded` hold the `TraceResult`. They are never authored: `validate` records them and
`pack` attaches them. They are written with `TraceResultJson.Serialize` (camelCase, string enums, nulls omitted) inside the pack's
JSON like any other field. `TraceStepView` and `HintLadder` hand the recording to `TraceRun`, which plays it and never asks a runtime.
Step ids are unique across a pack, so recordings are matched to steps by `(step id, walkthrough or trace)`.

Recording runs in a child process (`dotnet Brilliant.Cli.dll --csharp-trace-worker`) that is killed if it overruns, the same containment
as the reference-solution gate ([csharp-reference-checks.md](csharp-reference-checks.md)); it only runs on content that is already
structurally valid. A trace that fails to record fails validation with the reason.

## Tables, heaps and grids

Issue [#196](https://github.com/joeizang/brilliantApp/issues/196) adds the views that need no node identity. The CLI now sends the worker
the whole `visuals` list (pointers, labels, highlight and all), and `CSharpTraceViews` builds each view from the frozen variables of one
probe, following the Python tracer's rules and limits:

| `as` | C# types drawn | Notes |
| --- | --- | --- |
| `dict` | `Dictionary<,>`, `SortedDictionary<,>`, `SortedList<,>` | 20 rows, the rest in `More`; rows keep the collection's own order |
| `set` | `HashSet<T>`, `SortedSet<T>` | 20 rows; a `HashSet<T>` captured whole is sorted so rows don't jump as members are added |
| `heap` | an array or `List<T>` | 31 cells (five levels); pointers are int variables as for arrays. `PriorityQueue<,>` is not drawn: its heap order is internal |
| `grid` | `T[][]`, `List<List<T>>`, `T[,]`, `string[]` | 15 rows and 15 cells a row, with `Lengths` holding the real sizes; `cells`, `pointers`, `rowLabels`, `colLabels` and `highlight` work as in Python, with `(int, int)` tuples or two-item arrays/lists as pairs |

Dictionary and set rows carry an `Id` for the run, assigned in the order keys are first met and found again by value (so an `int` key and the
`long` equal to it are the same row). It never comes from a hash code or an address, so re-recording gives the same ids. `Hits` names the
local variables whose value is a key of the table, as for Python. Frozen collections are captured up to 128 items, so a set larger than that
shows the members met first rather than sorted ones.

## Linked lists and trees: node identity

A node is an instance of one of the learner's own classes (`FullName` not under `System.` or `Microsoft.`) that has the `next` field or
property (a list), or both `left` and `right` (a tree). `ProbeBudget` is built with `trackObjects: true` by the recorder, and then:

- every instance of a learner class gets an **identity**: a number for the whole run, handed out in the order objects finish being captured
  (`CapturedObject.Identity`, assigned after the object's own members, so a node comes after the nodes it points to). It is the same at every probe whichever variable holds the object, and never comes from an address or a hash code,
  so re-recording gives the same ids. A node's id in `ListNode`/`TreeNode` is this number.
- an object reached again on the way to itself (a circular or doubly linked list, a parent pointer) is captured as a `CapturedReference` to that
  identity instead of the old `<cycle>` placeholder; `CSharpTraceViews` follows it through an index of the snapshot's objects. Locals still read `<cycle>`.
- the capture goes 256 levels deep and 4000 objects (16 and 1000 otherwise), because a list of nodes is as deep as it is long.

Members are found by name among the object's fields and auto-property backing fields, exactly first and then ignoring case, so the defaults `next`,
`val`, `left` and `right` find C#'s `Next`, `Val`, `Left` and `Right`; set `next:`/`value:`/`left:`/`right:` for any other name. Properties with
their own getter are not read (capturing never runs learner code). Walks follow the Python tracer: 30 list nodes, 31 tree nodes (breadth first),
`More` counts the nodes beyond them, and `pointers` and a tree's `visited` list or set work the same way. Two C#-only limits: `More` can only count
what was captured, so a structure cut by the capture limit adds 1 for the cut; and a watched variable is drawn only in the frames where it is in
scope (a local function of top-level code sees it, a static method does not; pass the node as a parameter and watch that name as a pointer).

## Graphs

Issue [#196](https://github.com/joeizang/brilliantApp/issues/196), last part. A graph is a **non-empty `Dictionary<,>`** (or `SortedDictionary<,>`) from
each node to its neighbours, which the Python tracer draws from a dict the same way. `CSharpTraceViews.Graph` follows its rules and limits:

- a neighbour collection is a sequence (`List<T>`, an array, a `HashSet<T>`, sorted so the order is stable), or a nested dictionary whose values are the
  edge weights. With `weights: true` a member that is a two-item array/list or a `(node, weight)` tuple is read as the neighbour and its weight.
- a node is a key, then each neighbour not yet met, walked in the dictionary's order. A node that is an instance of the learner's own class keeps its
  run-wide identity (see above) as its id, so the same object is the same node at every step; any other node gets an id from the order it was met.
  A label is a string as it is, or the clipped repr of the value.
- 20 nodes and 60 edges; `More` and `MoreEdges` count what was cut. A `Pointers` name is kept only when it names a node that was kept.
- `visited` takes a sequence or a table (its keys) and `frontier` a sequence; both become `GraphMarks` of node ids, with the entries that match no node
  counted in `Unmatched`. `directed`, `weights`, `edge` and `max` are passed through for the player.

A watched variable that is never a non-empty dictionary is reported by `validate` as "never a non-empty Dictionary<,>".

## The call stack

A `call_stack` visual (it names no variable) records the calls the code makes: **methods and local functions** that have a body. Lambdas, constructors,
accessors and `ref`-returning methods are not tracked, and `functions: [Name]` narrows the stack to those names. When the visual is present
`ProbeRewriter` wraps each tracked body (`Enter`/`ExitCall` around it, a `try`/`finally` so a throw pops the call) and turns every `return` into a
probe, and `ProbeBudget` hands each snapshot the calls that were open at that moment. Without a `call_stack` none of this is inserted, so every other
recording is byte-identical to before.

| `TrackedCalls` | Recorded as |
| --- | --- |
| `Frames` | the open calls, outermost first, each a `RecordedCall` with a run-wide `Id` (0, 1, 2 … in call order, never an address), the function name and its parameters as `Args` (reprs clipped to 24 characters; `out` and ref-like parameters left out) |
| `Returning` | on the innermost call only, in the frame of its return: the clipped repr of the value, or `void` |
| `Returned` | on the frame *after* a non-void return only: the function, the value and the `Id` of the caller it went back to (`null` for the outermost) |
| `More` | the number of outer calls cut off. Only the 12 innermost calls are kept |

A return is a step of its own (`Event` `"return"` with the line of the `return`), but only while a stack is being watched. A void method returns on the line
of its closing brace, with the parameters and the top-level locals it had assigned. Known quirks: `if (n <= 1) return 1;` gives two steps on one line
(a probe before the `if`, a probe before the `return`), and a thrown exception ends the call with no return step.

## The stack and the heap

A `memory` visual (no variable) is for the recorded C# trace's stack-and-heap view, see [trace-player.md](trace-player.md#the-stack-and-the-heap-c).
`TraceFrame.Memory` is a `TrackedMemory`: `Frames` (the open calls, outermost first, each a `MemoryFrame` with an `Id`, the function and its
`Slots`), `Objects` (each a `MemoryObject` with an `Id`, its type, a `Kind` of `class`, `box` or `array`, its field `Slots` and the number cut
off in `More`) and `MoreFrames`. A `MemorySlot` is a name and a type with one of: a `Value` (a primitive, string or enum), a `Ref` (the id of
an object, never an address) or `Fields` (a struct, written out so a copy is a second set of values).

### How it is recorded

With a `memory` visual in the step, the recorder turns on three things that other recordings never see (they stay byte-identical, and
their golden files did not change):

- **Every method and local function is a tracked call**, as with `call_stack` (an `Enter`/`ExitCall` pair and a probe at each return). A
  `memory` view therefore adds the return steps a `call_stack` does. A `call_stack` visual next to it still lists only its own `functions`.
- **Value-typed locals and parameters are marked** (`ProbeRewriter` `markValues`: the probe takes `new InlineValue(x)`). A local reaches a probe
  boxed, so without the mark a `Point` held in a variable and a `Point` boxed into an `object` look the same. The static type decides:
  a value-typed variable, field or array element holds its value inline; the same value in a reference-typed place (`object o = 5`) is a box
  on the heap. A struct copy is therefore a second set of `Fields`, and a box is a new object with its own id. A variable whose type is a type
  parameter (`T best`) is decided at run time (`InlineValue.Mark<T>`): inline when the method is called with a value type, a reference otherwise.
- **A frame lists its locals in the order the code declares them** (parameters first), which the rewriter sorts for the memory view only.
- **`ProbeBudget` follows the stack** (`MemoryCapturer`): each tracked call keeps the locals its last probe saw, so a caller waiting on a
  call still shows what it held when it called. The code outside any method is the frame `Main` (id -1). The heap is read at the probe, so it is
  always current.

What is drawn, and where it stops:

- Strings, primitives, enums, `decimal`, dates and `Guid` are inline values (a string is not drawn as a heap object). A user struct, a tuple or a
  `KeyValuePair` is inline `Fields`, nested up to four levels.
- Class instances show their fields (a property's backing field by the property's name); arrays and `List<T>`, `Stack<T>`, `Queue<T>`,
  `HashSet<T>` and `SortedSet<T>` are `array` objects with `[0]`, `[1]`… slots. Other framework objects (a `Dictionary`, a `StringBuilder`) are shown by
  their type name with no slots. A multi-dimensional array shows only its length in `More`.
- A local that holds `null` shows the type `object` (the probe knows the value, not the declared type); the type is only drawn for structs, so this
  shows only in the accessible label.
- Limits: 12 frames (the innermost; `MoreFrames` counts the rest), 24 locals per frame, 24 objects and 16 slots per object per step (`More` counts the
  rest; a reference past the object limit reads `<limit>`). An object only a cut-off frame reached is dropped.
- Object ids are numbered in the order objects are first seen in the run (never from an address) and offset by one, so an object keeps its id at
  every step. A constructor, lambda or accessor is not a frame of its own: while one runs, the stack shows the frames as they were.

A memory view over code with no local and no object still fails `validate` with "nothing to draw".

