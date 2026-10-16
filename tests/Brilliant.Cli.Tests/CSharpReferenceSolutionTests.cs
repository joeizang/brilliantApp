using Brilliant.Cli;
using Brilliant.Core.Code;
using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

public sealed class CSharpReferenceSolutionTests
{
    [Fact]
    public void Compiles_a_reference_function_and_compares_each_test()
    {
        var result = new CSharpReferenceSolutionRunner().Run("static int Add(int a, int b) => a + b;", "Add",
            [new CodeTest("1, 2", "3"), new CodeTest("4, 5", "10")]);
        Assert.Equal(TestRunStatus.Failed, result.Status);
        Assert.True(result.Tests[0].Passed);
        Assert.Equal("9", result.Tests[1].Actual);
        Assert.Equal("10", result.Tests[1].Expected);
    }
    [Fact]
    public void Compilation_errors_report_original_source_lines()
    {
        var result = new CSharpReferenceSolutionRunner().Run("static int Double(int n)\n{\n    return missing;\n}", "Double", [new CodeTest("4", "8")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Equal(3, result.ErrorLine);
        Assert.Contains("solution.cs(3", result.Traceback);
    }

    [Theory]
    [InlineData("static int F() { while(true) {} }", "step budget")]
    [InlineData("static int F() => F();", "call-depth limit")]
    [InlineData("static int F() { try { while(true){} } catch { return 1; } }", "step budget")]
    [InlineData("static int F() { try { return 1; } finally { while(true){} } }", "step budget")]
    [InlineData("static bool Filter() { for(int i=0;i<200000;i++){} return true; } static int F() { try { throw new Exception(); } catch(Exception) when(Filter()) { return 1; } catch { return 42; } }", "step budget")]
    public void Budget_failures_stop_reference_solutions(string code, string message)
    {
        var result = new CSharpReferenceSolutionRunner().Run(code, "F", [new CodeTest("", "1")]);
        Assert.Equal(TestRunStatus.TimedOut, result.Status);
        Assert.Contains(message, result.Message);
    }

    [Fact]
    public void A_blocking_library_call_is_stopped_by_the_process_watchdog()
    {
        var result = new CSharpReferenceSolutionRunner(TimeSpan.FromSeconds(2)).Run(
            "static int Wait() { System.Threading.Thread.Sleep(60000); return 1; }", "Wait", [new CodeTest("", "1")]);
        Assert.Equal(TestRunStatus.TimedOut, result.Status);
        Assert.Contains("worker exceeded", result.Message);
    }

    [Theory]
    [InlineData("static int[] F() => [1,2];", "new[] {1,2}")]
    [InlineData("static List<int> F() => [1,2];", "new List<int> {1,2}")]
    [InlineData("static Dictionary<string,int[]> F() => new() { [\"b\"] = [2], [\"a\"] = [1] };", "new Dictionary<string,int[]> { [\"a\"] = [1], [\"b\"] = [2] }")]
    [InlineData("static object F() => (1, new[] {2});", "(1, new[] {2})")]
    [InlineData("static Result F() => new([1,2]); record Result(int[] Values);", "new Result([1,2])")]
    [InlineData("static Result F() => new(); record Result { public int[] Values => new[] {1,2}; }", "new Result()")]
    public void Compiled_collection_record_and_tuple_results_compare_structurally(string code, string expected)
    {
        var result = new CSharpReferenceSolutionRunner().Run(code, "F", [new CodeTest("", expected)]);
        Assert.True(result.Status == TestRunStatus.Passed, result.Message + result.Traceback + string.Join(";", result.Tests));
    }
    [Fact]
    public void Source_local_preprocessor_flags_fail_explicitly_instead_of_changing_the_selected_branch()
    {
        var result = new CSharpReferenceSolutionRunner().Run("#define FLAG\nstatic int F() {\n#if FLAG\nreturn 1;\n#else\nreturn 2;\n#endif\n}", "F", [new CodeTest("", "1")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Contains("#define/#undef", result.Message);
    }
    [Fact]
    public void An_exception_during_result_formatting_cannot_turn_into_a_pass()
    {
        var result = new CSharpReferenceSolutionRunner().Run("var value = new R(); R F() => value; record R { public int Bad => throw new Exception(\"bad getter\"); }", "F", [new CodeTest("", "value")]);
        Assert.Equal(TestRunStatus.Failed, result.Status);
        Assert.False(Assert.Single(result.Tests).Passed);
        Assert.Contains("bad getter", result.Tests[0].Traceback);
    }
}
