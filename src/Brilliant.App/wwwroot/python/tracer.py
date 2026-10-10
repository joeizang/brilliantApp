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
                                       "hits": {local variable: id}}}}],
     "stdout": str, "message": str | null, "traceback": str | null, "errorLine": int | null}

A "line" frame is the state just before that line runs. The last frame is the module's "return": the final
state, with no line. "error" means the code raised (frames up to the error are kept) or never compiled;
"truncated" means it ran past max_frames and was stopped. `watch_json` is the list of variable names to record,
e.g. ["nums", "counts"]; a name is looked up in the current frame's locals, then in the globals. A list or tuple is
recorded in "tracked" (cells); a dict (or subclass) or a set/frozenset in "tables" (rows: a set's rows have no
"value"). A name that is none of those is left out until it is. A set's rows are sorted, so its order doesn't jump
around as members are added; a dict's are in insertion order.

A row's "key" is only its label: the repr, clipped, so two different keys can read the same. Its "id" is what tells
rows apart, and the same key has the same id at every step of the run. "hits" says which of the frame's variables hold
a key (or member) of the table, by Python's own equality: `1.0` finds the key `1`, `True` too, whatever their reprs say.
A hit's id is the id of the row it found, which is not among the rows when that row was cut off by MAX_ROWS.
"""
import contextlib
import io
import itertools
import json
import linecache
import traceback
import sys

LEARNER_FILE = "<your code>"
MAX_FRAMES = 300       # steps recorded; more than a learner can usefully click through
MAX_LOCALS = 24        # variables shown per frame
MAX_CELLS = 40         # array cells recorded per frame (the rest are counted in "more")
MAX_REPR = 60          # characters of a variable's repr
MAX_ROWS = 20          # dict entries / set members recorded per frame (the rest are counted in "more")
MAX_SORT = 500         # sets bigger than this are shown in iteration order rather than paying to sort them at every step
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


def _watched(frame, watch):
    for name in watch:
        yield name, frame.f_locals[name] if name in frame.f_locals else frame.f_globals.get(name)


def _tracked(frame, watch):
    found = {}
    for name, value in _watched(frame, watch):
        if isinstance(value, (list, tuple)):
            found[name] = {
                "type": type(value).__name__,
                "cells": [_repr(cell, MAX_CELL_REPR) for cell in value[:MAX_CELLS]],
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
    namespace = {"__name__": "learner"}
    frames = []
    identities = _Identities()
    out = io.StringIO()

    try:
        compiled = compile(code, LEARNER_FILE, "exec")
    except SyntaxError as exc:
        tb, line = _error_trace(exc)
        return _result("error", frames, message="Python couldn't read this code.", tb=tb, line=line)

    def record(event, frame, line):
        if len(frames) >= max_frames:
            raise _TraceLimit()
        shown = _shown(frame.f_globals if frame.f_code.co_name == "<module>" else frame.f_locals)
        frames.append({
            "event": event, "line": line, "function": frame.f_code.co_name,
            "locals": _locals(shown), "tracked": _tracked(frame, watch), "tables": _tables(frame, watch, shown, identities),
        })

    def on_line(frame, event, arg):
        if event == "line":
            record("line", frame, frame.f_lineno)
        elif event == "return" and frame.f_code.co_name == "<module>":
            record("return", frame, None)
        return on_line

    def on_call(frame, event, arg):
        return on_line if frame.f_code.co_filename == LEARNER_FILE else None

    status, message, tb, error_line = "ok", None, None, None
    previous = sys.gettrace()
    try:
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            sys.settrace(on_call)
            try:
                exec(compiled, namespace)
            finally:
                sys.settrace(previous)
    except _TraceLimit:
        status, message = "truncated", "This code runs for more than %d steps, so only the first %d are shown." % (max_frames, max_frames)
    except (Exception, SystemExit) as exc:
        status, message = "error", "The code raised an error."
        tb, error_line = _error_trace(exc)
        if frames and frames[-1]["event"] == "return" and frames[-1]["function"] == "<module>":
            frames.pop()    # Python reports a return event while an exception unwinds; the run never finished, so there's no final state
    return _result(status, frames, out.getvalue(), message, tb, error_line)
