"""Records an execution trace of a learner's (or an author's) code for the trace player.

Loaded by the Pyodide worker (python-worker.js) and also importable under plain CPython, which is how
tests/python/test_tracer.py and the content validator check it. Only the standard library is used.

trace_code(code, watch_json, max_frames=MAX_FRAMES) -> JSON string:
    {"status": "ok" | "error" | "truncated",
     "frames": [{"event": "line" | "return", "line": int | null, "function": str,
                 "locals": [{"name", "type", "repr"}],
                 "tracked": {variable: {"type": str, "cells": [str], "more": int}}}],
     "stdout": str, "message": str | null, "traceback": str | null, "errorLine": int | null}

A "line" frame is the state just before that line runs. The last frame is the module's "return": the final
state, with no line. "error" means the code raised (frames up to the error are kept) or never compiled;
"truncated" means it ran past max_frames and was stopped. `watch_json` is the list of variable names to record
as arrays, e.g. ["nums"]; a name is looked up in the current frame's locals, then in the globals, and left out
while it is not a list or tuple.
"""
import contextlib
import io
import json
import linecache
import traceback
import sys

LEARNER_FILE = "<your code>"
MAX_FRAMES = 300       # steps recorded; more than a learner can usefully click through
MAX_LOCALS = 24        # variables shown per frame
MAX_CELLS = 40         # array cells recorded per frame (the rest are counted in "more")
MAX_REPR = 60          # characters of a variable's repr
MAX_CELL_REPR = 24     # characters of an array cell's repr
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


def _locals(frame_locals):
    shown = []
    for name, value in frame_locals.items():
        if name.startswith("__") or isinstance(value, _HIDDEN_TYPES):
            continue
        shown.append({"name": name, "type": type(value).__name__, "repr": _repr(value, MAX_REPR)})
        if len(shown) == MAX_LOCALS:
            break
    return shown


def _tracked(frame, watch):
    found = {}
    for name in watch:
        value = frame.f_locals[name] if name in frame.f_locals else frame.f_globals.get(name)
        if isinstance(value, (list, tuple)):
            found[name] = {
                "type": type(value).__name__,
                "cells": [_repr(cell, MAX_CELL_REPR) for cell in value[:MAX_CELLS]],
                "more": max(0, len(value) - MAX_CELLS),
            }
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
    out = io.StringIO()

    try:
        compiled = compile(code, LEARNER_FILE, "exec")
    except SyntaxError as exc:
        tb, line = _error_trace(exc)
        return _result("error", frames, message="Python couldn't read this code.", tb=tb, line=line)

    def record(event, frame, line):
        if len(frames) >= max_frames:
            raise _TraceLimit()
        locals_ = frame.f_globals if frame.f_code.co_name == "<module>" else frame.f_locals
        frames.append({
            "event": event, "line": line, "function": frame.f_code.co_name,
            "locals": _locals(locals_), "tracked": _tracked(frame, watch),
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
