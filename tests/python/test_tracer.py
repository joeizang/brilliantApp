"""Checks the trace recorder under plain CPython (the same file runs inside Pyodide in the app).

Run:  python3 -I -m unittest discover -s tests/python
"""
import importlib.util
import json
import pathlib
import unittest

_path = pathlib.Path(__file__).resolve().parents[2] / "src/Brilliant.Runtime.Python/wwwroot/python/tracer.py"
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


    def test_a_deque_is_recorded_front_to_back(self):
        r = trace("from collections import deque\nq = deque([1, 2])\nq.append(3)\nq.appendleft(0)\nq.popleft()\n", watch=["q"])
        cells = [f["tracked"]["q"]["cells"] for f in r["frames"] if "q" in f["tracked"]]
        self.assertEqual(cells, [["1", "2"], ["1", "2", "3"], ["0", "1", "2", "3"], ["1", "2", "3"]])
        self.assertEqual(r["frames"][-1]["tracked"]["q"]["type"], "deque")

    def test_a_long_deque_is_cut_and_the_rest_counted(self):
        r = trace("import collections\nq = collections.deque(range(100))\n", watch=["q"])
        t = r["frames"][-1]["tracked"]["q"]
        self.assertEqual((t["cells"][:2], len(t["cells"]), t["more"]), (["0", "1"], tracer.MAX_CELLS, 100 - tracer.MAX_CELLS))

    def test_a_bounded_deque_is_recorded_as_it_stands(self):
        r = trace("from collections import deque\nq = deque(maxlen=2)\nfor x in 'abc':\n    q.append(x)\n", watch=["q"])
        self.assertEqual(r["frames"][-1]["tracked"]["q"]["cells"], ["'b'", "'c'"])

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


class LinkedLists(unittest.TestCase):
    NODE = "class Node:\n    def __init__(self, val, next=None):\n        self.val = val\n        self.next = next\n"

    def lists(self, code, pointers=(), **options):
        entry = dict({"variable": "head", "as": "linked_list", "pointers": list(pointers)}, **options)
        return trace(self.NODE + code, [entry])

    def final(self, code, pointers=(), **options):
        return self.lists(code, pointers, **options)["frames"][-1]["lists"]["head"]

    def chain(self, recorded):
        return [(n["value"], n["next"]) for n in recorded["nodes"]]

    def test_follows_next_from_the_head_and_ends_at_none(self):
        head = self.final("head = Node(1, Node(2, Node(3)))\n")
        ids = [n["id"] for n in head["nodes"]]
        self.assertEqual(head["head"], ids[0])
        self.assertEqual(self.chain(head), [("1", ids[1]), ("2", ids[2]), ("3", None)])
        self.assertEqual(head["more"], 0)

    def test_works_with_dataclass_nodes_and_other_attribute_names(self):
        code = ("from dataclasses import dataclass\n@dataclass\nclass Item:\n    data: int\n    link: 'Item' = None\n"
                "head = Item('a', Item('b'))\n")
        head = self.final(code, next="link", value="data")
        self.assertEqual([n["value"] for n in head["nodes"]], ["'a'", "'b'"])

    def test_an_empty_list_has_no_head_and_no_nodes(self):
        self.assertEqual(self.final("head = None\n"), {"head": None, "nodes": [], "more": 0, "pointers": {}})

    def test_a_variable_that_is_not_a_node_is_left_out(self):
        frames = self.lists("head = 5\n")["frames"]
        self.assertTrue(all("head" not in f["lists"] for f in frames))

    def test_a_node_keeps_its_id_while_it_is_rewired(self):
        frames = self.lists("a = Node(1)\nb = Node(2)\nhead = a\na.next = b\nb.next = None\nhead = b\n")["frames"]
        recorded = [f["lists"]["head"] for f in frames if "head" in f["lists"]]
        first, linked, last = recorded[0], recorded[1], recorded[-1]
        a, b = linked["nodes"][0]["id"], linked["nodes"][1]["id"]
        self.assertEqual(self.chain(first), [("1", None)])
        self.assertEqual(first["nodes"][0]["id"], a)
        self.assertEqual(self.chain(linked), [("1", b), ("2", None)])
        self.assertEqual((last["head"], self.chain(last)), (b, [("2", None)]))

    def test_a_cycle_links_back_and_is_not_followed_again(self):
        head = self.final("head = Node(1, Node(2, Node(3)))\nhead.next.next.next = head.next\n")
        ids = [n["id"] for n in head["nodes"]]
        self.assertEqual(len(ids), 3)
        self.assertEqual(head["nodes"][2]["next"], ids[1])

    def test_the_nodes_are_capped_and_the_rest_counted(self):
        head = self.final("head = None\nfor i in range(50):\n    head = Node(i, head)\n")
        self.assertEqual((len(head["nodes"]), head["more"]), (tracer.MAX_NODES, 50 - tracer.MAX_NODES))

    def test_a_huge_cycle_free_list_is_walked_only_so_far(self):
        # Built by code exec() runs, which isn't traced, so the test records three steps rather than thousands.
        head = self.final("from types import SimpleNamespace as N\nhead = None\nexec('for i in range(3000): head = N(val=i, next=head)')\n")
        self.assertEqual(len(head["nodes"]) + head["more"], tracer.MAX_WALK)

    def test_pointers_name_the_node_they_hold_or_none(self):
        head = self.final("head = Node(1, Node(2))\ncurr = head.next\nprev = None\nn = 3\n", pointers=["curr", "prev", "n", "gone"])
        self.assertEqual(head["pointers"], {"curr": head["nodes"][1]["id"], "prev": None})

    def test_a_node_only_a_pointer_reaches_is_recorded_after_the_heads(self):
        head = self.final("head = Node(1)\nloose = Node(9, Node(8, head))\n", pointers=["loose"])
        ids = [n["id"] for n in head["nodes"]]
        self.assertEqual(self.chain(head), [("1", None), ("9", ids[2]), ("8", ids[0])])
        self.assertEqual(head["pointers"], {"loose": ids[1]})

    def test_a_node_without_the_value_attribute_has_no_value(self):
        head = self.final("class Bare:\n    next = None\nhead = Bare()\n")
        self.assertIsNone(head["nodes"][0]["value"])

    def test_a_link_to_something_that_is_not_a_node_ends_the_list(self):
        head = self.final("head = Node(1, 'oops')\n")
        self.assertEqual(self.chain(head), [("1", None)])

    def test_a_linked_list_inside_a_function_is_recorded(self):
        r = self.lists("def length(head):\n    n = 0\n    return n\nlength(Node(1))\n")
        inside = [f for f in r["frames"] if f["function"] == "length"]
        self.assertEqual(len(inside[0]["lists"]["head"]["nodes"]), 1)

    def test_plain_names_and_linked_lists_can_be_watched_together(self):
        r = trace(self.NODE + "nums = [1]\nhead = Node(1)\n", ["nums", {"variable": "head", "as": "linked_list"}])
        last = r["frames"][-1]
        self.assertEqual(last["tracked"]["nums"]["cells"], ["1"])
        self.assertEqual(len(last["lists"]["head"]["nodes"]), 1)


class Trees(unittest.TestCase):
    NODE = ("class T:\n    def __init__(self, val, left=None, right=None):\n"
            "        self.val = val\n        self.left = left\n        self.right = right\n")
    SMALL = "root = T(4, T(2, T(1), T(3)), T(6, None, T(7)))\n"

    def trees(self, code, pointers=(), **options):
        entry = dict({"variable": "root", "as": "tree", "pointers": list(pointers)}, **options)
        return trace(self.NODE + code, [entry])

    def final(self, code, pointers=(), **options):
        return self.trees(code, pointers, **options)["frames"][-1]["trees"]["root"]

    def shape(self, recorded):
        ids = {n["id"]: n["value"] for n in recorded["nodes"]}
        return [(n["value"], ids.get(n["left"]), ids.get(n["right"])) for n in recorded["nodes"]]

    def test_records_the_nodes_level_by_level_with_their_children(self):
        tree = self.final(self.SMALL)
        self.assertEqual(self.shape(tree), [("4", "2", "6"), ("2", "1", "3"), ("6", None, "7"),
                                            ("1", None, None), ("3", None, None), ("7", None, None)])
        self.assertEqual(tree["root"], tree["nodes"][0]["id"])
        self.assertEqual((tree["more"], tree["pointers"], tree["visited"]), (0, {}, None))

    def test_a_node_keeps_its_id_from_step_to_step(self):
        r = self.trees(self.SMALL + "root.left.val = 20\nroot.right.left = T(5)\n")
        recorded = [f["trees"]["root"] for f in r["frames"] if "root" in f["trees"]]
        before, after = recorded[0], recorded[-1]
        ids = lambda t: {n["value"]: n["id"] for n in t["nodes"]}
        self.assertEqual(ids(before)["4"], ids(after)["4"])
        self.assertEqual(ids(before)["2"], ids(after)["20"])
        self.assertIn(("6", "5", "7"), self.shape(after))

    def test_none_is_an_empty_tree_and_anything_else_is_not_recorded(self):
        r = self.trees("root = None\nroot = 5\nroot = T(1)\n")
        recorded = [f["trees"].get("root") for f in r["frames"] if f["function"] == "<module>"][-3:]
        self.assertEqual(recorded[0], {"root": None, "nodes": [], "more": 0, "pointers": {}, "visited": None})
        self.assertIsNone(recorded[1])
        self.assertEqual(recorded[2]["nodes"][0]["value"], "1")

    def test_custom_attribute_names(self):
        code = ("from dataclasses import dataclass\n@dataclass\nclass N:\n    key: int\n    lo: object = None\n    hi: object = None\n"
                "root = N(2, N(1), N(3))\n")
        tree = trace(code, [{"variable": "root", "as": "tree", "left": "lo", "right": "hi", "value": "key"}])["frames"][-1]["trees"]["root"]
        self.assertEqual(self.shape(tree), [("2", "1", "3"), ("1", None, None), ("3", None, None)])

    def test_pointers_hold_nodes_and_a_detached_subtree_comes_after_the_tree(self):
        tree = self.final(self.SMALL + "node = root.left\nloose = T(9, T(8))\nnothing = None\n", ["node", "loose", "nothing", "missing"])
        values = {n["id"]: n["value"] for n in tree["nodes"]}
        self.assertEqual(values[tree["pointers"]["node"]], "2")
        self.assertEqual(values[tree["pointers"]["loose"]], "9")
        self.assertIsNone(tree["pointers"]["nothing"])
        self.assertNotIn("missing", tree["pointers"])
        self.assertEqual([n["value"] for n in tree["nodes"]][-2:], ["9", "8"])

    def test_a_shared_node_or_a_cycle_is_recorded_once(self):
        tree = self.final("leaf = T(1)\nroot = T(2, leaf, leaf)\nroot.left.left = root\n")
        self.assertEqual([n["value"] for n in tree["nodes"]], ["2", "1"])
        self.assertEqual(tree["nodes"][1]["left"], tree["nodes"][0]["id"])

    def test_at_most_31_nodes_are_recorded_the_top_levels_first(self):
        # Nodes without an __init__ of their own, so building 63 of them stays well inside the step limit.
        code = ("from types import SimpleNamespace as N\nnodes = [N(val=i, left=None, right=None) for i in range(63)]\n"
                "for i in range(31): nodes[i].left, nodes[i].right = nodes[2 * i + 1], nodes[2 * i + 2]\nroot = nodes[0]\n")
        tree = self.final(code)
        self.assertEqual([n["value"] for n in tree["nodes"]], [str(i) for i in range(31)])
        self.assertEqual(tree["more"], 32)

    def test_a_visited_list_marks_nodes_in_order_by_value_or_by_node(self):
        code = self.SMALL + "order = [1, 2, 99]\norder.append(root)\n"
        tree = self.final(code, visited="order")
        values = {n["id"]: n["value"] for n in tree["nodes"]}
        self.assertTrue(tree["visited"]["ordered"])
        self.assertEqual([(values[v["id"]], v["order"]) for v in tree["visited"]["nodes"]], [("4", 4), ("2", 2), ("1", 1)])
        self.assertEqual(tree["visited"]["unmatched"], 1)

    def test_a_repeated_value_in_a_list_marks_the_next_node_with_it(self):
        tree = self.final("root = T(5, T(5), T(5))\norder = [5, 5]\n", visited="order")
        self.assertEqual([v["order"] for v in tree["visited"]["nodes"]], [1, 2])

    def test_a_visited_set_marks_every_match_with_no_order(self):
        tree = self.final("root = T(5, T(5), T(1))\nseen = {5}\n", visited="seen")
        self.assertEqual(tree["visited"], {"ordered": False, "nodes": [{"id": tree["nodes"][0]["id"], "order": None},
                                                                       {"id": tree["nodes"][1]["id"], "order": None}], "unmatched": 0})

    def test_a_visited_variable_that_is_not_a_list_or_set_marks_nothing(self):
        r = self.trees(self.SMALL + "order = 3\n", visited="order")
        self.assertTrue(all(f["trees"]["root"]["visited"] is None for f in r["frames"] if "root" in f["trees"]))

    def test_only_the_first_members_of_a_long_visited_list_are_matched(self):
        tree = self.final("root = T(1)\norder = list(range(500)) + [1]\n", visited="order")
        self.assertEqual(tree["visited"]["nodes"], [{"id": tree["root"], "order": 2}])
        self.assertEqual(tree["visited"]["unmatched"], 500)

    def test_values_whose_equality_raises_mark_nothing(self):
        code = ("class E:\n    def __eq__(self, other): raise ValueError\n    __hash__ = object.__hash__\n"
                "root = T(E())\norder = [1]\n")
        tree = self.final(code, visited="order")
        self.assertEqual(tree["visited"], {"ordered": True, "nodes": [], "unmatched": 1})


class Grids(unittest.TestCase):
    def grids(self, code, **options):
        entry = dict({"variable": "dp", "as": "grid"}, **options)
        return trace(code, [entry])

    def final(self, code, **options):
        return self.grids(code, **options)["frames"][-1]["grids"]["dp"]

    def test_records_the_rows_as_reprs(self):
        grid = self.final("dp = [[0, 1], [2, 'x', None]]\nend = 1\n")
        self.assertEqual(grid["rows"], [["0", "1"], ["2", "'x'", "None"]])
        self.assertEqual((grid["lengths"], grid["more"], grid["pointers"]), ([2, 3], 0, {}))
        self.assertEqual((grid["rowLabels"], grid["colLabels"], grid["highlight"]), (None, None, None))

    def test_a_string_row_gives_its_characters(self):
        grid = self.final("dp = ['ab', 'cd']\nend = 1\n")
        self.assertEqual(grid["rows"], [["a", "b"], ["c", "d"]])

    def test_an_empty_list_is_an_empty_grid(self):
        self.assertEqual(self.final("dp = []\nend = 1\n")["rows"], [])

    def test_a_flat_list_is_not_recorded(self):
        r = self.grids("dp = [1, 2, 3]\nend = 1\n")
        self.assertNotIn("dp", r["frames"][-1]["grids"])

    def test_cuts_rows_and_cells_and_counts_them(self):
        grid = self.final("dp = [list(range(20))] * 18\nend = 1\n")
        self.assertEqual((len(grid["rows"]), len(grid["rows"][0]), grid["lengths"][0], grid["more"]), (15, 15, 20, 3))

    def test_a_cell_pair_points_at_the_cell(self):
        grid = self.final("dp = [[0] * 3 for _ in range(3)]\ni, j = 1, 2\nend = 1\n", cells=[["i", "j"]])
        self.assertEqual(grid["pointers"], {"i,j": [1, 2]})

    def test_a_pair_that_is_not_ints_is_null_and_a_missing_one_is_left_out(self):
        grid = self.final("dp = [[0]]\ni, j = 0, 'x'\nend = 1\n", cells=[["i", "j"], ["i", "k"]])
        self.assertEqual(grid["pointers"], {"i,j": None})

    def test_a_pointer_holding_a_pair_or_none(self):
        grid = self.final("dp = [[0, 0]]\ncell = (0, 1)\ngone = None\nother = 'x'\nend = 1\n", pointers=["cell", "gone", "other"])
        self.assertEqual(grid["pointers"], {"cell": [0, 1], "gone": None})

    def test_labels_from_a_string_or_a_list(self):
        grid = self.final("dp = [[0]]\na = 'xyz'\nb = ['p', 2]\nend = 1\n", rowLabels="a", colLabels="b")
        self.assertEqual((grid["rowLabels"], grid["colLabels"]), (["x", "y", "z"], ["p", "2"]))

    def test_labels_of_another_type_are_null(self):
        grid = self.final("dp = [[0]]\na = 5\nend = 1\n", rowLabels="a")
        self.assertIsNone(grid["rowLabels"])

    def test_a_highlight_set_marks_its_cells_and_counts_the_rest(self):
        grid = self.final("dp = [[0, 0], [0, 0]]\nseen = {(1, 0), (0, 1), (5, 5), 'x'}\nend = 1\n", highlight="seen")
        self.assertEqual(grid["highlight"], {"cells": [[0, 1], [1, 0]], "unmatched": 2})

    def test_a_highlight_grid_of_flags_marks_the_true_cells(self):
        grid = self.final("dp = [[0, 0], [0, 0]]\nseen = [[True, False], [False, True]]\nend = 1\n", highlight="seen")
        self.assertEqual(grid["highlight"], {"cells": [[0, 0], [1, 1]], "unmatched": 0})

    def test_a_highlight_of_another_type_is_null(self):
        grid = self.final("dp = [[0]]\nseen = 3\nend = 1\n", highlight="seen")
        self.assertIsNone(grid["highlight"])


class Heaps(unittest.TestCase):
    def heaps(self, code, **options):
        entry = dict({"variable": "heap", "as": "heap"}, **options)
        return trace(code, [entry])

    def test_records_the_cells_of_a_list(self):
        r = self.heaps("heap = [3, 'x']\nend = 1\n")
        self.assertEqual(r["frames"][-1]["heaps"]["heap"], {"type": "list", "cells": ["3", "'x'"], "more": 0})

    def test_a_heappush_and_heappop_are_one_step_each(self):
        code = "import heapq\nheap = [2, 5]\nheapq.heappush(heap, 1)\nsmallest = heapq.heappop(heap)\n"
        r = self.heaps(code)
        cells = [f["heaps"]["heap"]["cells"] for f in r["frames"] if "heap" in f["heaps"]]
        self.assertEqual(cells, [["2", "5"], ["1", "5", "2"], ["2", "5"]])

    def test_anything_but_a_list_is_not_recorded(self):
        r = self.heaps("heap = (1, 2)\nend = 1\n")
        self.assertNotIn("heap", r["frames"][-1]["heaps"])

    def test_cuts_the_cells_at_five_levels_and_counts_the_rest(self):
        heap = self.heaps("heap = list(range(40))\nend = 1\n")["frames"][-1]["heaps"]["heap"]
        self.assertEqual((len(heap["cells"]), heap["more"]), (31, 9))

    def test_a_plain_name_entry_is_still_an_array(self):
        r = trace("heap = [1]\nend = 1\n", ["heap"])
        self.assertEqual((r["frames"][-1]["heaps"], r["frames"][-1]["tracked"]["heap"]["cells"]), ({}, ["1"]))


class Graphs(unittest.TestCase):
    def graphs(self, code, **options):
        entry = dict({"variable": "g", "as": "graph"}, **options)
        return trace(code, [entry])

    def final(self, code, **options):
        return self.graphs(code, **options)["frames"][-1]["graphs"]["g"]

    @staticmethod
    def labels(graph):
        return [n["label"] for n in graph["nodes"]]

    @staticmethod
    def edges(graph):
        label = {n["id"]: n["label"] for n in graph["nodes"]}
        return [(label[e["from"]], label[e["to"]], e["weight"]) for e in graph["edges"]]

    def test_records_the_keys_then_the_other_neighbours_and_the_edges(self):
        graph = self.final("g = {'A': ['B', 'C'], 'B': ['D']}\nend = 1\n")
        self.assertEqual(self.labels(graph), ["A", "B", "C", "D"])
        self.assertEqual(self.edges(graph), [("A", "B", None), ("A", "C", None), ("B", "D", None)])
        self.assertEqual((graph["more"], graph["moreEdges"], graph["pointers"], graph["visited"], graph["frontier"]), (0, 0, {}, None, None))

    def test_a_node_keeps_its_id_through_the_run(self):
        r = self.graphs("g = {1: [2]}\ng[3] = [1]\ng[2] = [3]\nend = 1\n")
        ids = [{n["label"]: n["id"] for n in f["graphs"]["g"]["nodes"]} for f in r["frames"] if "g" in f["graphs"]]
        self.assertEqual({len({frame[k] for frame in ids if k in frame}) for k in ("1", "2", "3")}, {1})

    def test_a_label_is_a_string_as_it_is_and_anything_else_its_repr(self):
        self.assertEqual(self.labels(self.final("g = {'x': [(0, 1)], 2: []}\nend = 1\n")), ["x", "2", "(0, 1)"])

    def test_a_defaultdict_and_sets_of_neighbours_in_sorted_order(self):
        code = "from collections import defaultdict\ng = defaultdict(set)\ng[1].update({3, 2})\nend = 1\n"
        self.assertEqual(self.edges(self.final(code)), [("1", "2", None), ("1", "3", None)])

    def test_without_weights_a_pair_is_a_node(self):
        graph = self.final("g = {(0, 0): [(0, 1)]}\nend = 1\n")
        self.assertEqual(self.edges(graph), [("(0, 0)", "(0, 1)", None)])

    def test_weights_from_pairs_or_a_dict(self):
        graph = self.final("g = {'A': [('B', 4)], 'B': {'C': 2.5}, 'C': ['A']}\nend = 1\n", weights=True)
        self.assertEqual(self.edges(graph), [("A", "B", "4"), ("B", "C", "2.5"), ("C", "A", None)])

    def test_a_dict_of_neighbours_is_weighted_without_the_flag(self):
        graph = self.final("g = {'A': {'B': 7}, 'B': {}, 'C': [('A', 1)]}\nend = 1\n")
        self.assertEqual(self.edges(graph), [("A", "B", "7"), ("C", "(\'A\', 1)", None)])

    def test_nodes_are_told_apart_by_value_as_keys_are(self):
        self.assertEqual(self.labels(self.final("g = {1: [1.0, True]}\nend = 1\n")), ["1"])

    def test_a_value_that_is_not_a_dict_is_not_recorded(self):
        self.assertNotIn("g", self.graphs("g = [[1]]\nend = 1\n")["frames"][-1]["graphs"])

    def test_cuts_nodes_and_edges_and_counts_them(self):
        graph = self.final("g = {i: [i + 100] for i in range(30)}\nend = 1\n")
        self.assertEqual((len(graph["nodes"]), graph["more"]), (20, 40))
        self.assertEqual((len(graph["edges"]), graph["moreEdges"]), (0, 30))
        graph = self.final("g = {0: list(range(1, 20)), 1: list(range(2, 20)) * 4}\nend = 1\n")
        self.assertEqual((len(graph["edges"]), graph["moreEdges"]), (60, 19 + 72 - 60))

    def test_a_huge_graph_is_looked_at_only_so_far(self):
        r = self.graphs("g = {0: list(range(10 ** 6))}\nend = 1\n")
        self.assertEqual(r["status"], "ok")
        self.assertEqual(r["frames"][-1]["graphs"]["g"]["moreEdges"], 999 - 20)

    def test_pointers_hold_a_node_or_none(self):
        graph = self.final("g = {'A': ['B']}\nnode, nb, gone, other = 'A', 'B', None, 'Z'\nend = 1\n", pointers=["node", "nb", "gone", "other", "unset"])
        self.assertEqual(graph["pointers"], {"node": 0 + graph["nodes"][0]["id"], "nb": graph["nodes"][1]["id"], "gone": None})

    def test_visited_and_frontier_mark_nodes_in_order(self):
        code = "from collections import deque\ng = {'A': ['B', 'C']}\nseen = {'C', 'A', 'Z'}\nq = deque(['C', 'B', 'C', 'Y'])\nend = 1\n"
        graph = self.final(code, visited="seen", frontier="q")
        ids = {n["label"]: n["id"] for n in graph["nodes"]}
        self.assertEqual(graph["visited"]["unmatched"], 1)
        self.assertEqual(sorted(graph["visited"]["nodes"]), sorted([ids["A"], ids["C"]]))
        self.assertEqual(graph["frontier"], {"nodes": [ids["C"], ids["B"]], "unmatched": 1})

    def test_a_frontier_that_is_a_set_is_not_recorded(self):
        self.assertIsNone(self.final("g = {1: []}\nq = {1}\nend = 1\n", frontier="q")["frontier"])


class CallStacks(unittest.TestCase):
    FACT = "def fact(n):\n    if n <= 1:\n        return 1\n    return n * fact(n - 1)\n\nresult = fact(3)\n"
    WATCH = [{"as": "call_stack"}]

    def stack(self, frame):
        return [(c["function"], [a["repr"] for a in c["args"]]) for c in frame["calls"]["frames"]]

    def test_records_the_active_calls_outermost_first_with_their_arguments(self):
        r = trace(self.FACT, self.WATCH)
        deepest = max(r["frames"], key=lambda f: len(f["calls"]["frames"]))
        self.assertEqual(self.stack(deepest), [("fact", ["3"]), ("fact", ["2"]), ("fact", ["1"])])
        self.assertEqual(deepest["calls"]["frames"][0]["args"], [{"name": "n", "repr": "3"}])
        self.assertEqual(r["frames"][0]["calls"], {"frames": [], "more": 0, "returned": None})

    def test_a_return_is_a_frame_with_the_value_and_the_next_frame_says_where_it_went(self):
        r = trace(self.FACT, self.WATCH)
        returns = [f for f in r["frames"] if f["event"] == "return" and f["function"] == "fact"]
        self.assertEqual([(f["line"], f["calls"]["frames"][-1]["returning"]) for f in returns], [(3, "1"), (4, "2"), (4, "6")])
        self.assertTrue(all(c["returning"] is None for f in returns for c in f["calls"]["frames"][:-1]))
        ids = [c["id"] for c in returns[0]["calls"]["frames"]]
        self.assertEqual(returns[1]["calls"]["returned"], {"function": "fact", "value": "1", "to": ids[1]})
        self.assertEqual(returns[2]["calls"]["returned"], {"function": "fact", "value": "2", "to": ids[0]})
        self.assertEqual(r["frames"][-1]["calls"]["returned"], {"function": "fact", "value": "6", "to": None})
        self.assertEqual(local(r["frames"][-1], "result")["repr"], "6")

    def test_returned_is_reported_once(self):
        r = trace(self.FACT, self.WATCH)
        self.assertEqual(sum(f["calls"]["returned"] is not None for f in r["frames"]), 3)

    def test_a_call_keeps_its_id_and_ids_are_never_reused(self):
        r = trace("def f(x):\n    return x\n\nf(1)\nf(2)\n", self.WATCH)
        ids = {}
        for frame in r["frames"]:
            for call in frame["calls"]["frames"]:
                ids.setdefault(call["id"], call["args"][0]["repr"])
                self.assertEqual(ids[call["id"]], call["args"][0]["repr"])
        self.assertEqual(sorted(ids.values()), ["1", "2"])

    def test_the_value_returns_to_the_caller_even_when_another_call_is_on_top(self):
        code = "def fib(n):\n    if n < 2:\n        return n\n    return fib(n - 1) + fib(n - 2)\n\nfib(2)\n"
        r = trace(code, self.WATCH)
        frame = next(f for f in r["frames"] if f["calls"]["returned"] and f["event"] == "line")
        outer = frame["calls"]["frames"][0]
        self.assertEqual(self.stack(frame), [("fib", ["2"]), ("fib", ["0"])])
        self.assertEqual(frame["calls"]["returned"], {"function": "fib", "value": "1", "to": outer["id"]})

    def test_functions_limits_the_calls_recorded(self):
        code = "def helper(x):\n    return x + 1\n\ndef main():\n    return helper(1)\n\nmain()\n"
        r = trace(code, [{"as": "call_stack", "functions": ["helper"]}])
        self.assertTrue(all(c["function"] == "helper" for f in r["frames"] for c in f["calls"]["frames"]))
        self.assertFalse(any(f["event"] == "return" and f["function"] == "main" for f in r["frames"]))
        returned = [f["calls"]["returned"] for f in r["frames"] if f["calls"]["returned"]]
        self.assertEqual(returned, [{"function": "helper", "value": "2", "to": None}])

    def test_deep_stacks_keep_the_innermost_calls_and_count_the_rest(self):
        code = "def down(n):\n    if n == 0:\n        return 0\n    return down(n - 1)\n\ndown(15)\n"
        r = trace(code, self.WATCH)
        deepest = max(r["frames"], key=lambda f: len(f["calls"]["frames"]) + f["calls"]["more"])
        self.assertEqual(len(deepest["calls"]["frames"]), tracer.MAX_CALLS)
        self.assertEqual(deepest["calls"]["more"], 16 - tracer.MAX_CALLS)
        self.assertEqual(deepest["calls"]["frames"][-1]["args"][0]["repr"], "0")

    def test_runaway_recursion_is_cut_at_the_frame_limit(self):
        r = trace("def f(n):\n    return f(n + 1)\n\nf(0)\n", self.WATCH, max_frames=50)
        self.assertEqual(r["status"], "truncated")
        self.assertEqual(len(r["frames"]), 50)

    def test_a_call_left_by_an_exception_has_no_return(self):
        code = "def boom():\n    raise ValueError\n\ntry:\n    boom()\nexcept ValueError:\n    pass\n"
        r = trace(code, self.WATCH)
        self.assertEqual(r["status"], "ok")
        self.assertFalse(any(f["event"] == "return" and f["function"] == "boom" for f in r["frames"]))
        self.assertFalse(any(f["calls"]["returned"] for f in r["frames"]))
        self.assertEqual(r["frames"][-1]["calls"]["frames"], [])

    def test_an_exception_caught_inside_the_call_still_returns(self):
        code = "def safe():\n    try:\n        raise ValueError\n    except ValueError:\n        return 0\n\nsafe()\n"
        r = trace(code, self.WATCH)
        self.assertEqual([f["calls"]["frames"][-1]["returning"] for f in r["frames"] if f["event"] == "return" and f["function"] == "safe"], ["0"])

    def returns_of(self, r, function):
        return [f["calls"]["frames"][-1]["returning"] for f in r["frames"] if f["event"] == "return" and f["function"] == function]

    def test_an_exception_passing_through_a_finally_is_still_no_return(self):
        code = ("def boom():\n    try:\n        raise ValueError('bad')\n    finally:\n        cleaned = True\n\n"
                "try:\n    boom()\nexcept ValueError:\n    pass\n")
        r = trace(code, self.WATCH)
        self.assertEqual(r["status"], "ok")
        self.assertIn(5, [f["line"] for f in r["frames"] if f["function"] == "boom"])   # the finally line ran
        self.assertEqual(self.returns_of(r, "boom"), [])
        self.assertFalse(any(f["calls"]["returned"] for f in r["frames"]))

    def test_a_return_in_a_finally_overrides_the_exception_and_is_a_return(self):
        code = "def swallow():\n    try:\n        raise ValueError\n    finally:\n        return 'kept'\n\nswallow()\n"
        r = trace(code, self.WATCH)
        self.assertEqual(self.returns_of(r, "swallow"), ["'kept'"])
        self.assertEqual(r["frames"][-1]["calls"]["returned"], {"function": "swallow", "value": "'kept'", "to": None})

    def test_falling_off_the_end_returns_none_even_after_a_caught_exception(self):
        code = "def quiet():\n    try:\n        raise ValueError\n    except ValueError:\n        pass\n\ndef plain():\n    x = 1\n\nquiet()\nplain()\n"
        r = trace(code, self.WATCH)
        self.assertEqual(self.returns_of(r, "quiet"), ["None"])
        self.assertEqual(self.returns_of(r, "plain"), ["None"])

    def test_an_exception_leaving_a_caller_too_returns_from_neither(self):
        code = ("def inner():\n    raise KeyError\n\ndef outer():\n    inner()\n    return 1\n\n"
                "try:\n    outer()\nexcept KeyError:\n    pass\n")
        r = trace(code, self.WATCH)
        self.assertEqual(self.returns_of(r, "inner") + self.returns_of(r, "outer"), [])
        self.assertEqual(r["frames"][-1]["calls"]["frames"], [])

    def test_methods_leave_out_self_and_generators_and_comprehensions_are_not_calls(self):
        code = ("class Box:\n    def put(self, x):\n        return x\n\ndef gen():\n    yield 1\n\n"
                "Box().put(5)\nlist(gen())\n[y for y in range(2)]\n")
        r = trace(code, self.WATCH)
        functions = {c["function"] for f in r["frames"] for c in f["calls"]["frames"]}
        self.assertEqual(functions, {"put"})
        put = next(c for f in r["frames"] for c in f["calls"]["frames"])
        self.assertEqual(put["args"], [{"name": "x", "repr": "5"}])

    def test_without_the_call_stack_nothing_is_added(self):
        r = trace(self.FACT)
        self.assertTrue(all("calls" not in f for f in r["frames"]))
        self.assertEqual([f["function"] for f in r["frames"] if f["event"] == "return"], ["<module>"])

    def test_the_call_stack_is_recorded_alongside_other_watches(self):
        code = "memo = {}\ndef f(n):\n    memo[n] = n\n    return n\n\nf(1)\n"
        r = trace(code, ["memo", {"as": "call_stack"}])
        frame = next(f for f in r["frames"] if f["calls"]["frames"] and "memo" in f["tables"])
        self.assertEqual(self.stack(frame), [("f", ["1"])])


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

    def test_a_dataclass_can_be_defined_and_the_module_is_not_left_behind(self):
        r = trace("from dataclasses import dataclass\n@dataclass\nclass P:\n    x: 'int' = 0\np = P(3)\n")
        self.assertEqual(r["status"], "ok")
        self.assertEqual(local(r["frames"][-1], "p")["repr"], "P(x=3)")
        self.assertNotIn("learner", __import__("sys").modules)

    def test_tracing_is_switched_off_afterwards(self):
        import sys
        before = sys.gettrace()
        trace("a = 1\n")
        trace("1/0\n")
        trace("while True: pass\n", max_frames=5)
        self.assertEqual(sys.gettrace(), before)


if __name__ == "__main__":
    unittest.main()
