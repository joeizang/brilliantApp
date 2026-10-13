# Trace Player: arrays, stacks, queues, dicts, sets, linked lists, binary trees, grids and the call stack, stepping and playback

PRD stories 24, 25 and 27 ([#23](https://github.com/joeizang/brilliantApp/issues/23)) and 26, 28, 29 and 65 ([#24](https://github.com/joeizang/brilliantApp/issues/24)).
A *trace* step runs a small example once, records every line it executes, and lets the learner walk through it: the code with the current
line highlighted, the watched lists drawn as cells with named pointers that slide as they move, the watched dicts and sets drawn as
tables whose rows appear, flash and fade, linked lists drawn as nodes joined by arrows that redraw as they are rewired, binary trees drawn level by level with the nodes a walk has visited numbered, 2-D tables (a DP table, a board) with row and column pointers, the call stack with each value handed back to its caller, and the local variables with what just changed marked. It steps by button or arrow key and
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
    - variable: nums        # a list, tuple or deque, drawn as cells
      as: array
      pointers: [i]         # int variables drawn as labelled markers on the cell they index
    - variable: counts      # a dict (or dict subclass), drawn as key/value rows
      as: dict
      pointers: [word]      # variables holding a key: the row they look up is marked
    - variable: seen        # a set or frozenset, drawn as a run of members
      as: set
```

`as` is `array`, `stack`, `queue`, `dict`, `set`, `linked_list`, `tree`, `grid` or `call_stack`. A pointer on a dict or set is any variable, of any type, whose value is a key: the row with that key
is marked with the pointer's name, and when no row has that key the player says *Looking up `word` finds no such key.*

`body` and `code` are required, and there must be at least one visual. A trace step takes no `hints` and can't be a review item.
The validator rejects, with the file and step in the message:

- an unknown `as`, a missing `variable`, a name that isn't a Python identifier, the same variable drawn twice, or a pointer listed twice;
- code that raises (the message has the line and the traceback) or runs past the recorder's limit;
- code with no executable line (empty or comment-only code), which would give the learner nothing to step through;
- a `variable` that is never the kind of value its `as` draws in the trace (an array: a list, tuple or deque; a stack or queue: a list or
  deque; a dict; a set), a pointer on an array, stack or queue that is never an `int`, and a pointer on a dict or set that is never a
  variable at all. Each would draw nothing.
- on a linked list: `next` or `value` on any other kind, or not a Python attribute name; the list's own variable listed as a pointer (it is
  already drawn on the first node); a `variable` that never holds a node; nodes that have no `value` attribute to show (the message says to
  set `value`); and a pointer that never holds a node of the list.
- on a tree: `left` or `right` on any other kind, not attribute names, or both the same attribute (`value` is an option of a linked list
  or a tree only); the tree's own variable listed as a pointer or as `visited`; `visited` on any other kind, or not an identifier; a
  `variable` that never holds a node; nodes with no `value` attribute; a pointer that never holds a node of the tree; and a `visited`
  variable that is never a list or set, or never marks a node.
- on a grid: `cells`, `rowLabels`, `colLabels`, `labelOffset` or `highlight` on any other kind; a `cells` entry that isn't a pair of
  identifiers, is listed twice or names the grid's own variable; the grid's own variable as a pointer, a label or the highlight; a
  negative `labelOffset`, or one given without labels; a `variable` that is never a non-empty list of lists or strings; a pair or pointer
  that never lands on a cell; a label variable that is never a string, list or tuple; and a highlight that is never recorded or never
  marks a cell.
- on the call stack: a `variable` or `pointers` (it has neither), a second `call_stack`, `functions` on any other kind, a function name
  that isn't an identifier or is listed twice, code that never calls a function of its own (or none of those listed), and a listed
  function that is never called.

### Stacks and queues

```yaml
- id: step.stacks.brackets
  type: trace
  title: Matching brackets
  body: |
    Each opener is pushed; each closer pops the opener it must match.
  code: |
    from collections import deque
    stack = []
    todo = deque()
    for ch in "([])":
        if ch in "([":
            stack.append(ch)
            todo.append(ch)
        else:
            stack.pop()
            todo.popleft()
    top = len(stack) - 1
  visualise:
    - variable: stack       # a list or a deque, drawn bottom (index 0) to top
      as: stack
      pointers: [top]       # int variables, on the cell they index, as on an array
    - variable: todo        # a list or a deque, drawn front (index 0) to back
      as: queue
```

- `as: stack` stands the cells **bottom to top** (index 0 at the bottom) with a *top* marker on the last one. A push grows in at the top;
  a pop rises away from it, and the player says *'(' popped off the top.* under it for that step.
- `as: queue` runs the cells **left to right**, front (index 0) to back, with *front* and *back* markers (*front · back* for one cell).
  `append` grows in at the back and `appendleft` at the front; `popleft` (or `pop(0)` on a list) leaves from the front, and the cells
  behind it slide up; `pop` on a deque leaves from the back. The note under it says which (*1 dequeued from the front.*).
- Both take a `list` or a `collections.deque`; an `array` takes a deque too. A stack's or queue's `pointers` are int variables, exactly as
  an array's. They are the only option: like every key of a step, an unknown one (say `top: right`) is rejected.

### Linked lists

```yaml
- id: step.links.reverse
  type: trace
  title: Reversing a linked list
  body: |
    Watch each node's arrow turn round. `prev` is the list reversed so far; `cur` is what is left to do.
  code: |
    class Node:
        def __init__(self, val, next=None):
            self.val = val
            self.next = next

    head = Node(1, Node(2, Node(3)))
    prev = None
    cur = head
    while cur:
        nxt = cur.next
        cur.next = prev
        prev = cur
        cur = nxt
    head = prev
  visualise:
    - variable: head        # holds the first node, or None
      as: linked_list
      next: next            # the attribute that links a node to the next (the default)
      value: val            # the attribute shown in the node (the default)
      pointers: [prev, cur, nxt]
```

- A **node** is any object with the `next` attribute (`next` and `value` default to `next` and `val`; a `@dataclass` with `link` and
  `data` takes `next: link` and `value: data`). The list ends where a link is `None`, or anything that isn't a node.
- Each node is a value box and a link box with an arrow to the next node, the last arrow pointing at **None**. The list's variable is drawn
  over the node it holds, like a pointer; `pointers` are variables that hold a node of the list, each named over its node. A pointer that
  holds `None` stands over a *None* (the end of the list, or a *None* of its own when the list doesn't end in one).
- Nodes only a pointer reaches (in the example, the reversed part, between `cur.next = prev` and `head = prev`) are **detached**: they are
  drawn on a row of their own below, dashed, with a note. A detached run that links back into the list ends in an arrow up to that node.
- A **cycle** is an arrow that runs under the row back to the node it loops to; nothing is drawn twice.
- A node is the same element for as long as it is the same object, so when the links are rewired it slides to its new place; an arrow
  that was rewired draws itself in, in the accent colour, and a value that changed flashes. A new node grows in.
- At most 30 nodes are drawn; the rest of the list is *+N more*. A pointer on a node past them is named under the list.

### Binary trees

```yaml
- id: step.trees.inorder
  type: trace
  title: Walking a tree in order
  body: |
    Left subtree, the node, right subtree. Watch the numbers: that is the order the walk visits them in.
  code: |
    class Node:
        def __init__(self, val, left=None, right=None):
            self.val = val
            self.left = left
            self.right = right

    root = Node(4, Node(2, Node(1), Node(3)), Node(6, Node(5), Node(7)))
    order = []
    stack, node = [], root
    while stack or node:
        while node:
            stack.append(node)
            node = node.left
        node = stack.pop()
        order.append(node.val)
        node = node.right
  visualise:
    - variable: root        # holds the root node, or None
      as: tree
      left: left            # the attribute holding the left child (the default)
      right: right          # the attribute holding the right child (the default)
      value: val            # the attribute shown in the node (the default)
      pointers: [node]      # variables that hold a node of the tree, named under it
      visited: order        # a list or set: the nodes it holds (or holds the values of) are shaded
```

- A **node** is any object with both the `left` and `right` attributes (a `SimpleNamespace` or a `@dataclass` will do). A child that is
  `None`, or anything that isn't a node, is not drawn.
- The tree is laid out in layers, one row per depth, and in order across: each node has a column of its own, so a left child is always
  left of its parent and a right child right of it, and the drawing is the same for the same tree every time. The tree's variable and
  `pointers` are named under the node they hold; one holding `None` is listed in a note (*node is None.*).
- `visited` marks nodes by identity (the list holds the nodes) or else by value (it holds their values). A **list** (or tuple or deque)
  numbers them in its order, a badge on each node; a **set** shades them without numbers. A node newly visited pops its badge. Members that
  match no node shown are counted in a note.
- Nodes only a pointer reaches are **detached**: drawn dashed, as a tree of their own to the right. A link to a node already drawn (two
  parents sharing a child, or a cycle) is a dashed bow to that node; nothing is drawn twice.
- A node is the same element for as long as it is the same object, so it slides when the tree changes shape; an inserted node grows in,
  its edge draws itself in, and a value that changed flashes.
- At most 31 nodes are drawn (five full levels), taken level by level; the rest are *+N more*, and a link to one of them is a short dashed
  stub. A long tree scrolls sideways.

### Grids

```yaml
- id: step.dp.lcs
  type: trace
  title: Filling the LCS table
  body: |
    Each cell is the answer for a prefix of `a` and a prefix of `b`. Watch `(i, j)` fill the table row by row.
  code: |
    a, b = "abc", "ac"
    dp = [[0] * (len(b) + 1) for _ in range(len(a) + 1)]
    for i in range(1, len(a) + 1):
        for j in range(1, len(b) + 1):
            if a[i - 1] == b[j - 1]:
                dp[i][j] = dp[i - 1][j - 1] + 1
            else:
                dp[i][j] = max(dp[i - 1][j], dp[i][j - 1])
  visualise:
    - variable: dp          # a list (or tuple) of lists, tuples or strings
      as: grid
      cells: [[i, j]]       # pairs of int variables, row then column, drawn on the cell dp[i][j]
      rowLabels: a          # a string, list or tuple labelling the rows
      colLabels: b          # ... and the columns
      labelOffset: 1        # the first label goes on row / column 1 (dp[i][j] is over a[i - 1], b[j - 1])
```

Two more options: `pointers` names variables holding a `(row, col)` pair (`cell = (r, c)` on a board), and `highlight` names a list or
set of `(row, col)` pairs, or a grid of `True`/`False` the same shape (a flood fill's `seen`), whose cells are shaded. The options are
camelCase, as every other option is (the issue wrote them snake_case).

- A **grid** is a list or tuple whose rows are lists, tuples or strings (a string row's cells are its characters, shown as they are, so
  a board `["#.#", "..."]` reads as one). Rows can differ in length. An empty list is an empty grid, a dashed box.
- The cells sit edge to edge, row 0 on top, with the column indexes above and the row indexes to the left; labels, when drawn, stand
  outside the indexes, lined up from `labelOffset`.
- A **pointer** (a `cells` pair, named `i,j`, or a variable holding a pair) has a lane of its own on both edges: its column variable's
  name over the column with an arrow down, its row variable's left of the row with an arrow across, and a ring around the cell. Markers
  and ring slide as it moves. A pointer off the grid is noted with where it is (*i,j is at (4, 0), off the grid.*); one at `None`, or a
  pair that isn't two ints, is noted too.
- A changed cell flashes; a cell or row that came in (an append) grows in. The highlight's cells are filled with the accent tint, and its
  members that mark no cell shown are counted in a note.
- At most 15 rows and 15 cells of each are drawn; a longer row ends in *+N*, and the rows cut off are *+N more rows not shown.* A wide
  grid scrolls sideways.

### The call stack

```yaml
- id: step.recursion.factorial
  type: trace
  title: Calls waiting on calls
  body: |
    Each call of `fact` waits for the one it made. Watch the values come back down the stack.
  code: |
    def fact(n):
        if n <= 1:
            return 1
        return n * fact(n - 1)

    print(fact(3))
  visualise:
    - as: call_stack        # no variable: it draws the calls under way
      functions: [fact]     # optional: only these functions' calls (the default is every function of the example's own)
```

- One **frame** per call under way, the module at the bottom and the running call on top (marked *running*), each shown as it was
  called: `fact(n=3)`, with each argument's repr. `self` and `cls` are left out. Only the example's own functions count: no calls into
  Python's library, and no class bodies, comprehensions or generators.
- While the call stack is watched, a function **returning** is a step of its own: the line it returns from, with *returns 6* beside
  its frame. On the next step the frame rises away (a dashed ghost) and the value pops up beside the frame it went back to, *← 6*, or
  beside the module. A note under the drawing says *fact(n=1) returned 1 to fact(n=2).*; a call ended by an exception *ended without
  returning*.
- A frame keeps its place for as long as the call lasts, so the frames under a new call never move.
- At most 12 frames are drawn, the innermost; the rest are counted where the module would be, *+N more calls below*.
- It is drawn above the other visuals and works alongside them. Locals are still compared only within one function; the call stack is
  compared by depth (which calls are new, which have gone).



`trace_code(code, watch_json)` runs the code with `sys.settrace` and returns JSON. It is plain stdlib Python so the CLI runs the same file
under CPython. Each frame is the state *just before* a line runs (`event: "line"`), or the state the module finished in (`event: "return"`,
no line). A frame holds the function name, the locals (name, type, clipped repr) and, per watched variable, either `tracked` (a list, tuple
or `collections.deque`: `type` and the first cells' reprs, front to back; a deque can't be sliced, so they are taken with `islice`), `tables` (a dict or set: `kind`, the Python `type` name, the first rows as `{id, key, value}` and `hits`) or `lists` (a linked list, below).
The watch list names the variables: a plain name for an array, stack, queue, dict or set, and for a linked list an object,
`{"variable", "as": "linked_list", "next", "value", "pointers"}`.

With `{"as": "call_stack", "functions": [...]}` in the watch list, every frame also has `calls`: `{frames, more, returned}`. `frames` are
the calls under way, outermost first, as `{id, function, args: [{name, repr}], returning}`; `id` grows with every call made, so a call
keeps its id for as long as it lasts and a new one always has a bigger one. Only a code object with `CO_OPTIMIZED` that isn't a generator,
coroutine or async generator, and isn't a comprehension, counts as a call (a class body is not optimised); with `functions`, only those.
Each function's return is recorded as a frame of its own (`event: "return"` with the line) whose top call has `returning` set to the
value's repr; the step after has `returned: {function, value, to}`, `to` being the id of the call it went back to (null for the module or
a call not shown). A call left by an exception gets no return frame. Python reports a `return` event (value None) for it too, so the
recorder looks at the instruction the frame stopped on: a real return is on `RETURN_VALUE` (or `RETURN_CONST`), an exception leaving is on
whatever raised or re-raised. A `line` event is no sign the exception was caught, since a `finally` runs lines while it passes through;
a `return` inside that `finally` does replace the exception, and is recorded as a return. At most 12 calls are kept, the innermost, and `more` counts the rest.

A tree is recorded as `{root, nodes, more, pointers, visited}`, with the watch entry `{"variable", "as": "tree", "left", "right", "value",
"pointers", "visited"}`. Node ids work as a linked list's. `nodes` are `{id, value, left, right}`, walked breadth first from the root and
then from each pointer, skipping any node already met, so a shared child or a cycle is recorded once. At most 31 are kept and `more`
counts the rest (walking no further than 1000). `visited` is null unless the named variable is a list, tuple, deque, set or frozenset;
then it is `{ordered, nodes: [{id, order}], unmatched}`. Each of its first 100 members marks the node it *is*, or failing that, nodes
whose value equals it (an `__eq__` that raises is no match); in a list a member marks only the first such node not yet marked, so
`[1, 1]` numbers two nodes holding 1. `order` is the member's place in the list (null for a set), and `unmatched` counts the members that
mark nothing, plus those past the first 100.

A grid is recorded as `{rows, lengths, more, pointers, rowLabels, colLabels, highlight}`, with the watch entry `{"variable", "as": "grid",
"pointers", "cells": [["i", "j"]], "rowLabels", "colLabels", "highlight"}`. It is recorded while the variable is a list or tuple whose
first 15 items are lists, tuples or strings. `rows` are the first 15 rows' first 15 cells (reprs, or a string's characters), `lengths`
each recorded row's full length and `more` the rows cut off. `pointers` maps a pair (`"i,j"`) or a pointer to `[row, col]` as it is, so
off the grid too; to null when a pair's variable isn't an int (a bool doesn't count) or the pointer holds None; a pair with a variable not
defined, or a pointer holding anything else, is left out. The labels are a string's characters or a list's or tuple's items (a string as
it is, anything else its repr), the first 15, or null. `highlight` is `{cells, unmatched}` when the variable is a list, tuple or set of
pairs (each of the first 100 members marks its recorded cell; the rest, and members that aren't pairs or mark no recorded cell, are
`unmatched`) or a grid of bools (each True marks its cell), and null otherwise.

A linked list is recorded as `{head, nodes, more, pointers}`. Every node gets an id the first time it is met and keeps it for the whole run
(the recorder holds on to the object, so its id can't be reused by a new one); `head` and each pointer are a node id or null for None. `nodes` are
`{id, value, next}`: the value's repr (null when the node has no such attribute) and the id of the node its link holds (null for None or
anything that isn't a node). They are the nodes reached from the head first, in link order, then those reached from each pointer that
weren't, stopping at any node already listed, so a cycle is walked once. At most 30 nodes are kept; `more` counts the rest, walking no further
than 1000 nodes. A pointer whose variable doesn't exist yet, or holds something that isn't a node, is left out.

The learner's code runs as a module named `learner` (registered in `sys.modules` while it runs and removed after), which `@dataclass` needs.
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
- `CellView.Changed`: the cell is new or its text differs. (On a stack or queue, see below.)
- **Stacks and queues** (`ArrayView.Kind` is `stack` or `queue`): comparing by index would say every cell of a queue changed after a
  `popleft`, so the model lines the two steps' cells up instead. It finds the longest run of cells the two share; what is before that run
  left or came in at the front, what is after it at the back. When runs tie (repeated values: `[1, 1] → [1]`), it takes the one where the
  change happens where that kind of sequence changes: a stack at the top, a queue in at the back and out at the front. A cell that left and
  one that came in at the same place pair up as a change in place (`s[-1] = x` flashes, it is not a pop and a push).
  - `CellView.Id` stays the same for as long as the cell stays in, so the player keys the cell by it and slides it to its new place.
    A cell that came in gets an id no cell of the step before had. (An array cell's id is its index.)
  - `CellView.Added`: the cell came in at one end since the step before (a push, `append`, `appendleft`).
  - `ArrayView.Gone`: the cells that left since the step before, each with its id, text and `AtFront`. They are ghosts for that one step.
  - When the sequence is cut off (*+N more*), the cells past the cut slide into view as the front leaves: that is not an enqueue, so
    nothing at the back is marked added or gone.
  - A stack or queue is drawn only while the variable is a `list` or `deque` (a tuple named as `stack` draws nothing).
- `RowView.Id`: what a row is keyed by (and what the Razor `@key` uses), taken from the recorder's `id`, never from the clipped label. In a
  trace recorded before ids existed it falls back to the label.
- `RowView.Change` (dicts and sets): `Added` when the id is new, `Updated` when its value differs (dicts only), `Removed` for a key the
  previous step had and this one does not (matched by id, so two keys with the same label are not confused). A removed row is kept as a ghost for that one step so the learner sees it go, and is never
  compared again. The first step marks nothing, and neither does the first step in a new function.
- `RowView.LookedUpBy`: the pointer variables the recorder reported as hitting that row's id. `TableView.Misses` are the pointers that
  exist but hit nothing at all (a hit on a row cut off by the 20-row limit is neither a lookup nor a miss, since the key may well be there).
  The model never compares reprs; a trace without `hits` marks no lookups and reports no misses.
- A table is drawn only when the recorded value really is the kind the visual asks for (a list named as `dict` draws nothing).
- **Linked lists** (`LinkedListView`): the nodes as `Chains`, rows of `NodeView`s. The first row follows the links from the head; each
  other starts at the first node no row has taken (a detached run). A row's `End` is `None`, `Back` (a cycle: `Target` is the node in that
  row it links to), `Join` (it links into an earlier row's `Target`) or `More` (a node that wasn't recorded). Nodes are compared by id, so
  `Changed` is a node whose value differs, `Relinked` one whose link now holds a different node, and `Added` a node the step before didn't
  have. `Pointers` are the variable itself (on the head) and then the recorded pointers, each `Moved` when it holds a different node (or
  None) than before.
- **Trees** (`TreeView`): `Nodes` are `TreeNodeView`s with their place, `Column` (an in-order count across the drawing) and `Depth`,
  and `Tree`, which tree they belong to (0 is the one from the root and only that one: when the root is None, a subtree a pointer holds
  is still detached, numbered from 1). The layout claims nodes by a
  breadth-first walk from the root, then from the first recorded node not yet claimed; a link is `Child` (drawn as an edge), `Elsewhere`
  (a node already claimed: a shared child or a cycle), `More` (a node not recorded) or `None`. Compared by id with the step before:
  `Added` a node it didn't have, `Changed` one whose value differs, and `JustVisited` one newly marked or renumbered. `Pointers` are the
  variable itself (on the root) and the recorded pointers, each `Moved` when it holds a different node.
- **Grids** (`GridView`): `Rows` of `GridCellView`s, each with its row and column, `More` the cells cut off a row and the view's `More`
  the rows cut off. Compared by place with the step before: `Changed` a cell whose text differs, `Added` a cell the step before didn't
  have; `Shaded` a cell the highlight marks. `Pointers` lists every pointer of the visual, pairs first, at every step (`Defined` says
  whether it was recorded), so each keeps its lane; `OnGrid` is a pointer on a cell drawn, `Moved` one whose place differs. The labels are
  lined up to the rows and columns by `labelOffset` (null where there is none).
- **The call stack** (`CallStackView`): `Frames` are the calls under way as `CallFrameView`s (`Label` is `fact(n=3)`). A frame is
  `Pushed` when its id is bigger than any the step before had, and `Received` is the value handed back to it since the step before (by
  `returned.to`). `Gone` are the calls the step before had that this one hasn't (ghosts for one step; when the stack is cut off, only
  those above the bottom one shown), and `ToModule` is a value handed back to the module or to a call not drawn.
- `PointerView.Index`: the cell the pointer's variable indexes (on an array, stack or queue alike). Null when the variable is missing, isn't an `int`, or is outside the
  recorded cells (negative, or past the end). `Moved` is true when the index differs from the previous state's.

## The player

- `TracePlayer.razor`: a read-only `CodeEditor` (new `ReadOnly` parameter) with the current line highlighted through the existing
  `HighlightLineAsync`; Previous, Play/Pause and Next; *Step n of m*; the speed buttons; one `TraceSequence` per watched stack or queue; one SVG per watched array; one `TraceTable` per
  watched dict or set (after the lists, whatever order the lesson names them in); one `TraceLinkedList` per watched linked list; one `TraceTree` per watched tree; one `TraceGrid` per watched grid; a `TraceCallStack` above them all when the call stack is watched; a *Variables* table; and, on the last step only,
  what the run printed, the error that ended it, or a note that it was cut short (`TraceEnding.razor`).
- **Animation is CSS.** A pointer is a `<g>` positioned with `style="transform: translate(…)"` and keyed by variable and name, so Blazor
  patches the same element and the browser transitions it from the old cell to the new one. A changed cell is keyed by step too, so it is
  a fresh element that replays its flash on every step it changes in. Changed rows of the variables table do the same, and so do the
  added, updated and removed rows of a `TraceTable` (added rows grow in, updated rows flash, removed rows are struck through).
  A stack's or queue's cell is a `<g>` keyed by its `Id` and placed with a CSS transform, so when the front leaves the rest slide up; an
  added cell grows in (`animate-cell-in`, scaling its body from its own centre), and a gone cell is a dashed ghost that rises and fades
  away from the end it left (`animate-cell-out`). The *top*, *front* and *back* markers are positioned the same way, so they slide too.
  `prefers-reduced-motion` turns all of it off (theme.css).
- Pointers sharing a cell are stacked on rows below it so every label stays readable. A pointer that isn't on a cell isn't drawn; the
  player says *i is not on a cell right now* under the list instead.
- Colours are the theme tokens, so it follows light/dark. Everything is in `rem`, and a long list or queue scrolls sideways inside its
  own box; a tall stack scrolls up and down inside its own (at most 28rem high), with its top in view first. A stack's pointers sit to
  the right of its cells, pointing at them.
- A stack or queue is laid out in SVG user units and sized in rem (16 units to 1rem), with its text in the same units, so cells and the
  values in them grow together with the app's text size (a seven-character value is set a little smaller to fit its cell). A stack's
  pointer names on one cell stand in columns as wide as the longest name in them; a queue's names take the first row below the cells
  where they clear their neighbours'. Either way the drawing (and its scroll box) reaches the last name.
- A linked list (`TraceLinkedList.razor`) is drawn the same way: user units sized in rem, each node a `<g>` keyed by its id and placed with
  a CSS transform, the arrow to the next node part of it. A rewired arrow is keyed by step and has `pathLength="1"`, so `animate-link-in`
  draws it from its node to the next; its head pops. Pointer names on one node take rows above it where they clear their neighbours', and a
  name wider than the first node moves the drawing's left edge rather than being cut off. A long list scrolls sideways.
- A tree (`TraceTree.razor`) is an SVG in a sideways scroll box, its sizes in rem. Each node is a `<g>` keyed by its id and placed with a
  CSS transform, so it slides; an added node grows in (`animate-cell-in`), a changed value flashes (`animate-cell-flash`, keyed by step)
  and a new visit badge pops (`animate-pop`). An edge is keyed by its two ends' places and has `pathLength="1"`, so a new or moved edge
  draws itself in (`animate-link-in`). Visited nodes are filled with the accent tint; pointer names stack in rows under their node, and a name wider than the leftmost
  node's column moves the drawing's left edge rather than being cut off. The
  SVG's label reads the tree level by level for a screen reader.
- A grid (`TraceGrid.razor`) is an SVG in a sideways scroll box, sized in rem. Each cell is a `<g>` keyed by its place; a changed one
  flashes (keyed by step) and a new one grows in. Each pointer is three `<g>`s keyed by its name (the ring, the column marker and the row
  marker), placed with CSS transforms, so they slide; nested rings stand inside one another when pointers share a cell. The SVG's label
  reads the grid row by row, with the pointers.
- The call stack (`TraceCallStack.razor`) is a column of frames drawn bottom up, in user units sized in rem, as wide as its longest call
  and returned value, scrolling sideways (and up and down, at most 30rem) on a phone. Each frame is a `<g>` keyed by its id and placed with
  a CSS transform; a new call grows in (`animate-cell-in`), a gone one rises away (`animate-cell-out`), the *running* marker slides to the
  new top, and a value handed back is keyed by step, so it pops (`animate-pop`) every time.
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

## Reused by the full-solution walkthrough

`TraceRun` (code + visuals → runs `IPythonRuntime.TraceAsync`, builds the states, shows the player or the failure) is shared by the trace step and
by the last rung of the hint ladder, see [hints.md](hints.md).

## Not in this slice

Per-variable speed or scrubbing, trees with more than two children (a general tree or a graph), drawing nested structures (a dict of lists, a node whose value is a node), doubly linked
lists (a `prev` arrow), and reading a trace aloud.
`Visual.As` is a string and the model builds one `ArrayView` per `array`, `stack` or `queue` visual, one `TableView` per `dict` or `set`
visual, one `LinkedListView` per `linked_list` visual, one `TreeView` per `tree` visual, one `GridView` per `grid` visual and one `CallStackView` for the `call_stack`, so another kind slots in beside them.

## Tests

`tests/python/test_tracer.py` (recorder), `tests/Brilliant.Core.Tests/TraceTests.cs` (model and JSON),
`tests/js/python-runtime.test.mjs` (bridge), `tests/Brilliant.Cli.Tests/TraceStepValidatorTests.cs` (YAML rules and the CPython check),
`tests/Brilliant.Lessons.UI.Tests/TracePlayerTests.cs`, `TraceStepViewTests.cs` and `WalkthroughRungTests.cs` (rendering, stepping, playback on a manual clock, shortcuts,
line highlight, lesson wiring), `tests/js/shortcuts.test.mjs` (key classification) and `ShortcutTests.cs` (the page listener's mapping).
bUnit can't observe a CSS transition or element reuse, so the sliding, flashing, the ghost row's fade and the stack and queue cells
growing in and rising away, a linked list's nodes sliding and arrows drawing in, a tree's nodes sliding, edges drawing in and visit badges popping, a grid's pointers sliding and cells flashing, and the call stack's frames growing in and values popping up, are checked by looking at the running app.

Known limit: the recorder keeps only the first 20 rows, so a pointer on a later row is shown as nothing (neither marked nor a miss) beside
*+N more*.
