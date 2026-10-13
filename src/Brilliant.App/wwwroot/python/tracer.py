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
MAX_WALK = 1000        # linked-list nodes followed per frame at most, so a huge list doesn't slow every step
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
    """Gives each linked-list node of the run a number, by identity. It keeps the node, so its id() can't be reused by another."""

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
            "lists": _lists(frame, watch, nodes),
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
