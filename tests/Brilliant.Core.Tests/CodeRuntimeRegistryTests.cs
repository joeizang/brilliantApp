using Brilliant.Core.Code;
using Brilliant.Core.Content;

namespace Brilliant.Core.Tests;

public sealed class CodeRuntimeRegistryTests
{
    [Fact]
    public void Resolves_each_language_to_its_own_runtime()
    {
        var python = new Runtime("python");
        var csharp = new Runtime("csharp");
        var registry = new CodeRuntimeRegistry([python, csharp]);
        Assert.Same(python, registry.Resolve("python"));
        Assert.Same(csharp, registry.Resolve("csharp"));
    }

    [Fact]
    public void An_app_without_runtimes_reports_the_requested_language()
    {
        var registry = new CodeRuntimeRegistry([]);
        Assert.Contains("C#", Assert.Throws<InvalidOperationException>(() => registry.Resolve("csharp")).Message);
    }

    [Fact]
    public void Languages_are_exact_keys_and_never_fall_back_to_another_runtime()
    {
        var registry = new CodeRuntimeRegistry([new Runtime("python")]);
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("Python"));
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("csharp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_runtime_needs_a_language(string language) =>
        Assert.Throws<ArgumentException>(() => new CodeRuntimeRegistry([new Runtime(language)]));

    [Fact]
    public void Duplicate_language_registrations_are_rejected() =>
        Assert.Contains("python", Assert.Throws<ArgumentException>(() =>
            new CodeRuntimeRegistry([new Runtime("python"), new Runtime("python")])).Message);

    private sealed class Runtime(string language) : ICodeRuntime
    {
        public string Language => language;
        public Task<TestRunResult> RunTestsAsync(string code, string entrypoint, IReadOnlyList<CodeTest> tests, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TraceResult> TraceAsync(string code, IReadOnlyList<Visual> visuals, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
