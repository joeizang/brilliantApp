using Brilliant.Core.Code;
using Brilliant.Core.Content;
using Brilliant.Core.Drafts;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The file tabs of a multi-file C# write-code step (#152).</summary>
public class MultiFileWriteCodeTests : ShortcutContext
{
    private static readonly WriteCodeStep Step = new("step.m", "Shapes", "Total the areas.", "csharp", "public interface IShape {}", "TotalArea",
        [new CodeTest("3", "14")])
    {
        Files =
        [
            new CodeFile("IShape.cs", "public interface IShape {}"),
            new CodeFile("Square.cs", "public record Square(int Side) : IShape;"),
            new CodeFile("Program.cs", "int TotalArea(int n) => 0;"),
        ],
    };

    private sealed class MemoryDrafts : ICodeDraftStore
    {
        public Dictionary<string, string> Saved { get; } = [];
        public string? Get(string stepId) => Saved.GetValueOrDefault(stepId);
        public void Save(string stepId, string code) => Saved[stepId] = code;
        public void Reset(string stepId) => Saved.Remove(stepId);
    }

    private sealed class RecordingRuntime(TestRunResult result) : ICodeRuntime
    {
        public TestRunResult Result { get; set; } = result;
        public string Language => "csharp";
        public IReadOnlyList<CodeFile>? Files { get; private set; }
        public Task<TestRunResult> RunTestsAsync(string code, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken ct = default) =>
            throw new InvalidOperationException("A multi-file step runs all its files.");
        public Task<TestRunResult> RunTestsAsync(IReadOnlyList<CodeFile> files, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken ct = default)
        {
            Files = files;
            return Task.FromResult(Result);
        }
        public Task<TraceResult> TraceAsync(string code, IReadOnlyList<Visual> visuals, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private readonly MemoryDrafts _drafts = new();
    private RecordingRuntime _runtime = new(new TestRunResult(TestRunStatus.Passed, "", null, null, null, []));

    // The stubbed editor always reports "edited" as its text.
    private (IRenderedComponent<WriteCodeStepView> Cut, BunitJSModuleInterop Module) Open(WriteCodeStep? step = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/code-editor.js");
        module.Setup<int>("create", _ => true).SetResult(1);
        module.Setup<string>("getCode", _ => true).SetResult("edited");
        Services.AddSingleton<ICodeDraftStore>(_drafts);
        Services.AddSingleton<ICodeRuntime>(_runtime);
        var cut = Render<WriteCodeStepView>(p => p.Add(c => c.Step, step ?? Step));
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));
        return (cut, module);
    }

    private static void Type(BunitJSModuleInterop module, string code) =>
        ((DotNetObjectReference<CodeEditor>)module.Invocations["create"].Single().Arguments[2]!).Value.OnJsChanged(code);

    private static void OpenTab(IRenderedComponent<WriteCodeStepView> cut, string name) =>
        cut.FindAll("[role=tab]").Single(t => t.GetAttribute("data-file") == name).Click();

    private static string Selected(IRenderedComponent<WriteCodeStepView> cut) =>
        cut.FindAll("[role=tab]").Single(t => t.GetAttribute("aria-selected") == "true").GetAttribute("data-file")!;

    [Fact]
    public void Each_file_gets_a_tab_and_the_first_one_opens_with_its_starter()
    {
        var (cut, module) = Open();

        Assert.Equal(["IShape.cs", "Square.cs", "Program.cs"], cut.FindAll("[role=tab]").Select(t => t.GetAttribute("data-file")));
        Assert.Equal("IShape.cs", Selected(cut));
        Assert.Equal("Files", cut.Find("[role=tablist]").GetAttribute("aria-label"));
        Assert.Equal("public interface IShape {}", module.Invocations["create"].Single().Arguments[1]);
        Assert.Equal("csharp", module.Invocations["create"].Single().Arguments[4]);
    }

    [Fact]
    public void A_single_file_step_has_no_tabs()
    {
        var single = new WriteCodeStep("step.s", "Echo", "Write f", "csharp", "int F(int x) => x;", "F", [new CodeTest("1", "1")]);
        var (cut, _) = Open(single);

        Assert.Empty(cut.FindAll("[role=tablist]"));
        Assert.Empty(cut.FindAll("[role=tab]"));
    }

    [Fact]
    public void Opening_a_file_shows_its_saved_draft_or_its_starter()
    {
        _drafts.Saved["step.m/Square.cs"] = "my square";
        var (cut, module) = Open();

        OpenTab(cut, "Square.cs");
        cut.WaitForAssertion(() => Assert.Equal("my square", Assert.Single(module.Invocations["setCode"]).Arguments[1]));
        Assert.Equal("Square.cs", Selected(cut));

        OpenTab(cut, "Program.cs");
        cut.WaitForAssertion(() => Assert.Equal("int TotalArea(int n) => 0;", module.Invocations["setCode"].Last().Arguments[1]));
    }

    [Fact]
    public void Switching_saves_the_open_file_under_its_own_key_and_later_typing_goes_to_the_new_file()
    {
        var (cut, module) = Open();

        OpenTab(cut, "Square.cs");
        cut.WaitForAssertion(() => Assert.Equal("Square.cs", Selected(cut)));
        Assert.Equal("edited", _drafts.Saved["step.m/IShape.cs"]);   // what the editor held when the learner left it

        Type(module, "square v2");
        Assert.Equal("square v2", _drafts.Saved["step.m/Square.cs"]);
        Assert.Equal("edited", _drafts.Saved["step.m/IShape.cs"]);
        Assert.False(_drafts.Saved.ContainsKey("step.m"));           // there is no whole-step draft
    }

    [Fact]
    public void A_file_edited_back_to_its_starter_is_stored_as_no_draft()
    {
        var (cut, module) = Open();

        Type(module, "changed");
        Assert.True(_drafts.Saved.ContainsKey("step.m/IShape.cs"));
        Type(module, "public interface IShape {}");

        Assert.False(_drafts.Saved.ContainsKey("step.m/IShape.cs"));
    }

    [Fact]
    public void Running_sends_every_file_with_the_open_one_as_it_stands()
    {
        _drafts.Saved["step.m/Program.cs"] = "int TotalArea(int n) => n;";
        var (cut, _) = Open();

        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.NotNull(_runtime.Files));

        Assert.Equal(["IShape.cs", "Square.cs", "Program.cs"], _runtime.Files!.Select(f => f.Name));
        Assert.Equal("edited", _runtime.Files![0].Content);
        Assert.Equal("public record Square(int Side) : IShape;", _runtime.Files![1].Content);
        Assert.Equal("int TotalArea(int n) => n;", _runtime.Files![2].Content);
    }

    [Fact]
    public void The_submission_records_all_the_files_joined()
    {
        var (cut, _) = Open();
        WriteCodeStepView.Submission? submitted = null;
        cut.Render(p => p.Add(c => c.Step, Step).Add(c => c.OnSubmitted, (WriteCodeStepView.Submission s) => submitted = s));

        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.NotNull(submitted));

        Assert.Contains("// ===== Square.cs =====", submitted!.Code);
        Assert.Contains("// ===== Program.cs =====", submitted.Code);
    }

    [Fact]
    public void A_compile_error_in_another_file_opens_that_file_and_marks_its_tab()
    {
        _runtime = new RecordingRuntime(new TestRunResult(TestRunStatus.Error, "", "C# compilation failed in Square.cs, line 3.",
            "Square.cs(3,30): error CS0103: The name 'Sidee' does not exist", 3, []) { ErrorFile = "Square.cs" });
        var (cut, module) = Open();

        cut.Find("button.primary").Click();

        cut.WaitForAssertion(() => Assert.Equal("Square.cs", Selected(cut)));
        cut.WaitForAssertion(() => Assert.Contains(module.Invocations["highlightLine"], c => Equals(c.Arguments[1], 3)));
        Assert.Contains(module.Invocations["setErrorMarkers"], _ => true);
        var marked = cut.FindAll("[role=tab]").Single(t => t.QuerySelector(".file-error") is not null);
        Assert.Equal("Square.cs", marked.GetAttribute("data-file"));
        Assert.Contains("compilation failed in Square.cs, line 3", cut.Markup);
    }

    [Fact]
    public void Running_again_clears_the_error_mark()
    {
        _runtime = new RecordingRuntime(new TestRunResult(TestRunStatus.Error, "", "C# compilation failed in IShape.cs, line 1.",
            "IShape.cs(1,1): error CS1002", 1, []) { ErrorFile = "IShape.cs" });
        var (cut, _) = Open();
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".file-error")));

        _runtime.Result = new TestRunResult(TestRunStatus.Passed, "", null, null, null, []);
        cut.Find("button.primary").Click();

        cut.WaitForAssertion(() => Assert.Contains("All 0 tests passed", cut.Markup));
        Assert.Empty(cut.FindAll(".file-error"));
    }

    [Fact]
    public void Reset_restores_every_file_and_forgets_every_draft()
    {
        _drafts.Saved["step.m/IShape.cs"] = "a";
        _drafts.Saved["step.m/Square.cs"] = "b";
        _drafts.Saved["step.m/Program.cs"] = "c";
        var (cut, module) = Open();

        cut.Find("button.reset").Click();
        cut.FindAll(".reset-confirm button").Single(b => b.TextContent == "Yes, reset").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".reset-confirm")));

        Assert.Empty(_drafts.Saved);
        Assert.Equal("public interface IShape {}", module.Invocations["setCode"].Last().Arguments[1]);
        OpenTab(cut, "Square.cs");
        cut.WaitForAssertion(() => Assert.Equal("public record Square(int Side) : IShape;", module.Invocations["setCode"].Last().Arguments[1]));
    }
}
