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


class TrackedTables(unittest.TestCase):
    def test_a_watched_dict_is_recorded_as_rows_in_insertion_order(self):
        r = trace("d = {'b': 2, 'a': 1}\n", watch=["d"])
        t = r["frames"][-1]["tables"]["d"]
        self.assertEqual([(row["key"], row["value"]) for row in t["rows"]], [("'b'", "2"), ("'a'", "1")])
        self.assertEqual((t["kind"], t["type"], t["more"], t["hits"]), ("dict", "dict", 0, {}))
        self.assertNotIn("d", r["frames"][-1]["tracked"])           # tables are not arrays

    def test_a_watched_set_is_recorded_sorted_so_that_its_order_does_not_jump_around(self):
        r = trace("s = {30, 1, 20}\n", watch=["s"])
        t = r["frames"][-1]["tables"]["s"]
        self.assertEqual((t["kind"], t["type"], [row["key"] for row in t["rows"]]), ("set", "set", ["1", "20", "30"]))
        self.assertTrue(all("value" not in row for row in t["rows"]))

    def test_a_frozenset_is_a_set_and_a_dict_subclass_is_a_dict(self):
        r = trace("import collections\nf = frozenset([2, 1])\nc = collections.OrderedDict(a=1)\n", watch=["f", "c"])
        tables = r["frames"][-1]["tables"]
        self.assertEqual((tables["f"]["kind"], tables["f"]["type"]), ("set", "frozenset"))
        self.assertEqual((tables["c"]["kind"], tables["c"]["type"]), ("dict", "OrderedDict"))

    def test_a_set_of_mixed_types_falls_back_to_ordering_by_repr(self):
        r = trace("s = {1, 'a', (2, 3)}\n", watch=["s"])
        self.assertEqual([row["key"] for row in r["frames"][-1]["tables"]["s"]["rows"]], ["'a'", "(2, 3)", "1"])

    def test_a_very_large_set_is_cut_without_being_sorted(self):
        code = ("class K:\n    compared = 0\n    def __lt__(self, other):\n        K.compared += 1\n        return id(self) < id(other)\n"
                "s = set(map(K.__new__, [K] * 600))\nprint(K.compared)\n")
        r = trace(code, watch=["s"])
        table = r["frames"][-1]["tables"]["s"]
        self.assertEqual((len(table["rows"]), table["more"]), (tracer.MAX_ROWS, 600 - tracer.MAX_ROWS))
        self.assertEqual(r["stdout"], "0\n")                         # sorting 600 members would have compared them

    def test_a_list_is_not_a_table_and_a_dict_is_not_an_array(self):
        r = trace("xs = [1]\nd = {}\n", watch=["xs", "d"])
        last = r["frames"][-1]
        self.assertEqual(list(last["tracked"]), ["xs"])
        self.assertEqual(list(last["tables"]), ["d"])

    def test_frames_are_snapshots_of_the_table(self):
        r = trace("d = {}\nd['a'] = 1\nd['a'] = 2\ndel d['a']\n", watch=["d"])
        rows = [[(row["key"], row["value"]) for row in f["tables"]["d"]["rows"]] for f in r["frames"] if "d" in f["tables"]]
        self.assertEqual(rows, [[], [("'a'", "1")], [("'a'", "2")], []])

    def test_long_tables_are_cut_and_the_rest_counted(self):
        r = trace("d = {i: i for i in range(100)}\ns = set(range(100))\n", watch=["d", "s"])
        for name in ("d", "s"):
            t = r["frames"][-1]["tables"][name]
            self.assertEqual((len(t["rows"]), t["more"]), (tracer.MAX_ROWS, 100 - tracer.MAX_ROWS))

    def test_keys_and_values_are_clipped(self):
        r = trace("d = {'k' * 100: 'v' * 100}\n", watch=["d"])
        row = r["frames"][-1]["tables"]["d"]["rows"][0]
        self.assertLessEqual(len(row["key"]), tracer.MAX_CELL_REPR)
        self.assertLessEqual(len(row["value"]), tracer.MAX_CELL_REPR)

    def test_a_dict_passed_into_a_function_is_found_by_its_parameter_name(self):
        r = trace("def size(table):\n    return len(table)\nsize({'a': 1})\n", watch=["table"])
        inside = next(f for f in r["frames"] if f["function"] == "size")
        self.assertEqual([(row["key"], row["value"]) for row in inside["tables"]["table"]["rows"]], [("'a'", "1")])

    def test_a_value_whose_repr_fails_is_still_a_row(self):
        code = "class Bad:\n    def __repr__(self):\n        raise ValueError\nd = {'x': Bad()}\n"
        row = trace(code, watch=["d"])["frames"][-1]["tables"]["d"]["rows"][0]
        self.assertEqual(row["value"], "<unprintable Bad>")

    def test_frames_always_carry_a_tables_object(self):
        self.assertEqual(trace("x = 1\n")["frames"][0]["tables"], {})


def rows(frame, name):
    return frame["tables"][name]["rows"]


def hits(frame, name):
    return frame["tables"][name]["hits"]


class TableIdentity(unittest.TestCase):
    """A row's "id" tells rows apart (and the same row across steps) even when their clipped labels read the same."""

    def test_keys_that_clip_to_the_same_label_still_get_different_ids(self):
        r = trace("d = {'a' * 30 + 'x': 1, 'a' * 30 + 'y': 2}\n", watch=["d"])
        first, second = rows(r["frames"][-1], "d")
        self.assertEqual(first["key"], second["key"])
        self.assertNotEqual(first["id"], second["id"])

    def test_set_members_that_clip_to_the_same_label_get_different_ids(self):
        r = trace("s = {'a' * 30 + 'x', 'a' * 30 + 'y'}\n", watch=["s"])
        first, second = rows(r["frames"][-1], "s")
        self.assertEqual(first["key"], second["key"])
        self.assertNotEqual(first["id"], second["id"])

    def test_a_key_keeps_its_id_from_step_to_step_and_a_new_key_gets_a_new_one(self):
        r = trace("d = {'a': 1}\nd['b'] = 2\nd['a'] = 5\n", watch=["d"])
        frames = [f for f in r["frames"] if "d" in f["tables"]]
        ids = [{row["key"]: row["id"] for row in rows(f, "d")} for f in frames]
        self.assertEqual(ids[0]["'a'"], ids[1]["'a'"])
        self.assertEqual(ids[1]["'a'"], ids[2]["'a'"])
        self.assertNotEqual(ids[1]["'a'"], ids[1]["'b'"])

    def test_a_key_that_python_treats_as_equal_has_one_id(self):
        r = trace("d = {1: 'x'}\nd2 = {True: 'x'}\n", watch=["d", "d2"])
        last = r["frames"][-1]
        self.assertEqual(rows(last, "d")[0]["id"], rows(last, "d2")[0]["id"])

    def test_keys_whose_equality_raises_do_not_break_the_trace(self):
        code = ("class E:\n    def __hash__(self): return 1\n    def __eq__(self, other): raise ValueError\n"
                "a = {E(): 1}\nb = {E(): 2}\n")
        r = trace(code, watch=["a", "b"])
        self.assertEqual(r["status"], "ok")
        last = r["frames"][-1]
        self.assertIsInstance(rows(last, "a")[0]["id"], int)
        self.assertNotEqual(rows(last, "a")[0]["id"], rows(last, "b")[0]["id"])


class TableLookups(unittest.TestCase):
    """"hits" says which local variables hold a key (or member) of the table, by Python's own equality."""

    def test_a_variable_holding_a_key_hits_that_keys_row(self):
        r = trace("d = {'a': 1, 'b': 2}\nword = 'b'\n", watch=["d"])
        last = r["frames"][-1]
        self.assertEqual(hits(last, "d"), {"word": rows(last, "d")[1]["id"]})

    def test_equal_numbers_of_different_types_hit_though_their_reprs_differ(self):
        r = trace("d = {1: 'found'}\nkey = 1.0\nflag = True\n", watch=["d"])
        last = r["frames"][-1]
        self.assertEqual(hits(last, "d"), {"key": rows(last, "d")[0]["id"], "flag": rows(last, "d")[0]["id"]})

    def test_set_membership_uses_equality_too(self):
        r = trace("s = {1, 2}\nx = 2.0\n", watch=["s"])
        last = r["frames"][-1]
        self.assertEqual(hits(last, "s"), {"x": rows(last, "s")[1]["id"]})

    def test_a_variable_that_is_not_a_key_is_not_a_hit(self):
        r = trace("d = {'a': 1}\nword = 'zzz'\nn = 1\n", watch=["d"])
        self.assertEqual(hits(r["frames"][-1], "d"), {})

    def test_an_unhashable_variable_is_not_a_hit_and_does_not_break_the_trace(self):
        r = trace("d = {'a': 1}\nxs = [1]\nother = {2: 3}\n", watch=["d"])
        self.assertEqual(r["status"], "ok")
        self.assertEqual(hits(r["frames"][-1], "d"), {})

    def test_a_variable_whose_equality_raises_is_not_a_hit(self):
        code = ("class E:\n    def __hash__(self): return hash('a')\n    def __eq__(self, other): raise ValueError\n"
                "d = {'a': 1}\ne = E()\n")
        r = trace(code, watch=["d"])
        self.assertEqual(r["status"], "ok")
        self.assertEqual(hits(r["frames"][-1], "d"), {})

    def test_a_key_beyond_the_rows_shown_is_still_a_hit_with_an_id_that_is_not_shown(self):
        r = trace("d = {i: i for i in range(100)}\nk = 50\n", watch=["d"])
        last = r["frames"][-1]
        self.assertIn("k", hits(last, "d"))
        self.assertNotIn(hits(last, "d")["k"], [row["id"] for row in rows(last, "d")])

    def test_a_key_that_later_comes_into_view_has_the_id_it_was_given_as_a_hit(self):
        r = trace("d = {i: i for i in range(25)}\nk = 20\ndel d[0]\n", watch=["d"])
        before, after = [f for f in r["frames"] if "d" in f["tables"]][-2:]
        self.assertNotIn(hits(before, "d")["k"], [row["id"] for row in rows(before, "d")])    # row 20 is the 21st: cut off
        shown = {row["key"]: row["id"] for row in rows(after, "d")}
        self.assertEqual(hits(after, "d")["k"], shown["20"])                                  # del d[0] pulled it into view

    def test_the_lookup_is_found_for_a_variable_inside_a_function(self):
        r = trace("def has(table, key):\n    return key in table\nhas({'a': 1}, 'a')\n", watch=["table"])
        inside = [f for f in r["frames"] if f["function"] == "has"][-1]
        self.assertEqual(hits(inside, "table"), {"key": rows(inside, "table")[0]["id"]})


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
