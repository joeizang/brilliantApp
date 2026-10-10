using Brilliant.Core.Content;
using Brilliant.Core.Drafts;
using Brilliant.Core.Python;
using Brilliant.Lessons.UI;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Brilliant.Lessons.UI.Tests;

public class WriteCodeStepViewTests : BunitContext
{
    private static readonly WriteCodeStep Step = new("step.w", "Echo", "Write f", "python", "def f(x):\n    pass\n", "f",
        [new CodeTest("7", "7")]);

    private sealed class FakeRuntime(TestRunResult result) : IPythonRuntime
    {
        public Task<TestRunResult> RunTestsAsync(string code, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private sealed class MemoryDrafts : ICodeDraftStore
    {
        public Dictionary<string, string> Saved { get; } = [];
        public string? Get(string stepId) => Saved.GetValueOrDefault(stepId);
        public void Save(string stepId, string code) => Saved[stepId] = code;
        public void Reset(string stepId) => Saved.Remove(stepId);
    }

    private readonly MemoryDrafts _drafts = new();

    // Renders the step with a stubbed editor module, then returns it (the module is returned so tests can inspect JS calls).
    private (IRenderedComponent<WriteCodeStepView> Cut, BunitJSModuleInterop Module) Open()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/code-editor.js");
        module.Setup<int>("create", _ => true).SetResult(1);
        module.Setup<string>("getCode", _ => true).SetResult("def f(x):\n    return x\n");
        Services.AddSingleton<ICodeDraftStore>(_drafts);
        Services.AddSingleton<IPythonRuntime>(new FakeRuntime(new TestRunResult(TestRunStatus.Passed, "", null, null, null, [])));
        var cut = Render<WriteCodeStepView>(p => p.Add(c => c.Step, Step));
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));
        return (cut, module);
    }

    // What the editor reports when the learner types: the component hands JS a reference whose OnJsChanged it calls.
    private static void Type(BunitJSModuleInterop module, string code) =>
        ((DotNetObjectReference<CodeEditor>)module.Invocations["create"].Single().Arguments[2]!).Value.OnJsChanged(code);

    // Renders the step with a stubbed editor module and runtime, then clicks "Run tests".
    private IRenderedComponent<WriteCodeStepView> Run(TestRunResult result)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/code-editor.js");
        module.Setup<int>("create", _ => true).SetResult(1);
        module.Setup<string>("getCode", _ => true).SetResult("def f(x):\n    return x\n");
        Services.AddSingleton<ICodeDraftStore>(_drafts);
        Services.AddSingleton<IPythonRuntime>(new FakeRuntime(result));

        var cut = Render<WriteCodeStepView>(p => p.Add(c => c.Step, Step));
        cut.Find("button.primary").Click();
        cut.WaitForState(() => cut.FindAll(".verdict").Count > 0);
        return cut;
    }

    [Fact]
    public void A_passing_submission_still_shows_what_its_test_calls_printed()
    {
        var cut = Run(new TestRunResult(TestRunStatus.Passed, "", null, null, null,
            [new TestOutcome("f(7)", "7", "7", true, "debug 7\n", null, null)]));

        Assert.Contains("All 1 tests passed", cut.Find(".verdict").TextContent);
        Assert.Contains("Printed during f(7)", cut.Markup);
        Assert.Contains("debug 7", cut.Find(".output pre").TextContent);
    }

    [Fact]
    public void A_failing_submission_shows_load_time_output_and_output_from_every_test()
    {
        var cut = Run(new TestRunResult(TestRunStatus.Failed, "loading\n", null, null, null,
        [
            new TestOutcome("f(1)", "1", "1", true, "one\n", null, null),
            new TestOutcome("f(2)", "2", "3", false, "two\n", null, null),
        ]));

        var outputs = cut.FindAll(".output pre").Select(e => e.TextContent).ToList();
        Assert.Equal(["loading\n", "one\n", "two\n"], outputs);
        Assert.Contains("First failing test", cut.Markup);
    }

    [Fact]
    public void An_error_before_the_tests_shows_the_output_printed_so_far()
    {
        var cut = Run(new TestRunResult(TestRunStatus.Error, "partial\n", "Your code raised an error before the tests could run.",
            "ZeroDivisionError: division by zero", 2, []));

        Assert.Contains("Output before the error", cut.Markup);
        Assert.Contains("partial", cut.Find(".output pre").TextContent);
    }

    [Fact]
    public void Silent_code_shows_no_output_section()
    {
        var cut = Run(new TestRunResult(TestRunStatus.Passed, "", null, null, null,
            [new TestOutcome("f(7)", "7", "7", true, "", null, null)]));

        Assert.Empty(cut.FindAll(".output"));
    }

    [Fact]
    public void A_new_step_opens_with_the_starter_code()
    {
        var (_, module) = Open();
        Assert.Equal(Step.Starter, module.Invocations["create"].Single().Arguments[1]);
    }

    [Fact]
    public void A_saved_draft_is_restored_instead_of_the_starter()
    {
        _drafts.Saved[Step.Id] = "def f(x):\n    return x + 1\n";
        var (_, module) = Open();
        Assert.Equal("def f(x):\n    return x + 1\n", module.Invocations["create"].Single().Arguments[1]);
    }

    [Fact]
    public void Edits_are_saved_as_the_learner_types()
    {
        var (_, module) = Open();
        Type(module, "def f(x):\n    return 1\n");
        Assert.Equal("def f(x):\n    return 1\n", _drafts.Saved[Step.Id]);
    }

    [Fact]
    public void Typing_the_starter_back_in_leaves_no_draft()
    {
        _drafts.Saved[Step.Id] = "something else";
        var (_, module) = Open();
        Type(module, Step.Starter);
        Assert.False(_drafts.Saved.ContainsKey(Step.Id));
    }

    [Fact]
    public void Reset_asks_for_confirmation_and_keeping_the_code_changes_nothing()
    {
        _drafts.Saved[Step.Id] = "mine";
        var (cut, module) = Open();

        cut.Find("button.reset").Click();
        Assert.Contains("Your changes will be lost", cut.Find(".reset-confirm").TextContent);
        Assert.Empty(module.Invocations["setCode"]);

        cut.FindAll(".reset-confirm button").Single(b => b.TextContent == "Keep my code").Click();

        Assert.Equal("mine", _drafts.Saved[Step.Id]);
        Assert.Empty(module.Invocations["setCode"]);
        Assert.Empty(cut.FindAll(".reset-confirm"));
    }

    [Fact]
    public void Confirmed_reset_restores_the_starter_and_discards_the_draft()
    {
        _drafts.Saved[Step.Id] = "mine";
        var (cut, module) = Open();

        cut.Find("button.reset").Click();
        cut.FindAll(".reset-confirm button").Single(b => b.TextContent == "Yes, reset").Click();

        cut.WaitForAssertion(() => Assert.Equal(Step.Starter, Assert.Single(module.Invocations["setCode"]).Arguments[1]));
        Assert.False(_drafts.Saved.ContainsKey(Step.Id));
        Assert.Empty(cut.FindAll(".reset-confirm"));
    }

    [Fact]
    public void Reset_clears_the_previous_test_results()
    {
        var run = Run(new TestRunResult(TestRunStatus.Passed, "", null, null, null, [new TestOutcome("f(7)", "7", "7", true, "", null, null)]));
        Assert.NotEmpty(run.FindAll(".verdict"));

        run.Find("button.reset").Click();
        run.FindAll(".reset-confirm button").Single(b => b.TextContent == "Yes, reset").Click();

        run.WaitForAssertion(() => Assert.Empty(run.FindAll(".verdict")));
    }

    [Fact]
    public void An_autosave_arriving_while_a_reset_is_in_flight_does_not_bring_the_draft_back()
    {
        _drafts.Saved[Step.Id] = "mine";
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/code-editor.js");
        module.Setup<int>("create", _ => true).SetResult(1);
        var setCode = module.SetupVoid("setCode", _ => true);   // stays pending until we complete it
        Services.AddSingleton<ICodeDraftStore>(_drafts);
        Services.AddSingleton<IPythonRuntime>(new FakeRuntime(new TestRunResult(TestRunStatus.Passed, "", null, null, null, [])));
        var cut = Render<WriteCodeStepView>(p => p.Add(c => c.Step, Step));
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["create"]));

        cut.Find("button.reset").Click();
        cut.FindAll(".reset-confirm button").Single(b => b.TextContent == "Yes, reset").Click();
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["setCode"]));

        Type(module, "my draft");      // the old document's debounced callback lands mid-reset
        setCode.SetVoidResult();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".reset-confirm")));
        Assert.False(_drafts.Saved.ContainsKey(Step.Id));

        Type(module, "typed after the reset");   // normal autosave resumes afterwards
        Assert.Equal("typed after the reset", _drafts.Saved[Step.Id]);
    }
}
