"""Checks the trace recorder under plain CPython (the same file runs inside Pyodide in the app).

Run:  python3 -I -m unittest discover -s tests/python
"""
import importlib.util
import json
import pathlib
import unittest

_path = pathlib.Path(__file__).resolve().parents[2] / "src/Brilliant.App/wwwroot/python/tracer.py"
_spec = importlib.util.spec_from_file_location("tracer", _path)
tracer = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(tracer)


def trace(code, watch=(), **kwargs):
    return json.loads(tracer.trace_code(code, json.dumps(list(watch)), **kwargs))


def local(frame, name):
    return next((v for v in frame["locals"] if v["name"] == name), None)


class Frames(unittest.TestCase):
    def test_records_the_state_before_each_line_runs(self):
        r = trace("a = 1\nb = a + 1\n")
        self.assertEqual(r["status"], "ok")
        line_frames = [f for f in r["frames"] if f["event"] == "line"]
        self.assertEqual([f["line"] for f in line_frames], [1, 2])
        self.assertIsNone(local(line_frames[0], "a"))               # before line 1 runs
        self.assertEqual(local(line_frames[1], "a"), {"name": "a", "type": "int", "repr": "1"})

    def test_the_last_frame_is_the_final_state_with_no_line(self):
        r = trace("a = 1\nb = a + 1\n")
        last = r["frames"][-1]
        self.assertEqual((last["event"], last["line"], last["function"]), ("return", None, "<module>"))
        self.assertEqual(local(last, "b")["repr"], "2")

    def test_loops_record_every_pass(self):
        r = trace("total = 0\nfor i in range(3):\n    total += i\n")
        self.assertEqual([f["line"] for f in r["frames"] if f["event"] == "line"], [1, 2, 3, 2, 3, 2, 3, 2])

    def test_functions_report_their_own_locals_and_name(self):
        r = trace("def f(x):\n    y = x * 2\n    return y\nf(4)\n")
        inside = [f for f in r["frames"] if f["function"] == "f"]
        self.assertEqual([f["line"] for f in inside], [2, 3])
        self.assertEqual(local(inside[1], "y")["repr"], "8")
        self.assertIsNone(local(inside[1], "f"))                    # the module's names aren't mixed in

    def test_functions_modules_and_classes_are_not_listed_as_variables(self):
        r = trace("import math\ndef f(): pass\nclass C: pass\nn = 5\n")
        names = [v["name"] for v in r["frames"][-1]["locals"]]
        self.assertEqual(names, ["n"])

    def test_long_reprs_are_clipped(self):
        r = trace("s = 'x' * 500\n")
        self.assertLessEqual(len(local(r["frames"][-1], "s")["repr"]), tracer.MAX_REPR)

    def test_a_value_whose_repr_fails_is_still_listed(self):
        r = trace("class Bad:\n    def __repr__(self):\n        raise ValueError\nb = Bad()\n")
        self.assertEqual(local(r["frames"][-1], "b")["repr"], "<unprintable Bad>")


class TrackedArrays(unittest.TestCase):
    def test_a_watched_list_is_recorded_cell_by_cell(self):
        r = trace("nums = [3, 8, 2]\n", watch=["nums"])
        self.assertEqual(r["frames"][-1]["tracked"]["nums"], {"type": "list", "cells": ["3", "8", "2"], "more": 0})

    def test_it_is_absent_until_it_exists_and_when_it_is_not_a_list(self):
        r = trace("x = 5\nx = [1]\n", watch=["x"])
        lines = [f for f in r["frames"] if f["event"] == "line"]
        self.assertEqual(lines[0]["tracked"], {})
        self.assertEqual(lines[1]["tracked"], {})                   # still 5 before line 2 runs
        self.assertIn("x", r["frames"][-1]["tracked"])

    def test_each_frame_is_a_snapshot(self):
        r = trace("nums = [1, 2]\nnums[0] = 9\n", watch=["nums"])
        cells = [f["tracked"]["nums"]["cells"] for f in r["frames"] if "nums" in f["tracked"]]
        self.assertEqual(cells, [["1", "2"], ["9", "2"]])

    def test_tuples_count_and_cells_use_repr(self):
        r = trace("t = ('a', 2)\n", watch=["t"])
        self.assertEqual(r["frames"][-1]["tracked"]["t"], {"type": "tuple", "cells": ["'a'", "2"], "more": 0})

    def test_a_list_passed_into_a_function_is_found_by_its_parameter_name(self):
        r = trace("def total(xs):\n    return sum(xs)\ntotal([1, 2])\n", watch=["xs"])
        inside = next(f for f in r["frames"] if f["function"] == "total")
        self.assertEqual(inside["tracked"]["xs"]["cells"], ["1", "2"])

    def test_long_lists_are_cut_and_the_rest_counted(self):
        r = trace("xs = list(range(100))\n", watch=["xs"])
        t = r["frames"][-1]["tracked"]["xs"]
        self.assertEqual((len(t["cells"]), t["more"]), (tracer.MAX_CELLS, 100 - tracer.MAX_CELLS))

    def test_cells_are_clipped(self):
        r = trace("xs = ['y' * 100]\n", watch=["xs"])
        self.assertLessEqual(len(r["frames"][-1]["tracked"]["xs"]["cells"][0]), tracer.MAX_CELL_REPR)


class Problems(unittest.TestCase):
    def test_a_syntax_error_has_no_frames_and_a_line(self):
        r = trace("a = (\n")
        self.assertEqual((r["status"], r["frames"]), ("error", []))
        self.assertIsNotNone(r["errorLine"])

    def test_a_runtime_error_keeps_the_frames_up_to_it(self):
        r = trace("a = 1\nb = a / 0\nc = 3\n")
        self.assertEqual(r["status"], "error")
        self.assertEqual(r["errorLine"], 2)
        self.assertIn("ZeroDivisionError", r["traceback"])
        self.assertEqual([f["line"] for f in r["frames"]], [1, 2])

    def test_runaway_code_is_cut_at_the_frame_limit(self):
        r = trace("i = 0\nwhile True:\n    i += 1\n", max_frames=10)
        self.assertEqual(r["status"], "truncated")
        self.assertEqual(len(r["frames"]), 10)
        self.assertIn("10", r["message"])

    def test_the_limit_cannot_be_swallowed_by_the_learners_code(self):
        r = trace("while True:\n    try:\n        pass\n    except Exception:\n        pass\n", max_frames=20)
        self.assertEqual(r["status"], "truncated")

    def test_print_output_is_captured_and_does_not_leak(self):
        r = trace("print('hi')\n")
        self.assertEqual(r["stdout"], "hi\n")

    def test_tracing_is_switched_off_afterwards(self):
        import sys
        before = sys.gettrace()
        trace("a = 1\n")
        trace("1/0\n")
        trace("while True: pass\n", max_frames=5)
        self.assertEqual(sys.gettrace(), before)


if __name__ == "__main__":
    unittest.main()
