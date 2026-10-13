"""Records an execution trace of a learner's (or an author's) code for the trace player.

Loaded by the Pyodide worker (python-worker.js) and also importable under plain CPython, which is how
tests/python/test_tracer.py and the content validator check it. Only the standard library is used.

trace_code(code, watch_json, max_frames=MAX_FRAMES) -> JSON string:
    {"status": "ok" | "error" | "truncated",
     "frames": [{"event": "line" | "return", "line": int | null, "function": str,
                 "locals": [{"name", "type", "repr"}],
                 "tracked": {variable: {"type": str, "cells": [str], "more": int}},
                 "tables": {variable: {"kind": "dict" | "set", "type": str, "more": int,
                                       "rows": [{"id": int, "key": str, "value": str}],
                                       "hits": {local variable: id}}},
                 "lists": {variable: {"head": id | null, "more": int,
                                      "nodes": [{"id": int, "value": str | null, "next": id | null}],
                                      "pointers": {pointer: id | null}}},
                 "trees": {variable: {"root": id | null, "more": int,
                                      "nodes": [{"id": int, "value": str | null, "left": id | null, "right": id | null}],
                                      "pointers": {pointer: id | null},
                                      "visited": {"ordered": bool, "nodes": [{"id": int, "order": int | null}], "unmatched": int} | null}},
                 "grids": {variable: {"rows": [[str]], "lengths": [int], "more": int,
                                      "pointers": {pointer: [row, col] | null},
                                      "rowLabels": [str] | null, "colLabels": [str] | null,
                                      "highlight": {"cells": [[row, col]], "unmatched": int} | null}},
                 "graphs": {variable: {"nodes": [{"id": int, "label": str}], "more": int,
                                       "edges": [{"from": id, "to": id, "weight": str | null}], "moreEdges": int,
                                       "pointers": {pointer: id | null},
                                       "visited": {"nodes": [id], "unmatched": int} | null,
                                       "frontier": {"nodes": [id], "unmatched": int} | null}},
                 "calls": {"frames": [{"id": int, "function": str, "args": [{"name", "repr"}], "returning": str | null}],
                           "more": int, "returned": {"function": str, "value": str, "to": id | null} | null}}],
     "stdout": str, "message": str | null, "traceback": str | null, "errorLine": int | null}

A "line" frame is the state just before that line runs. The last frame is the module's "return": the final
state, with no line. "error" means the code raised (frames up to the error are kept) or never compiled;
"truncated" means it ran past max_frames and was stopped. `watch_json` is the list of variable names to record,
e.g. ["nums", "counts"]; a name is looked up in the current frame's locals, then in the globals. A list, tuple or
collections.deque is recorded in "tracked" (cells, front to back; its "type" says which); a dict (or subclass) or a set/frozenset in "tables" (rows: a set's rows have no
"value"). A name that is none of those is left out until it is. A set's rows are sorted, so its order doesn't jump
around as members are added; a dict's are in insertion order.

A watch entry can also be an object, for a linked list: {"variable": "head", "as": "linked_list", "next": "next",
"value": "val", "pointers": ["prev", "curr"]}. A node is any object with the "next" attribute; the variable holds the
first node (or None, an empty list). The list is recorded in "lists" while the variable holds a node or None. Its nodes
are the ones reached from the head by following "next", then those reached from each pointer that holds a node the
head doesn't reach (a detached node), in that order, at most MAX_NODES of them ("more" counts the rest, up to
MAX_WALK). A node's "id" is its identity, the same at every step for as long as the run lasts, so a node rewired keeps
its id. Its "next" is the id of the node it links to, or null at the end; a "next" that links back to a node already
recorded is a cycle, which is not followed again. Its "value" is the repr of the value attribute, null when the node
has none. "pointers" maps each pointer that holds a node to its id, or to null when it is None; a pointer holding
anything else (or not defined) is left out.

A watch entry {"variable": "root", "as": "tree", "left": "left", "right": "right", "value": "val", "pointers": ["node"],
"visited": "order"} records a binary tree in "trees" while the variable holds a node or None. A node is any object with both the
"left" and the "right" attribute. Its nodes are taken level by level from the root, then from each pointer that holds a node the root
doesn't reach (a detached subtree), at most MAX_TREE_NODES ("more" counts the rest, up to MAX_WALK); a node met again (shared, or a
cycle) is not followed again. Ids are by identity, as for a linked list; a child that isn't a node is null. "visited" names a list,
tuple, deque or set that a traversal fills: a member that is a node of the tree marks that node, and any other member marks a node
whose value equals it. In a list (ordered) each member marks the first node, level by level, it matches that no earlier member has,
and "order" is its place in the list, from 1; in a set every node it matches is marked and "order" is null. "unmatched" counts the
members that mark no recorded node; only the first MAX_VISITED members are matched, and the rest count as unmatched. "visited" is null when the variable is not given, or is not one of those.

A watch entry {"variable": "dp", "as": "grid", "cells": [["i", "j"]], "pointers": ["cell"], "rowLabels": "a", "colLabels": "b",
"highlight": "seen"} records a 2-D table in "grids" while the variable holds a list or tuple whose rows are lists, tuples or strings (a
string's cells are its characters, shown as they are). At most MAX_GRID rows are recorded, and MAX_GRID cells of each; "lengths" are the
recorded rows' full lengths and "more" counts the rows cut off. "pointers" maps a pair of int variables (named "i,j") or a variable holding
a (row, col) pair of ints to that pair, or to null when a variable of the pair isn't an int or the pointer holds None; a pointer whose
variable isn't defined, or that holds anything else, is left out. The pair is recorded as it is, so it can be off the grid. A label
variable that is a string gives its characters, a list or tuple its items (a string as it is, anything else as its repr), the first
MAX_GRID of them; null when it is neither. "highlight" names a list, tuple or set of (row, col) pairs, or a grid of True/False flags the
same shape as the table: "cells" are the recorded cells it marks, sorted, and "unmatched" counts the pairs that mark no recorded cell (or
aren't pairs at all); only the first MAX_VISITED members are looked at, and the rest count as unmatched. It is null when the variable is
not given, or is none of those.

A watch entry {"variable": "graph", "as": "graph", "weights": false, "pointers": ["node", "nb"], "visited": "seen", "frontier": "queue"}
records a graph in "graphs" while the variable holds a dict (a defaultdict too) mapping each node to its neighbours: a list, tuple, deque,
set or dict of them (a set's in sorted order). A dict of neighbours is weighted already, its values the weights. With "weights", a
neighbour given as a (node, weight) pair is that node with that weight; without it, a pair is a node itself (a grid square, say). Nodes are told apart by value, as dict
keys are, and a node's "id" is the same at every step of the run. The nodes are the keys, in order, then the neighbours that aren't keys,
in the order met, at most MAX_GRAPH_NODES ("more" counts the rest; at most MAX_WALK keys and neighbours are looked at). A node's "label"
is a string as it is, anything else its repr. "edges" go from a key to each of its neighbours, both recorded, at most MAX_GRAPH_EDGES of
them; "moreEdges" counts the rest. "pointers" maps each pointer that holds a node of the graph to its id, or to null when it is None;
one holding anything else (or not defined) is left out. "visited" names a list, tuple, deque, set or dict (its keys) and "frontier" a
list, tuple or deque (a queue or a stack): "nodes" are the recorded nodes their members are, in the order met, and "unmatched" counts
the members that are none (only the first MAX_VISITED are looked at; the rest count as unmatched). Each is null when its variable is
not given, or is none of those.

A watch entry {"as": "call_stack", "functions": ["fact"]} records the active calls in "calls" at every frame (and only
when it is watched, the frames below grow by one per return). Its "frames" are the calls of functions defined in the learner's code
that haven't returned yet, outermost first, so the last is the one running; "functions", when given, keeps only the calls
of those functions. A call's "id" is the same at every step it is on the stack and never reused within the run; its "args"
are the arguments' reprs as they were when it was called (a method's "self" or "cls" is left out). At most MAX_CALLS are
recorded, the innermost ones; "more" counts the outer calls cut off. Class bodies, comprehensions and generators are not calls here.
While the call stack is watched, a call's return is also a frame of its own: event "return", the line it returns from,
its locals, and that call's "returning" the repr of the value it returns (a call left by an exception, through a finally or not, has no return frame).
The next frame after it has "returned": which function returned what, and "to", the id of the call it returned to (null
when that is the module, or a call that isn't recorded).

A row's "key" is only its label: the repr, clipped, so two different keys can read the same. Its "id" is what tells
rows apart, and the same key has the same id at every step of the run. "hits" says which of the frame's variables hold
a key (or member) of the table, by Python's own equality: `1.0` finds the key `1`, `True` too, whatever their reprs say.
A hit's id is the id of the row it found, which is not among the rows when that row was cut off by MAX_ROWS.
"""
import collections
import contextlib
import dis
import io
import itertools
import json
import linecache
import traceback
import sys
import types

LEARNER_FILE = "<your code>"
MAX_FRAMES = 300       # steps recorded; more than a learner can usefully click through
MAX_LOCALS = 24        # variables shown per frame
MAX_CELLS = 40         # array cells recorded per frame (the rest are counted in "more")
MAX_REPR = 60          # characters of a variable's repr
MAX_ROWS = 20          # dict entries / set members recorded per frame (the rest are counted in "more")
MAX_SORT = 500         # sets bigger than this are shown in iteration order rather than paying to sort them at every step
MAX_NODES = 30         # linked-list nodes recorded per frame (the rest are counted in "more")
MAX_WALK = 1000        # linked-list or tree nodes followed per frame at most, so a huge one doesn't slow every step
MAX_TREE_NODES = 31    # tree nodes recorded per frame, five full levels (the rest are counted in "more")
MAX_VISITED = 100      # members of a tree's visited list or set matched against its nodes (the rest mark nothing)
MAX_GRID = 15          # rows of a grid recorded per frame, and cells of each row (the rest are counted)
MAX_GRAPH_NODES = 20   # graph nodes recorded per frame (the rest are counted in "more")
MAX_GRAPH_EDGES = 60   # graph edges recorded per frame (the rest are counted in "moreEdges")
MAX_CALLS = 12         # calls on the stack recorded per frame, the innermost (the rest are counted in "more")
MAX_CELL_REPR = 24     # characters of an array cell's, or a table key's or value's, repr
MAX_OUTPUT = 4000      # characters of stdout kept

_HIDDEN_TYPES = (type(sys), type(lambda: 0), type, type(len), type(print))   # modules, functions, classes, builtins


class _TraceLimit(BaseException):
    """Raised from the trace function to stop a run that has recorded enough. Not an Exception, so `except Exception` can't swallow it."""


def _clip(text, limit):
    return text if len(text) <= limit else text[:limit - 1] + "…"


def _repr(value, limit):
    try:
        return _clip(repr(value), limit)
    except Exception:
        return "<unprintable %s>" % type(value).__name__


def _shown(frame_locals):
    """The variables a frame lists: not dunders, modules, functions or classes, and at most MAX_LOCALS of them."""
    shown = []
    for name, value in frame_locals.items():
        if name.startswith("__") or isinstance(value, _HIDDEN_TYPES):
            continue
        shown.append((name, value))
        if len(shown) == MAX_LOCALS:
            break
    return shown


def _locals(shown):
    return [{"name": name, "type": type(value).__name__, "repr": _repr(value, MAX_REPR)} for name, value in shown]


class _Identities:
    """Gives each distinct key (or set member) of the run a number, the same one every time it is met.

    Keys are told apart the way a dict tells them apart, so 1, 1.0 and True share a number, as they share a row.
    """

    def __init__(self):
        self._by_key = {}
        self._by_object = {}
        self._taken = 0

    def _take(self):
        self._taken += 1
        return self._taken - 1

    def of(self, key):
        try:
            if key not in self._by_key:
                self._by_key[key] = self._take()
            return self._by_key[key]
        except Exception:       # a hash or equality that raises: fall back to the object itself
            return self._by_object.setdefault(id(key), self._take())


class _Nodes:
    """Gives each linked-list or tree node of the run a number, by identity. It keeps the node, so its id() can't be reused by another."""

    def __init__(self):
        self._ids = {}

    def of(self, node):
        entry = self._ids.get(id(node))
        if entry is None:
            entry = self._ids[id(node)] = (node, len(self._ids))
        return entry[1]


_MISSING = object()


def _lookup(frame, name):
    if name in frame.f_locals:
        return frame.f_locals[name]
    return frame.f_globals.get(name, _MISSING)


def _watched(frame, watch):
    for name in watch:
        if isinstance(name, str):
            value = _lookup(frame, name)
            yield name, None if value is _MISSING else value


def _is_node(value, link):
    if value is None or isinstance(value, _HIDDEN_TYPES):
        return False
    try:
        return hasattr(value, link)
    except Exception:           # a __getattr__ that raises something other than AttributeError
        return False


def _attr(value, name, default):
    try:
        return getattr(value, name, default)
    except Exception:
        return default


def _linked(entry, frame, nodes):
    """One linked list as recorded at this frame, or None while its variable holds neither a node nor None."""
    link, attr = entry.get("next", "next"), entry.get("value", "val")
    head = _lookup(frame, entry["variable"])
    if head is not None and not _is_node(head, link):
        return None
    starts = [head] if head is not None else []
    pointers = {}
    for name in entry.get("pointers", []):
        value = _lookup(frame, name)
        if value is None:
            pointers[name] = None
        elif _is_node(value, link):
            pointers[name] = nodes.of(value)
            starts.append(value)

    recorded, seen, more = [], set(), 0
    for node in starts:
        while node is not None and id(node) not in seen and len(seen) < MAX_WALK:
            seen.add(id(node))
            after = _attr(node, link, None)
            if not _is_node(after, link):
                after = None    # the end, or a link to something that isn't a node
            if len(recorded) < MAX_NODES:
                value = _attr(node, attr, _MISSING)
                recorded.append({"id": nodes.of(node), "value": None if value is _MISSING else _repr(value, MAX_CELL_REPR),
                                 "next": None if after is None else nodes.of(after)})
            else:
                more += 1
            node = after
    return {"head": None if head is None else nodes.of(head), "nodes": recorded, "more": more, "pointers": pointers}


def _lists(frame, watch, nodes):
    found = {}
    for entry in watch:
        if isinstance(entry, dict) and entry.get("as") == "linked_list":
            recorded = _linked(entry, frame, nodes)
            if recorded is not None:
                found[entry["variable"]] = recorded
    return found


def _is_tree_node(value, left, right):
    return _is_node(value, left) and _is_node(value, right)


def _same(a, b):
    try:
        return bool(a == b)
    except Exception:           # an __eq__ that raises
        return False


def _visited(value, kept):
    """Which recorded nodes the visited list or set marks. kept is [(node, its value or _MISSING, recorded)] level by level."""
    ordered = isinstance(value, (list, tuple, collections.deque))
    if not ordered and not isinstance(value, (set, frozenset)):
        return None
    marks, unmatched = {}, 0
    by_identity = {id(node): recorded for node, _, recorded in kept}
    for place, member in enumerate(itertools.islice(value, MAX_VISITED), start=1):
        found = by_identity.get(id(member))
        matches = [found] if found is not None else [r for _, v, r in kept if v is not _MISSING and _same(v, member)]
        if ordered:
            matches = [r for r in matches if r["id"] not in marks][:1]
        for recorded in matches:
            marks.setdefault(recorded["id"], place if ordered else None)
        if not matches:
            unmatched += 1
    nodes = [{"id": r["id"], "order": marks[r["id"]]} for _, _, r in kept if r["id"] in marks]
    return {"ordered": ordered, "nodes": nodes, "unmatched": unmatched + max(0, len(value) - MAX_VISITED)}


def _tree(entry, frame, nodes):
    """One binary tree as recorded at this frame, or None while its variable holds neither a node nor None."""
    left, right, attr = entry.get("left", "left"), entry.get("right", "right"), entry.get("value", "val")
    root = _lookup(frame, entry["variable"])
    if root is not None and not _is_tree_node(root, left, right):
        return None
    starts = [root] if root is not None else []
    pointers = {}
    for name in entry.get("pointers", []):
        value = _lookup(frame, name)
        if value is None:
            pointers[name] = None
        elif _is_tree_node(value, left, right):
            pointers[name] = nodes.of(value)
            starts.append(value)

    kept, seen, more = [], set(), 0
    for start in starts:
        level = collections.deque([start])
        while level and len(seen) < MAX_WALK:
            node = level.popleft()
            if id(node) in seen:
                continue
            seen.add(id(node))
            children = [_attr(node, link, None) for link in (left, right)]
            children = [c if _is_tree_node(c, left, right) else None for c in children]
            level.extend(c for c in children if c is not None and id(c) not in seen)
            if len(kept) < MAX_TREE_NODES:
                value = _attr(node, attr, _MISSING)
                kept.append((node, value, {
                    "id": nodes.of(node), "value": None if value is _MISSING else _repr(value, MAX_CELL_REPR),
                    "left": None if children[0] is None else nodes.of(children[0]),
                    "right": None if children[1] is None else nodes.of(children[1])}))
            else:
                more += 1
    visited = entry.get("visited")
    visited = None if not visited else _lookup(frame, visited)
    return {"root": None if root is None else nodes.of(root), "nodes": [r for _, _, r in kept], "more": more,
            "pointers": pointers, "visited": None if visited is None or visited is _MISSING else _visited(visited, kept)}


def _trees(frame, watch, nodes):
    found = {}
    for entry in watch:
        if isinstance(entry, dict) and entry.get("as") == "tree":
            recorded = _tree(entry, frame, nodes)
            if recorded is not None:
                found[entry["variable"]] = recorded
    return found


def _int(value):
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def _pair(value):
    """[row, col] when the value is a pair of ints (a tuple or list of two), else None."""
    if isinstance(value, (tuple, list)) and len(value) == 2:
        row, col = _int(value[0]), _int(value[1])
        if row is not None and col is not None:
            return [row, col]
    return None


def _is_grid(value):
    return isinstance(value, (list, tuple)) and all(isinstance(row, (list, tuple, str)) for row in itertools.islice(value, MAX_GRID))


def _labels(value):
    if isinstance(value, str):
        return list(value[:MAX_GRID])
    if isinstance(value, (list, tuple)):
        return [_clip(item, MAX_CELL_REPR) if isinstance(item, str) else _repr(item, MAX_CELL_REPR) for item in value[:MAX_GRID]]
    return None


def _highlight(value, widths):
    """The recorded cells a list or set of (row, col) pairs, or a grid of True/False flags, marks. widths are the recorded rows' cell counts."""
    if not isinstance(value, (list, tuple, set, frozenset)):
        return None
    inside = lambda row, col: 0 <= row < len(widths) and 0 <= col < widths[row]
    rows = list(itertools.islice(value, MAX_GRID)) if isinstance(value, (list, tuple)) else []
    if rows and all(isinstance(row, (list, tuple)) for row in rows) \
            and all(isinstance(flag, bool) for row in rows for flag in itertools.islice(row, MAX_GRID)):
        cells = [[r, c] for r, row in enumerate(rows) for c, flag in enumerate(itertools.islice(row, MAX_GRID)) if flag and inside(r, c)]
        return {"cells": cells, "unmatched": 0}
    marked, unmatched = set(), 0
    for member in itertools.islice(value, MAX_VISITED):
        pair = _pair(member)
        if pair is not None and inside(*pair):
            marked.add(tuple(pair))
        else:
            unmatched += 1
    return {"cells": [list(cell) for cell in sorted(marked)], "unmatched": unmatched + max(0, len(value) - MAX_VISITED)}


def _grid(entry, frame):
    """One grid as recorded at this frame, or None while its variable holds no list of rows."""
    value = _lookup(frame, entry["variable"])
    if not _is_grid(value):
        return None
    rows, lengths = [], []
    for row in itertools.islice(value, MAX_GRID):
        lengths.append(len(row))
        rows.append(list(row[:MAX_GRID]) if isinstance(row, str) else [_repr(cell, MAX_CELL_REPR) for cell in itertools.islice(row, MAX_GRID)])

    pointers = {}
    for names in entry.get("cells", []):
        row, col = (_lookup(frame, name) for name in names)
        if row is _MISSING or col is _MISSING:
            continue
        row, col = _int(row), _int(col)
        pointers[",".join(names)] = None if row is None or col is None else [row, col]
    for name in entry.get("pointers", []):
        held = _lookup(frame, name)
        if held is None:
            pointers[name] = None
        elif _pair(held) is not None:
            pointers[name] = _pair(held)

    def named(option, read):
        name = entry.get(option)
        held = _MISSING if not name else _lookup(frame, name)
        return None if held is _MISSING else read(held)

    return {"rows": rows, "lengths": lengths, "more": max(0, len(value) - MAX_GRID), "pointers": pointers,
            "rowLabels": named("rowLabels", _labels), "colLabels": named("colLabels", _labels),
            "highlight": named("highlight", lambda held: _highlight(held, [len(r) for r in rows]))}


def _grids(frame, watch):
    found = {}
    for entry in watch:
        if isinstance(entry, dict) and entry.get("as") == "grid":
            recorded = _grid(entry, frame)
            if recorded is not None:
                found[entry["variable"]] = recorded
    return found


def _label(value):
    return _clip(value, MAX_CELL_REPR) if isinstance(value, str) else _repr(value, MAX_CELL_REPR)


def _in_order(members):
    """A set's members sorted (by repr when they can't be compared), so its edges keep their order; a big one as it iterates."""
    if len(members) > MAX_SORT:
        return list(itertools.islice(members, MAX_WALK))
    try:
        return sorted(members)
    except Exception:
        return sorted(members, key=lambda member: _repr(member, MAX_CELL_REPR))


def _neighbours(value, weights):
    """(neighbour, weight or _MISSING) for each neighbour a node's entry lists, or nothing when it lists none; a dict's values are weights."""
    if isinstance(value, dict):
        return iter(value.items())
    if isinstance(value, (set, frozenset)):
        value = _in_order(value)
    elif not isinstance(value, (list, tuple, collections.deque)):
        return iter(())
    if not weights:
        return ((member, _MISSING) for member in value)
    return ((m[0], m[1]) if isinstance(m, (tuple, list)) and len(m) == 2 else (m, _MISSING) for m in value)


def _marks(value, ids, kinds):
    """The recorded nodes a visited or frontier variable's members are, in the order met, and how many members are none of them."""
    if not isinstance(value, kinds):
        return None
    marked, unmatched = [], 0
    for member in itertools.islice(value, MAX_VISITED):
        found = ids(member)
        if found is None:
            unmatched += 1
        elif found not in marked:
            marked.append(found)
    return {"nodes": marked, "unmatched": unmatched + max(0, len(value) - MAX_VISITED)}


def _graph(entry, frame, identities):
    """One graph as recorded at this frame, or None while its variable holds no dict."""
    graph = _lookup(frame, entry["variable"])
    if not isinstance(graph, dict):
        return None
    weights = bool(entry.get("weights"))
    kept, cut, edges, more_edges, looked = {}, set(), [], 0, 0

    def node(value):
        """The id of a node met, which is recorded while there is room."""
        found = identities.of(value)
        if found not in kept and found not in cut:
            if len(kept) < MAX_GRAPH_NODES:
                kept[found] = _label(value)
            else:
                cut.add(found)
        return found

    keys = list(itertools.islice(graph.items(), MAX_WALK))
    for key, _ in keys:
        node(key)
    looked = len(keys)
    for key, listed in keys:
        start = identities.of(key)
        for neighbour, weight in _neighbours(listed, weights):
            if looked >= MAX_WALK:
                break
            looked += 1
            end = node(neighbour)
            if start in kept and end in kept and len(edges) < MAX_GRAPH_EDGES:
                edges.append({"from": start, "to": end, "weight": None if weight is _MISSING else _repr(weight, MAX_CELL_REPR)})
            else:
                more_edges += 1

    def ids(value):
        """The id of the recorded node a value is, else None."""
        found = identities.of(value)
        return found if found in kept else None

    pointers = {}
    for name in entry.get("pointers", []):
        held = _lookup(frame, name)
        if held is None:
            pointers[name] = None
        elif held is not _MISSING and (found := ids(held)) is not None:
            pointers[name] = found

    def named(option, kinds):
        name = entry.get(option)
        held = _MISSING if not name else _lookup(frame, name)
        return None if held is _MISSING else _marks(held, ids, kinds)

    return {"nodes": [{"id": i, "label": label} for i, label in kept.items()],
            "more": len(cut) + max(0, len(graph) - len(keys)), "edges": edges, "moreEdges": more_edges, "pointers": pointers,
            "visited": named("visited", (list, tuple, collections.deque, set, frozenset, dict)),
            "frontier": named("frontier", (list, tuple, collections.deque))}


def _graphs(frame, watch, identities):
    found = {}
    for entry in watch:
        if isinstance(entry, dict) and entry.get("as") == "graph":
            recorded = _graph(entry, frame, identities)
            if recorded is not None:
                found[entry["variable"]] = recorded
    return found


def _tracked(frame, watch):
    found = {}
    for name, value in _watched(frame, watch):
        if isinstance(value, (list, tuple, collections.deque)):
            found[name] = {
                "type": type(value).__name__,
                # A deque can't be sliced, so the cells are taken by iterating, which works for all three.
                "cells": [_repr(cell, MAX_CELL_REPR) for cell in itertools.islice(value, MAX_CELLS)],
                "more": max(0, len(value) - MAX_CELLS),
            }
    return found


def _members(value):
    if len(value) > MAX_SORT:
        return list(itertools.islice(value, MAX_ROWS))
    try:
        return sorted(value)[:MAX_ROWS]
    except Exception:           # members that can't be compared (e.g. 1 and 'a')
        return sorted(value, key=lambda member: _repr(member, MAX_CELL_REPR))[:MAX_ROWS]


def _hits(shown, table, identities):
    found = {}
    for name, value in shown:
        try:
            if value in table:
                found[name] = identities.of(value)
        except Exception:       # unhashable, or an equality that raises: not a key
            pass
    return found


def _tables(frame, watch, shown, identities):
    found = {}
    for name, value in _watched(frame, watch):
        if isinstance(value, dict):
            rows = [{"id": identities.of(k), "key": _repr(k, MAX_CELL_REPR), "value": _repr(v, MAX_CELL_REPR)}
                    for k, v in itertools.islice(value.items(), MAX_ROWS)]
            kind = "dict"
        elif isinstance(value, (set, frozenset)):
            rows = [{"id": identities.of(member), "key": _repr(member, MAX_CELL_REPR)} for member in _members(value)]
            kind = "set"
        else:
            continue
        found[name] = {"kind": kind, "type": type(value).__name__, "rows": rows, "more": max(0, len(value) - MAX_ROWS),
                       "hits": _hits(shown, value, identities)}
    return found


_CO_OPTIMIZED = 0x01                   # set for a function's code, not a module's or a class body's
_SKIPPED_FLAGS = 0x20 | 0x80 | 0x200   # CO_GENERATOR, CO_COROUTINE, CO_ASYNC_GENERATOR

# A "return" event comes both for a real return and for a call an exception is leaving (with None for the value). Only a real
# return is on one of these instructions; one an exception leaves is on whatever raised or re-raised, a finally's RERAISE among
# them. (A finally runs lines as the exception passes through, so a line event is no sign the exception was caught.)
_RETURN_OPS = frozenset(dis.opmap[name] for name in ("RETURN_VALUE", "RETURN_CONST") if name in dis.opmap)


def _returns_normally(frame):
    lasti = frame.f_lasti
    return 0 <= lasti < len(frame.f_code.co_code) and frame.f_code.co_code[lasti] in _RETURN_OPS


def _call_stack_entry(watch):
    return next((e for e in watch if isinstance(e, dict) and e.get("as") == "call_stack"), None)


def _is_shown_call(code, entry):
    """Whether a call of this code goes on the recorded call stack: a function (or lambda) of the learner's, not a class body, a generator or a comprehension."""
    if not code.co_flags & _CO_OPTIMIZED or code.co_flags & _SKIPPED_FLAGS or (code.co_name.startswith("<") and code.co_name != "<lambda>"):
        return False
    functions = entry.get("functions")
    return not functions or code.co_name in functions


def _args(frame):
    code = frame.f_code
    count = code.co_argcount + code.co_kwonlyargcount + bool(code.co_flags & 0x04) + bool(code.co_flags & 0x08)
    names = code.co_varnames[:count]
    if names and names[0] in ("self", "cls"):
        names = names[1:]
    values = frame.f_locals
    return [{"name": name, "repr": _repr(values[name], MAX_CELL_REPR) if name in values else "?"} for name in names]


class _Calls:
    """The learner's calls that are under way, by frame. A frame is kept until its call returns, so its id() can't be reused meanwhile."""

    def __init__(self, entry):
        self.entry = entry
        self._by_frame = {}
        self._taken = 0
        self.returned = None    # what the last return handed back, for the next frame recorded

    def enter(self, frame):
        if _is_shown_call(frame.f_code, self.entry):
            self._by_frame[id(frame)] = (frame, {"id": self._taken, "function": frame.f_code.co_name, "args": _args(frame)})
            self._taken += 1

    def call_of(self, frame):
        entry = self._by_frame.get(id(frame))
        return entry[1] if entry is not None and entry[0] is frame else None

    def caller_of(self, frame):
        frame = frame.f_back
        while frame is not None:
            call = self.call_of(frame)
            if call is not None:
                return call
            frame = frame.f_back
        return None

    def leave(self, frame, value):
        call = self.call_of(frame)
        if call is not None and _returns_normally(frame):
            caller = self.caller_of(frame)
            self.returned = {"function": call["function"], "value": _repr(value, MAX_CELL_REPR),
                             "to": None if caller is None else caller["id"]}
        self._by_frame.pop(id(frame), None)

    def record(self, frame, returning=None):
        stack = []
        while frame is not None:
            call = self.call_of(frame)
            if call is not None:
                stack.append(dict(call, returning=returning if not stack else None))
            frame = frame.f_back
        stack.reverse()
        recorded = {"frames": stack[-MAX_CALLS:], "more": max(0, len(stack) - MAX_CALLS), "returned": self.returned}
        self.returned = None
        return recorded


def _error_trace(exc):
    frames = [f for f in traceback.extract_tb(exc.__traceback__) if f.filename == LEARNER_FILE]
    line = frames[-1].lineno if frames else None
    if isinstance(exc, SyntaxError) and exc.filename == LEARNER_FILE:
        line = exc.lineno
    lines = []
    if frames:
        lines.append("Traceback (most recent call last):\n")
        lines.extend(traceback.format_list(frames))
    lines.extend(traceback.format_exception_only(type(exc), exc))
    return _clip("".join(lines).rstrip("\n"), MAX_OUTPUT), line


def _result(status, frames, stdout="", message=None, tb=None, line=None):
    return json.dumps({
        "status": status, "frames": frames, "stdout": _clip(stdout, MAX_OUTPUT),
        "message": message, "traceback": tb, "errorLine": line,
    })


def trace_code(code, watch_json, max_frames=MAX_FRAMES):
    watch = json.loads(watch_json)
    linecache.cache[LEARNER_FILE] = (len(code), None, code.splitlines(True), LEARNER_FILE)
    # The code runs as a module that is in sys.modules while it runs, as @dataclass needs to look up its class's module.
    module = types.ModuleType("learner")
    namespace = module.__dict__
    frames = []
    identities = _Identities()
    nodes = _Nodes()
    entry = _call_stack_entry(watch)
    calls = _Calls(entry) if entry is not None else None
    out = io.StringIO()

    try:
        compiled = compile(code, LEARNER_FILE, "exec")
    except SyntaxError as exc:
        tb, line = _error_trace(exc)
        return _result("error", frames, message="Python couldn't read this code.", tb=tb, line=line)

    def record(event, frame, line, returning=None):
        if len(frames) >= max_frames:
            raise _TraceLimit()
        shown = _shown(frame.f_globals if frame.f_code.co_name == "<module>" else frame.f_locals)
        recorded = {
            "event": event, "line": line, "function": frame.f_code.co_name,
            "locals": _locals(shown), "tracked": _tracked(frame, watch), "tables": _tables(frame, watch, shown, identities),
            "lists": _lists(frame, watch, nodes), "trees": _trees(frame, watch, nodes),
            "grids": _grids(frame, watch), "graphs": _graphs(frame, watch, identities),
        }
        if calls is not None:
            recorded["calls"] = calls.record(frame, returning)
        frames.append(recorded)

    def on_line(frame, event, arg):
        if event == "line":
            record("line", frame, frame.f_lineno)
        elif event == "return":
            if frame.f_code.co_name == "<module>":
                record("return", frame, None)
            elif calls is not None:
                if calls.call_of(frame) is not None and _returns_normally(frame):
                    record("return", frame, frame.f_lineno, _repr(arg, MAX_CELL_REPR))
                calls.leave(frame, arg)
        return on_line

    def on_call(frame, event, arg):
        if frame.f_code.co_filename != LEARNER_FILE:
            return None
        if calls is not None:
            calls.enter(frame)
        return on_line

    status, message, tb, error_line = "ok", None, None, None
    previous = sys.gettrace()
    replaced = sys.modules.get(module.__name__)
    sys.modules[module.__name__] = module
    try:
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            sys.settrace(on_call)
            try:
                exec(compiled, namespace)
            finally:
                sys.settrace(previous)
                if replaced is None:
                    del sys.modules[module.__name__]
                else:
                    sys.modules[module.__name__] = replaced
    except _TraceLimit:
        status, message = "truncated", "This code runs for more than %d steps, so only the first %d are shown." % (max_frames, max_frames)
    except (Exception, SystemExit) as exc:
        status, message = "error", "The code raised an error."
        tb, error_line = _error_trace(exc)
        if frames and frames[-1]["event"] == "return" and frames[-1]["function"] == "<module>":
            frames.pop()    # Python reports a return event while an exception unwinds; the run never finished, so there's no final state
    return _result(status, frames, out.getvalue(), message, tb, error_line)
