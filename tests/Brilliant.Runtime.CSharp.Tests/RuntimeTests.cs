using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Brilliant.Runtime.CSharp;

namespace Brilliant.Runtime.CSharp.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public async Task A_submission_reports_passed_count_and_first_failing_test()
    {
        var runtime = new CSharpRuntime();
        var result = await runtime.RunTestsAsync("static int Add(int a, int b) => a + b;", "Add",
            [new CodeTest("1, 2", "3"), new CodeTest("4, 5", "10")]);
        Assert.True(result.Status == TestRunStatus.Failed, result.Message + result.Traceback);
        Assert.Equal(1, result.PassedCount);
        Assert.Equal("Add(4, 5)", result.FirstFailure!.Call);
        Assert.Equal("9", result.FirstFailure.Actual);
        Assert.Equal("10", result.FirstFailure.Expected);
    }
    [Fact]
    public async Task A_probe_free_blocking_call_returns_a_timeout_without_waiting_for_the_call()
    {
        var runtime = new CSharpRuntime(TimeSpan.FromMilliseconds(100)) { AllowBlockingCalls = true };
        var result = await runtime.RunTestsAsync("static int F() { System.Threading.Thread.Sleep(2000); return 1; }", "F", [new CodeTest("", "1")]);
        Assert.True(result.Status == TestRunStatus.TimedOut, result.Message + result.Traceback);
        Assert.Contains("test 1", result.Message);
    }
    [Fact]
    public async Task Printed_output_is_captured_per_test_without_replacing_process_console()
    {
        var console = Console.Out;
        var result = await new CSharpRuntime().RunTestsAsync("static int F(int n) { Console.WriteLine(n); Console.Error.Write(\"error\"); return n; }", "F", [new CodeTest("3", "3"), new CodeTest("5", "5")]);
        Assert.Equal(TestRunStatus.Passed, result.Status);
        Assert.Equal("3" + Environment.NewLine + "error", result.Tests[0].Stdout);
        Assert.Equal("5" + Environment.NewLine + "error", result.Tests[1].Stdout);
        Assert.Same(console, Console.Out);
    }
    [Fact]
    public async Task Compilation_errors_identify_the_original_line_without_failed_tests()
    {
        var result = await new CSharpRuntime().RunTestsAsync("static int F()\n{\n return missing;\n}", "F", [new CodeTest("", "1")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Equal(3, result.ErrorLine);
        Assert.Contains("solution.cs(3", result.Traceback);
        Assert.Empty(result.Tests);
    }

    [Theory]
    [InlineData("static int[] F() => [1,2];", "new[] {1,2}")]
    [InlineData("static List<int> F() => [1,2];", "new List<int> {1,2}")]
    [InlineData("static object F() => (1, new[] {2});", "(1, new[] {2})")]
    [InlineData("static R F() => new([1,2]); record R(int[] Items);", "new R([1,2])")]
    public async Task Results_compare_structurally(string code, string expected)
    {
        var result = await new CSharpRuntime().RunTestsAsync(code, "F", [new CodeTest("", expected)]);
        Assert.True(result.Status == TestRunStatus.Passed, result.Message + result.Traceback);
    }

    [Theory]
    [InlineData("static int F() { while (true) {} }", "step budget")]
    [InlineData("static int F() => F();", "call-depth")]
    [InlineData("static int F() { try { while(true){} } catch { return 1; } }", "step budget")]
    [InlineData("static int F() { try { return 1; } finally { while(true){} } }", "step budget")]
    public async Task Runaway_submissions_stop_and_a_corrected_submission_can_run(string code, string message)
    {
        var runtime = new CSharpRuntime();
        var result = await runtime.RunTestsAsync(code, "F", [new CodeTest("", "1")]);
        Assert.True(result.Status == TestRunStatus.TimedOut, result.Message + result.Traceback);
        Assert.Contains(message, result.Message);
        Assert.Equal(TestRunStatus.Passed, (await runtime.RunTestsAsync("static int F() => 1;", "F", [new CodeTest("", "1")])).Status);
    }

    [Fact]
    public async Task Native_lifetime_limit_counts_loads_including_repeat_submissions_and_saves_restart_for_next_run()
    {
        var runtime = new CSharpRuntime();
        for (var i = 0; i < CSharpRuntime.AssemblyLoadLimit; i++)
            Assert.Equal(TestRunStatus.Passed, (await runtime.RunTestsAsync("static int F() => 1;", "F", [new CodeTest("", "1")])).Status);
        Assert.Equal(50, runtime.Diagnostics.AssemblyLoads);
        Assert.True(runtime.Diagnostics.RequiresRestart);
        Assert.Contains("restart", (await runtime.RunTestsAsync("static int F() => 1;", "F", [new CodeTest("", "1")])).Message);
        Assert.Equal(50, runtime.Diagnostics.AssemblyLoads);
        Assert.True(runtime.Diagnostics.WorkingSetBytes > 0);
    }

    [Fact]
    public async Task Coreclr_collects_completed_submission_contexts()
    {
        var runtime = new CSharpRuntime();
        await RunAndForget(runtime);
        for (var i = 0; i < 10 && runtime.Diagnostics.AliveContexts != 0; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(30);
        }
        Assert.Equal(1, runtime.Diagnostics.AssemblyLoads);
        Assert.Equal(0, runtime.Diagnostics.AliveContexts);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task RunAndForget(CSharpRuntime runtime) =>
        Assert.Equal(TestRunStatus.Passed, (await runtime.RunTestsAsync("static int F() => 1;", "F", [new CodeTest("", "1")])).Status);
    [Fact]
    public async Task Session_rejects_control_of_the_host_probe_budget()
    {
        var result = await new CSharpRuntime().RunTestsAsync("static int F() { Brilliant.Code.CSharp.ProbeBudget.Current = null; return 1; }", "F", [new CodeTest("", "1")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Contains("not available", result.Message);
    }
    [Fact]
    public async Task Cancelled_blocking_execution_requires_restart_and_prevents_more_loads()
    {
        var runtime = new CSharpRuntime(TimeSpan.FromMilliseconds(50)) { AllowBlockingCalls = true };
        var result = await runtime.RunTestsAsync("static int F() { System.Threading.Thread.Sleep(1000); return 1; }", "F", [new CodeTest("", "1")]);
        Assert.True(result.Status == TestRunStatus.TimedOut, result.Message + result.Traceback);
        Assert.True(runtime.Diagnostics.RequiresRestart);
        Assert.Contains("restart", (await runtime.RunTestsAsync("static int F() => 1;", "F", [new CodeTest("", "1")])).Message);
        Assert.Equal(1, runtime.Diagnostics.AssemblyLoads);
    }

    [Theory]
    [InlineData("static int F() { System.Threading.Thread.Sleep(5000); return 1; }")]
    [InlineData("static int F() { Action<int> exit = Environment.Exit; return 1; }")]
    [InlineData("static int F() { System.Threading.Tasks.Task.Delay(10).Wait(); return 1; }")]
    [InlineData("static int F() { object x = 1; return x.GetType().GetHashCode(); }")]
    [InlineData("static int F() { dynamic x = 1; return x; }")]
    [InlineData("static int F() { return System.IO.File.ReadAllText(\"a\").Length; }")]
    public async Task Unsupported_host_apis_fail_before_loading(string code)
    {
        var runtime = new CSharpRuntime();
        var result = await runtime.RunTestsAsync(code, "F", [new CodeTest("", "1")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Contains("not available", result.Message);
        Assert.Equal(0, runtime.Diagnostics.AssemblyLoads);
    }

    [Fact]
    public async Task Compiled_submission_cache_is_bounded_and_does_not_hide_actual_loads()
    {
        var runtime = new CSharpRuntime();
        for (var i = 0; i < 12; i++)
            await runtime.RunTestsAsync($"static int F() => {i};", "F", [new CodeTest("", i.ToString())]);
        Assert.Equal(8, runtime.Diagnostics.CachedSubmissions);
        Assert.Equal(12, runtime.Diagnostics.AssemblyLoads);
    }
    [Fact]
    public async Task Exception_diagnostics_never_execute_a_learner_override()
    {
        var result = await new CSharpRuntime().RunTestsAsync("static int F() => throw new Bad(); class Bad : Exception { public override string ToString() => throw new Exception(\"format crash\"); public override string Message => throw new Exception(\"message crash\"); }", "F", [new CodeTest("", "1")]);
        Assert.True(result.Status == TestRunStatus.Failed, result.Message + result.Traceback);
        Assert.Contains("Bad", result.FirstFailure!.Traceback);
        Assert.DoesNotContain("crash", result.FirstFailure.Traceback);
    }

    [Fact]
    public async Task Runtime_namespace_cannot_be_shadowed_to_disable_probes()
    {
        var result = await new CSharpRuntime().RunTestsAsync("static int F() => 1; namespace Brilliant.Code.CSharp { public class ProbeBudget { public static ProbeBudget Current => new(); public void Enter(){} public void Exit(){} public void Tick(){} } }", "F", [new CodeTest("", "1")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Contains("reserved", result.Message);
    }

    [Fact]
    public async Task Excessive_printing_is_bounded_and_does_not_poison_the_next_run()
    {
        var runtime = new CSharpRuntime();
        var result = await runtime.RunTestsAsync("static int F() { Console.Write(new string('x', 20000)); return 1; }", "F", [new CodeTest("", "1")]);
        Assert.True(result.Status == TestRunStatus.Error, result.Message + result.Traceback);
        Assert.Contains("output", result.Message);
        Assert.False(runtime.Diagnostics.RequiresRestart);
    }

    [Fact]
    public async Task A_signature_that_does_not_match_the_test_call_explains_the_test_without_a_learner_line()
    {
        var result = await new CSharpRuntime().RunTestsAsync("static int AddTwo(int a, int b)\n{\n    return a + b;\n}", "AddTwo", [new CodeTest("1", "3")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Null(result.ErrorLine);
        Assert.Contains("Test 1 calls AddTwo(1)", result.Message);
        Assert.DoesNotContain("test-1.cs", result.Traceback);
        Assert.Contains("CS7036", result.Traceback);
    }

    [Fact]
    public async Task A_learner_error_alongside_a_test_call_error_keeps_the_learner_line()
    {
        var result = await new CSharpRuntime().RunTestsAsync("static int F(int a, int b)\n{\n    return missing;\n}", "F", [new CodeTest("1", "1")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Equal(3, result.ErrorLine);
        Assert.Equal("C# compilation failed.", result.Message);
    }

    private const string Allocates = "static int Sum(int n) { var scratch = new int[16]; scratch[0] = n; return scratch[0]; }";

    [Fact]
    public async Task A_zero_allocation_method_passes_a_zero_byte_budget()
    {
        var runtime = new CSharpRuntime();
        var result = await runtime.RunTestsAsync("static int Double(int n) => n * 2;", "Double", [new CodeTest("4", "8", null, 0)]);

        Assert.True(result.Status == TestRunStatus.Passed, result.Message + result.Traceback);
        var allocation = Assert.IsType<AllocationReport>(Assert.Single(result.Tests).Allocation);
        Assert.Equal(0, allocation.Bytes);
        Assert.False(allocation.Exceeded);
    }

    [Fact]
    public async Task An_allocating_method_fails_a_zero_byte_budget_and_reports_the_measured_bytes()
    {
        var runtime = new CSharpRuntime();
        var result = await runtime.RunTestsAsync(Allocates, "Sum", [new CodeTest("4", "4", null, 0)]);

        Assert.Equal(TestRunStatus.Failed, result.Status);
        var failure = result.FirstFailure!;
        Assert.False(failure.Passed);
        Assert.Equal("4", failure.Actual); // the answer was right; the allocation is what failed
        var allocation = failure.Allocation!;
        Assert.True(allocation.Enforced);
        Assert.Equal("CoreCLR", allocation.Runtime);
        Assert.InRange(allocation.Bytes!.Value, 64, 128); // an int[16] is 88 bytes on CoreCLR
    }

    [Fact]
    public async Task A_budget_at_the_measured_bytes_passes()
    {
        var runtime = new CSharpRuntime();
        var measured = (await runtime.RunTestsAsync(Allocates, "Sum", [new CodeTest("4", "4", null, 0)])).FirstFailure!.Allocation!.Bytes!.Value;

        var result = await runtime.RunTestsAsync(Allocates, "Sum", [new CodeTest("4", "4", null, (int)measured)]);

        Assert.Equal(TestRunStatus.Passed, result.Status);
    }

    [Fact]
    public async Task A_one_off_allocation_in_the_first_call_is_not_counted()
    {
        // The first call builds a lookup table; every later call allocates nothing.
        var runtime = new CSharpRuntime();
        var result = await runtime.RunTestsAsync("static int Lookup(int n) { Cache.Table ??= new int[64]; return Cache.Table.Length + n; }\nstatic class Cache { public static int[]? Table; }", "Lookup",
            [new CodeTest("1", "65", null, 0)]);

        Assert.True(result.Status == TestRunStatus.Passed, result.Message + result.Traceback);
    }

    [Fact]
    public async Task A_measurement_from_a_non_authoritative_runtime_is_advice_and_never_fails_the_test()
    {
        var runtime = new CSharpRuntime { EnforceAllocationBudgets = false, AllocationRuntime = "Mono interpreter" };
        var result = await runtime.RunTestsAsync(Allocates, "Sum", [new CodeTest("4", "4", null, 0)]);

        Assert.Equal(TestRunStatus.Passed, result.Status);
        var allocation = Assert.Single(result.Tests).Allocation!;
        Assert.False(allocation.Enforced);
        Assert.True(allocation.Exceeded);
        Assert.Equal("Mono interpreter", allocation.Runtime);
    }

    [Fact]
    public async Task A_test_without_a_budget_is_not_measured()
    {
        var result = await new CSharpRuntime().RunTestsAsync(Allocates, "Sum", [new CodeTest("4", "4")]);

        Assert.Equal(TestRunStatus.Passed, result.Status);
        Assert.Null(Assert.Single(result.Tests).Allocation);
    }

    [Fact]
    public async Task A_wrong_answer_is_reported_as_a_wrong_answer_and_not_measured()
    {
        var result = await new CSharpRuntime().RunTestsAsync(Allocates, "Sum", [new CodeTest("4", "5", null, 0)]);

        Assert.Equal(TestRunStatus.Failed, result.Status);
        Assert.Null(result.FirstFailure!.Allocation);
        Assert.Equal("4", result.FirstFailure.Actual);
    }

    [Fact]
    public async Task A_negative_budget_is_an_error()
    {
        var result = await new CSharpRuntime().RunTestsAsync("static int F() => 1;", "F", [new CodeTest("", "1", null, -1)]);

        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Contains("negative", result.Message);
    }

    [Fact]
    public async Task Printing_while_measuring_does_not_hit_the_output_limit()
    {
        var result = await new CSharpRuntime().RunTestsAsync("static int F() { Console.WriteLine(\"hello\"); return 1; }", "F", [new CodeTest("", "1", null, 100_000)]);

        Assert.True(result.Status == TestRunStatus.Passed, result.Message + result.Traceback);
        Assert.Equal("hello\n", Assert.Single(result.Tests).Stdout.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_call_too_slow_to_repeat_is_reported_as_not_measured_and_never_times_out_a_passing_run(bool enforce)
    {
        // The checked call takes about 40% of the test timeout; repeating it would overrun the watchdog.
        var runtime = new CSharpRuntime(TimeSpan.FromSeconds(1)) { AllowBlockingCalls = true, EnforceAllocationBudgets = enforce, AllocationRuntime = "Mono interpreter" };
        var result = await runtime.RunTestsAsync("static int F() { System.Threading.Thread.Sleep(400); return 1; }", "F", [new CodeTest("", "1", null, 1_000_000)]);

        Assert.True(result.Status == TestRunStatus.Passed, result.Message + result.Traceback);
        var allocation = Assert.Single(result.Tests).Allocation!;
        Assert.False(allocation.Measured);
        Assert.Null(allocation.Bytes);
        Assert.False(runtime.Diagnostics.RequiresRestart);
    }
}
