"""Runs a learner's code against a write-code step's hidden tests.

Loaded by the Pyodide worker (python-worker.js) and also importable under plain CPython, which is how
tests/python/test_harness.py checks it. Only the standard library is used.

run_tests(code, entrypoint, tests_json) -> JSON string:
    {"status": "passed" | "failed" | "error",
     "stdout": str, "message": str | null, "traceback": str | null, "errorLine": int | null,
     "tests": [{"call", "expected", "actual", "passed", "stdout", "traceback", "errorLine"}]}

"error" means the code never reached the tests (syntax error, exception while loading, missing function).
"""
import contextlib
import io
import json
import linecache
import traceback

LEARNER_FILE = "<your code>"
MAX_OUTPUT = 4000      # characters of stdout / repr kept, so a print loop can't flood the UI
MAX_FRAMES = 10        # learner frames kept in a traceback (deep recursion would otherwise print hundreds)


def _clip(text, limit=MAX_OUTPUT):
    return text if len(text) <= limit else text[:limit] + "\n… (output cut off)"


def _trace(exc):
    """Traceback limited to the learner's own frames, plus the line number of the innermost one."""
    frames = [f for f in traceback.extract_tb(exc.__traceback__) if f.filename == LEARNER_FILE]
    line = frames[-1].lineno if frames else None
    if isinstance(exc, SyntaxError) and exc.filename == LEARNER_FILE:
        line = exc.lineno
    skipped = max(0, len(frames) - MAX_FRAMES)
    lines = []
    if frames:
        lines.append("Traceback (most recent call last):\n")
        if skipped:
            lines.append("  … %d earlier calls not shown\n" % skipped)
        lines.extend(traceback.format_list(frames[skipped:]))
    lines.extend(traceback.format_exception_only(type(exc), exc))
    return _clip("".join(lines).rstrip("\n")), line


def _result(status, stdout="", message=None, tb=None, line=None, tests=None):
    return json.dumps({
        "status": status, "stdout": _clip(stdout), "message": message, "traceback": tb, "errorLine": line,
        "tests": tests or [],
    })


def run_tests(code, entrypoint, tests_json):
    tests = json.loads(tests_json)
    linecache.cache[LEARNER_FILE] = (len(code), None, code.splitlines(True), LEARNER_FILE)
    namespace = {"__name__": "learner"}
    out = io.StringIO()

    # 1. Load the learner's code (compile, then run top-level statements).
    try:
        compiled = compile(code, LEARNER_FILE, "exec")
    except SyntaxError as exc:
        tb, line = _trace(exc)
        return _result("error", message="Python couldn't read your code.", tb=tb, line=line)
    try:
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            exec(compiled, namespace)
    except (Exception, SystemExit) as exc:
        tb, line = _trace(exc)
        return _result("error", out.getvalue(), "Your code raised an error before the tests could run.", tb, line)

    fn = namespace.get(entrypoint)
    if not callable(fn):
        return _result("error", out.getvalue(), "Define a function called `%s`." % entrypoint)

    # 2. Call it once per test. The expected value is evaluated in a clean namespace so the learner can't affect it.
    outcomes = []
    for test in tests:
        call = "%s(%s)" % (entrypoint, test["input"])
        try:
            expected = eval(test["expected"], {})
        except Exception as exc:
            return _result("error", message="This exercise has a broken test (expected `%s`): %s" % (test["expected"], exc))

        buf = io.StringIO()
        actual_repr, tb, line, passed = None, None, None, False
        try:
            with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
                actual = eval(call, namespace)
            actual_repr = _clip(repr(actual))
            passed = bool(actual == expected)
        except (Exception, SystemExit) as exc:
            tb, line = _trace(exc)
        outcomes.append({
            "call": call, "expected": _clip(repr(expected)), "actual": actual_repr, "passed": passed,
            "stdout": _clip(buf.getvalue()), "traceback": tb, "errorLine": line,
        })

    status = "passed" if outcomes and all(o["passed"] for o in outcomes) else "failed"
    return _result(status, out.getvalue(), tests=outcomes)
