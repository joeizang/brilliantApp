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
}
