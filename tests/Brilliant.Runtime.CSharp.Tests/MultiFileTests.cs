using Brilliant.Core.Code;
using Brilliant.Core.Content;

namespace Brilliant.Runtime.CSharp.Tests;

/// <summary>Multi-file C# exercises (#152): files compile together and diagnostics name the file and line.</summary>
public sealed class MultiFileTests
{
    private static readonly CodeFile Shape = new("IShape.cs", "public interface IShape\n{\n    int Area();\n}\n");
    private static readonly CodeFile Circle = new("Circle.cs", "public record Circle(int R) : IShape\n{\n    public int Area() => 3 * R * R;\n}\n");
    private static readonly CodeFile Program = new("Program.cs", "int TotalArea(int n)\n{\n    var shapes = new List<IShape>();\n    for (var i = 0; i < n; i++) shapes.Add(new Circle(2));\n    return shapes.Sum(s => s.Area());\n}\n");

    [Fact]
    public async Task Files_are_compiled_together_and_the_tests_run_against_them()
    {
        var result = await new CSharpRuntime().RunTestsAsync([Shape, Circle, Program], "TotalArea", [new CodeTest("2", "24"), new CodeTest("0", "0")]);
        Assert.True(result.Status == TestRunStatus.Passed, result.Message + result.Traceback);
    }

    [Fact]
    public async Task A_type_in_one_file_can_be_used_by_the_top_level_functions_of_another_whatever_the_order()
    {
        var result = await new CSharpRuntime().RunTestsAsync([Program, Circle, Shape], "TotalArea", [new CodeTest("1", "12")]);
        Assert.True(result.Status == TestRunStatus.Passed, result.Message + result.Traceback);
    }

    [Fact]
    public async Task A_compile_error_names_the_file_and_line_in_a_type_file()
    {
        var broken = Circle with { Content = "public record Circle(int R) : IShape\n{\n    public int Area() => missing;\n}\n" };
        var result = await new CSharpRuntime().RunTestsAsync([Shape, broken, Program], "TotalArea", [new CodeTest("1", "12")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Equal("Circle.cs", result.ErrorFile);
        Assert.Equal(3, result.ErrorLine);
        Assert.Contains("Circle.cs", result.Message);
        Assert.Contains("line 3", result.Message);
        Assert.Contains("Circle.cs(3", result.Traceback);
        Assert.Empty(result.Tests);
    }

    [Fact]
    public async Task A_compile_error_names_the_file_and_line_in_the_file_with_the_top_level_functions()
    {
        var broken = Program with { Content = "int TotalArea(int n)\n{\n    return nope;\n}\n" };
        var result = await new CSharpRuntime().RunTestsAsync([Shape, Circle, broken], "TotalArea", [new CodeTest("1", "12")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Equal(("Program.cs", 3), (result.ErrorFile, result.ErrorLine));
        Assert.Contains("Program.cs(3", result.Traceback);
    }

    [Fact]
    public async Task A_file_that_does_not_implement_the_interface_is_a_compile_error_in_that_file()
    {
        var lazy = new CodeFile("Circle.cs", "public record Circle(int R) : IShape;\n");
        var result = await new CSharpRuntime().RunTestsAsync([Shape, lazy, Program], "TotalArea", [new CodeTest("1", "12")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Equal("Circle.cs", result.ErrorFile);
    }

    [Fact]
    public async Task A_single_file_keeps_its_name_and_message()
    {
        var result = await new CSharpRuntime().RunTestsAsync("static int F()\n{\n return missing;\n}", "F", [new CodeTest("", "1")]);
        Assert.Null(result.ErrorFile);
        Assert.Equal("C# compilation failed.", result.Message);
        Assert.Contains("solution.cs(3", result.Traceback);
    }

    [Fact]
    public async Task Top_level_functions_in_two_files_are_refused_and_both_files_are_named()
    {
        var other = new CodeFile("Other.cs", "int G() => 1;\n");
        var result = await new CSharpRuntime().RunTestsAsync([Program, other], "TotalArea", [new CodeTest("1", "12")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Contains("Program.cs", result.Message);
        Assert.Contains("Other.cs", result.Message);
    }

    [Fact]
    public async Task A_missing_entrypoint_is_explained_in_terms_of_the_test_call()
    {
        var result = await new CSharpRuntime().RunTestsAsync([Shape, Circle], "TotalArea", [new CodeTest("1", "12")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Contains("Test 1 calls TotalArea(1)", result.Message);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("../Circle.cs")]
    [InlineData("test-1.cs")]
    [InlineData("reference.cs")]
    public async Task Bad_file_names_are_refused(string name)
    {
        var result = await new CSharpRuntime().RunTestsAsync([Shape, new CodeFile(name, "class A {}"), Program], "TotalArea", [new CodeTest("1", "12")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Contains(name, result.Message);
    }

    [Fact]
    public async Task Repeated_file_names_are_refused_ignoring_case()
    {
        var result = await new CSharpRuntime().RunTestsAsync([Shape, new CodeFile("ishape.cs", "class A {}"), Program], "TotalArea", [new CodeTest("1", "12")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Contains("twice", result.Message);
    }

    [Fact]
    public async Task Files_are_subject_to_the_same_probes_and_the_same_denied_apis_as_one()
    {
        var spin = new CodeFile("Spin.cs", "public static class Spin { public static int Forever() { while (true) { } } }\n");
        var caller = new CodeFile("Program.cs", "int F() => Spin.Forever();\n");
        var runtime = new CSharpRuntime(TimeSpan.FromMilliseconds(300));
        var timedOut = await runtime.RunTestsAsync([spin, caller], "F", [new CodeTest("", "1")]);
        Assert.True(timedOut.Status is TestRunStatus.TimedOut or TestRunStatus.Error, timedOut.Message);

        var denied = new CodeFile("Net.cs", "public static class Net { public static int Get() { System.Threading.Thread.Sleep(1); return 1; } }\n");
        var refused = await new CSharpRuntime().RunTestsAsync([denied, caller with { Content = "int F() => Net.Get();\n" }], "F", [new CodeTest("", "1")]);
        Assert.Equal(TestRunStatus.Error, refused.Status);
    }

    [Fact]
    public async Task Printing_from_a_type_file_is_captured_with_the_test()
    {
        var talker = new CodeFile("Talker.cs", "public static class Talker { public static int Say(int n) { Console.WriteLine(\"hi \" + n); return n; } }\n");
        var result = await new CSharpRuntime().RunTestsAsync([talker, new CodeFile("Program.cs", "int F(int n) => Talker.Say(n);\n")], "F", [new CodeTest("4", "4")]);
        Assert.True(result.Status == TestRunStatus.Passed, result.Message + result.Traceback);
        Assert.Contains("hi 4", result.Tests[0].Stdout);
    }

    [Fact]
    public async Task The_size_limit_counts_all_files()
    {
        var big = new CodeFile("Big.cs", "// " + new string('x', 40_000) + "\n");
        var result = await new CSharpRuntime().RunTestsAsync([big, big with { Name = "Big2.cs" }, Program], "TotalArea", [new CodeTest("1", "12")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Contains("64 KiB", result.Message);
    }

    [Fact]
    public async Task An_allocation_budget_applies_when_the_code_is_spread_over_files()
    {
        var holder = new CodeFile("Holder.cs", "public static class Holder { public static int[] Make() => new int[16]; }\n");
        var program = new CodeFile("Program.cs", "int F(int n) => Holder.Make().Length + n;\n");
        var result = await new CSharpRuntime { EnforceAllocationBudgets = true }.RunTestsAsync([holder, program], "F", [new CodeTest("0", "16", MaxAllocatedBytes: 0)]);
        Assert.Equal(TestRunStatus.Failed, result.Status);
        Assert.True(result.Tests[0].Allocation!.Exceeded);
    }

    [Fact]
    public async Task A_denied_api_in_a_type_file_names_that_file_and_line()
    {
        var reader = new CodeFile("Reader.cs", "public static class Reader\n{\n    public static string Read() => System.IO.File.ReadAllText(\"x\");\n}\n");
        var result = await new CSharpRuntime().RunTestsAsync([reader, new CodeFile("Program.cs", "string F() => Reader.Read();\n")], "F", [new CodeTest("", "\"\"")]);
        Assert.Equal(TestRunStatus.Error, result.Status);
        Assert.Contains("Reader.cs, line 3", result.Message);
    }
}
