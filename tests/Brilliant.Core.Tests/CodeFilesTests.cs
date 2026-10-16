using System.Text.Json;
using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Brilliant.Core.Drafts;
using Brilliant.Core.Sync;

namespace Brilliant.Core.Tests;

/// <summary>What every part of the system shares about a multi-file exercise's files (#152).</summary>
public sealed class CodeFilesTests
{
    private static CodeFile F(string name) => new(name, "class X {}");

    [Fact]
    public void Between_one_and_eight_files_with_plain_distinct_names_are_fine()
    {
        Assert.Null(CodeFiles.Problem([F("A.cs"), F("b_2.cs"), F("_C.cs")]));
        Assert.Null(CodeFiles.Problem(Enumerable.Range(0, 8).Select(i => F($"F{i}.cs")).ToList()));
    }

    [Fact]
    public void Nine_files_or_none_are_too_many_or_too_few()
    {
        Assert.NotNull(CodeFiles.Problem(Enumerable.Range(0, 9).Select(i => F($"F{i}.cs")).ToList()));
        Assert.NotNull(CodeFiles.Problem([]));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("Circle")]
    [InlineData("../Circle.cs")]
    [InlineData("sub/Circle.cs")]
    [InlineData("1Circle.cs")]
    [InlineData("Circle .cs")]
    [InlineData("")]
    [InlineData("reference.cs")]
    [InlineData("Reference.CS")]
    [InlineData("test-12.cs")]
    public void Names_that_are_not_plain_csharp_files_or_belong_to_the_harness_are_refused(string name) =>
        Assert.NotNull(CodeFiles.NameProblem(name));

    [Fact]
    public void A_name_is_limited_in_length()
    {
        Assert.Null(CodeFiles.NameProblem(new string('a', CodeFiles.MaxNameLength - 3) + ".cs"));
        Assert.NotNull(CodeFiles.NameProblem(new string('a', CodeFiles.MaxNameLength) + ".cs"));
    }

    [Fact]
    public void Repeated_names_are_refused_ignoring_case()
    {
        Assert.Contains("used twice", CodeFiles.Problem([F("A.cs"), F("a.cs")]));
    }

    [Fact]
    public void Draft_keys_never_collide_between_steps_or_files_and_fit_the_sync_limit()
    {
        Assert.Equal("step.a/Circle.cs", CodeFiles.DraftKey("step.a", "Circle.cs"));
        Assert.NotEqual(CodeFiles.DraftKey("step.a", "B.cs"), CodeFiles.DraftKey("step.a", "C.cs"));

        // The longest step id the publisher accepts, with the longest file name, is still a key the server takes.
        var key = CodeFiles.DraftKey(new string('s', 64 - CodeFiles.MaxNameLength), new string('f', CodeFiles.MaxNameLength - 3) + ".cs");
        Assert.Null(DraftLimits.Problem(new CodeDraft(key, "code", DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public void Combine_joins_files_under_their_names_and_leaves_a_single_file_alone()
    {
        Assert.Equal("int F() => 1;", CodeFiles.Combine([new CodeFile("solution.cs", "int F() => 1;")]));

        var combined = CodeFiles.Combine([new CodeFile("A.cs", "a"), new CodeFile("B.cs", "b")]);

        Assert.Equal("// ===== A.cs =====\na\n// ===== B.cs =====\nb", combined);
    }

    [Fact]
    public void A_multi_file_step_survives_the_pack_json_and_a_single_file_step_writes_no_files()
    {
        var multi = new WriteCodeStep("step.m", "T", "P", "csharp", "a", "F", [new CodeTest("1", "1")])
        {
            Files = [new CodeFile("A.cs", "a"), new CodeFile("B.cs", "b")],
        };
        var json = JsonSerializer.Serialize<Step>(multi, ContentPackFormat.JsonOptions);
        var back = Assert.IsType<WriteCodeStep>(JsonSerializer.Deserialize<Step>(json, ContentPackFormat.JsonOptions));

        Assert.True(back.IsMultiFile);
        Assert.Equal([("A.cs", "a"), ("B.cs", "b")], back.Files!.Select(f => (f.Name, f.Content)));

        var single = new WriteCodeStep("step.s", "T", "P", "csharp", "a", "F", [new CodeTest("1", "1")]);
        Assert.DoesNotContain("files", JsonSerializer.Serialize<Step>(single, ContentPackFormat.JsonOptions));
        Assert.False(single.IsMultiFile);
    }

    [Fact]
    public async Task A_runtime_without_multi_file_support_refuses_several_files_and_runs_one()
    {
        var impl = new OneFileRuntime();
        ICodeRuntime runtime = impl;   // the multi-file overload is a default interface method

        var refused = await runtime.RunTestsAsync(new[] { F("A.cs"), F("B.cs") }, "F", []);
        Assert.Equal(TestRunStatus.Error, refused.Status);
        Assert.Contains("single file", refused.Message);
        Assert.Null(impl.Code);

        await runtime.RunTestsAsync(new[] { new CodeFile("solution.cs", "the code") }, "F", []);
        Assert.Equal("the code", impl.Code);
    }

    private sealed class OneFileRuntime : ICodeRuntime
    {
        public string Language => "python";
        public string? Code { get; private set; }
        public Task<TestRunResult> RunTestsAsync(string code, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken ct = default)
        {
            Code = code;
            return Task.FromResult(new TestRunResult(TestRunStatus.Passed, "", null, null, null, []));
        }
        public Task<TraceResult> TraceAsync(string code, IReadOnlyList<Visual> visuals, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
