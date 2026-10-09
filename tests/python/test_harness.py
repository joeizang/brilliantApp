"""Checks the write-code test harness under plain CPython (the same file runs inside Pyodide in the app).

Run:  python3 -I -m unittest discover -s tests/python
"""
import importlib.util
import json
import pathlib
import unittest

_path = pathlib.Path(__file__).resolve().parents[2] / "src/Brilliant.App/wwwroot/python/harness.py"
_spec = importlib.util.spec_from_file_location("harness", _path)
harness = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(harness)


def run(code, entry="f", tests=(("1", "1"),)):
    return json.loads(harness.run_tests(code, entry, json.dumps([{"input": i, "expected": e} for i, e in tests])))


class PassingAndFailing(unittest.TestCase):
    def test_all_tests_pass(self):
        r = run("def f(x):\n    return x * 2\n", tests=[("1", "2"), ("5", "10")])
        self.assertEqual(r["status"], "passed")
        self.assertTrue(all(t["passed"] for t in r["tests"]))

    def test_reports_call_expected_and_actual_of_each_test(self):
        r = run("def f(x):\n    return x + 1\n", tests=[("1", "2"), ("[3, 4][0]", "9")])
        self.assertEqual(r["status"], "failed")
        bad = next(t for t in r["tests"] if not t["passed"])
        self.assertEqual((bad["call"], bad["expected"], bad["actual"]), ("f([3, 4][0])", "9", "4"))

    def test_runs_every_test_even_after_a_failure(self):
        r = run("def f(x):\n    return 0\n", tests=[("1", "1"), ("2", "0"), ("3", "0")])
        self.assertEqual([t["passed"] for t in r["tests"]], [False, True, True])

    def test_multiple_arguments_and_keywords(self):
        r = run("def f(a, b=2):\n    return a * b\n", tests=[("3, 4", "12"), ("3", "6"), ("3, b=5", "15")])
        self.assertEqual(r["status"], "passed")

    def test_tuple_and_list_results_are_compared_by_value(self):
        r = run("def f(xs):\n    return min(xs), max(xs)\n", tests=[("[3, 1, 2]", "(1, 3)")])
        self.assertEqual(r["status"], "passed")
        r = run("def f(xs):\n    return [min(xs), max(xs)]\n", tests=[("[3, 1, 2]", "(1, 3)")])
        self.assertEqual(r["status"], "failed")

    def test_each_test_gets_fresh_arguments(self):
        r = run("def f(xs):\n    xs.append(0)\n    return len(xs)\n", tests=[("[1]", "2"), ("[1]", "2")])
        self.assertEqual(r["status"], "passed")


class Output(unittest.TestCase):
    def test_print_output_is_captured_per_test(self):
        r = run("def f(x):\n    print('hi', x)\n    return x\n", tests=[("7", "7")])
        self.assertEqual(r["tests"][0]["stdout"], "hi 7\n")

    def test_top_level_prints_are_captured_once(self):
        r = run("print('loading')\ndef f(x):\n    return x\n")
        self.assertEqual(r["stdout"], "loading\n")

    def test_output_is_cut_off_when_huge(self):
        r = run("def f(x):\n    print('a' * 100000)\n    return x\n")
        self.assertLess(len(r["tests"][0]["stdout"]), 5000)
        self.assertIn("cut off", r["tests"][0]["stdout"])

    def test_main_guard_does_not_run(self):
        r = run("def f(x):\n    return x\nif __name__ == '__main__':\n    print('should not run')\n")
        self.assertEqual(r["stdout"], "")


class Errors(unittest.TestCase):
    def test_syntax_error_reports_line_and_message(self):
        r = run("def f(x)\n    return x\n")
        self.assertEqual(r["status"], "error")
        self.assertEqual(r["errorLine"], 1)
        self.assertIn("SyntaxError", r["traceback"])

    def test_indentation_error_is_a_syntax_error(self):
        r = run("def f(x):\nreturn x\n")
        self.assertEqual(r["status"], "error")
        self.assertEqual(r["errorLine"], 2)

    def test_exception_while_loading_the_code(self):
        r = run("x = 1 / 0\ndef f(a):\n    return a\n")
        self.assertEqual(r["status"], "error")
        self.assertEqual(r["errorLine"], 1)
        self.assertIn("ZeroDivisionError", r["traceback"])

    def test_missing_function(self):
        r = run("def g(x):\n    return x\n", entry="f")
        self.assertEqual(r["status"], "error")
        self.assertIn("`f`", r["message"])

    def test_entrypoint_that_is_not_callable(self):
        self.assertEqual(run("f = 3\n")["status"], "error")

    def test_runtime_error_in_a_test_shows_only_learner_frames(self):
        r = run("def helper(x):\n    return x / 0\n\ndef f(x):\n    return helper(x)\n")
        t = r["tests"][0]
        self.assertFalse(t["passed"])
        self.assertIsNone(t["actual"])
        self.assertEqual(t["errorLine"], 2)
        self.assertIn('File "<your code>", line 2, in helper', t["traceback"])
        self.assertIn("return x / 0", t["traceback"])         # source lines are shown
        self.assertNotIn("harness", t["traceback"])
        self.assertTrue(t["traceback"].endswith("ZeroDivisionError: division by zero"))

    def test_exit_does_not_kill_the_runner(self):
        r = run("def f(x):\n    raise SystemExit(1)\n")
        self.assertEqual(r["status"], "failed")

    def test_deep_recursion_traceback_is_shortened(self):
        r = run("def f(x):\n    return f(x)\n")
        t = r["tests"][0]
        self.assertIn("RecursionError", t["traceback"])
        self.assertLess(t["traceback"].count('File "<your code>"'), 15)
        self.assertIn("earlier calls not shown", t["traceback"])

    def test_broken_test_expectation_is_reported_as_an_authoring_error(self):
        r = run("def f(x):\n    return x\n", tests=[("1", "undefined_name")])
        self.assertEqual(r["status"], "error")
        self.assertIn("broken test", r["message"])

    def test_learner_cannot_change_what_is_expected(self):
        r = run("list = 5\ndef f(x):\n    return x\n", tests=[("2", "list((2,))[0]")])
        self.assertEqual(r["status"], "passed")


if __name__ == "__main__":
    unittest.main()
