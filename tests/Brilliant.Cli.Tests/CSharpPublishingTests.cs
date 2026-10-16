using System.IO.Compression;
using Brilliant.Core.Code;
using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

public sealed class CSharpPublishingTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-csharp-publishing-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_repository, "content", "sample");
    private const string LessonFile = "tracks/one/double/lesson.yaml";

    private void WritePack(string solution = "static int Double(int n) => n * 2;", bool includePython = false)
    {
        Write("apps/sample/manifest.yaml", $$"""
            appId: example.csharp.publishing
            displayName: C# publishing checks
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [{{(includePython ? "python, csharp" : "csharp")}}]
            stepTypes: [write-code, fill-blank]
            """);
        Write("content/sample/pack.yaml", "id: pack.csharp.sample\nversion: 1.0.0\ntargetFramework: net10.0\nlangVersion: '14'\n");
        Write("content/sample/tracks/one/track.yaml", "id: track.csharp.sample\ntitle: C# samples\n");
        var indentedSolution = string.Join('\n', solution.Split('\n').Select(line => "      " + line));
        var lesson = """
            id: lesson.csharp.double
            title: Double a number
            steps:
              - id: step.csharp.double
                type: write-code
                title: Double
                prompt: Return twice the argument.
                language: csharp
                largeScreenPreferred: true
                entrypoint: Double
                starter: 'static int Double(int n) => 0;'
                solution: |
            """ + "\n" + indentedSolution + "\n" + """
                tests:
                  - input: '4'
                    expected: '8'
                  - input: '-3'
                    expected: '-6'
                phoneAlternative:
                  id: step.csharp.double.fill
                  type: fill-blank
                  title: Complete Double
                  prompt: Finish Double.
                  language: csharp
                  template: 'static int Double(int n) => n * {{factor}};'
                  blanks:
                    - id: factor
                      accepted: ['2']
            """ + "\n";
        if (includePython)
            lesson += """
              - id: step.python.double
                type: write-code
                title: Double in Python
                prompt: Return twice the argument.
                language: python
                entrypoint: double
                starter: 'def double(n): return 0'
                solution: 'def double(n): return n * 2'
                tests:
                  - input: '4'
                    expected: '8'
            """ + "\n";
        Write("content/sample/" + LessonFile, lesson);
    }

    [Fact]
    public void The_default_gate_compiles_csharp_without_sending_it_to_cpython()
    {
        // A C# local function cannot pass CPython's parser; no injected runner supplies this result.
        WritePack();
        var report = ContentValidator.Load(Root, out var content);

        Assert.True(report.IsValid, string.Join('\n', report.Errors));
        Assert.NotNull(content);
        Assert.Equal("net10.0", content.Manifest.TargetFramework);
        Assert.Equal("14", content.Manifest.LangVersion);
        Assert.Equal("csharp", Assert.IsType<WriteCodeStep>(Assert.Single(content.Lessons[0].Steps)).Language);
    }

    [Fact]
    public void A_csharp_write_code_step_without_a_phone_alternative_is_rejected_because_android_cannot_run_it()
    {
        WritePack();
        var file = Path.Combine(Root, LessonFile);
        var text = File.ReadAllText(file);
        File.WriteAllText(file, text[..text.IndexOf("    phoneAlternative:", StringComparison.Ordinal)]);

        var report = ContentValidator.Load(Root, out _);

        var error = Assert.Single(report.Errors);
        Assert.Contains("phoneAlternative", error.Message);
        Assert.Contains("Android", error.Message);
    }

    [Fact]
    public void A_wrong_csharp_reference_reports_the_lesson_step_test_expected_and_actual()
    {
        WritePack("static int Double(int n) => n * 3;");
        var output = Path.Combine(_repository, "wrong.zip");

        var report = ContentPacker.Pack(Root, output);

        Assert.False(report.IsValid);
        Assert.False(File.Exists(output));
        var error = Assert.Single(report.Errors);
        Assert.EndsWith(LessonFile, error.File);
        Assert.Contains("step.csharp.double", error.Message);
        Assert.Contains("test 1: Double(4)", error.Message);
        Assert.Contains("expected: 8", error.Message);
        Assert.Contains("actual:   12", error.Message);
    }

    [Fact]
    public void A_csharp_compile_error_keeps_its_solution_line_and_blocks_packing()
    {
        WritePack("static int Double(int n)\n{\n    return missing;\n}");
        var output = Path.Combine(_repository, "compile-error.zip");

        var report = ContentPacker.Pack(Root, output);

        Assert.False(report.IsValid);
        Assert.False(File.Exists(output));
        var error = Assert.Single(report.Errors);
        Assert.EndsWith(LessonFile, error.File);
        Assert.Contains("step.csharp.double", error.Message);
        Assert.Contains("C# compilation failed", error.Message);
        Assert.Contains("solution.cs(3,", error.Message);
        Assert.Contains("CS0103", error.Message);
        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public void A_validated_csharp_reference_is_not_serialized_into_the_pack()
    {
        WritePack();
        var output = Path.Combine(_repository, "validated.zip");

        var report = ContentPacker.Pack(Root, output);

        Assert.True(report.IsValid, string.Join('\n', report.Errors));
        Assert.Equal("pack.csharp.sample", ContentPackFormat.Load(output).Manifest.PackId);
        using var zip = ZipFile.OpenRead(output);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain("n * 2", reader.ReadToEnd());
        }
    }

    [Fact]
    public void The_default_gate_dispatches_both_languages_within_one_pack()
    {
        WritePack(includePython: true);

        var report = ContentValidator.Load(Root, out var content);

        Assert.True(report.IsValid, string.Join('\n', report.Errors));
        Assert.Equal(["csharp", "python"], content!.Lessons[0].Steps.Cast<WriteCodeStep>().Select(step => step.Language));
    }

    [Fact]
    public void An_explicit_python_runner_is_authoritative_and_never_receives_csharp()
    {
        WritePack();
        var runner = new PythonOnlyRunner();
        var output = Path.Combine(_repository, "wrong-runner.zip");

        var report = ContentPacker.Pack(Root, output, runner);

        Assert.Contains(report.Errors, error => error.Message.Contains("no reference/trace checker is available for 'csharp'"));
        Assert.Equal(0, runner.Calls);
        Assert.False(File.Exists(output));
    }

    /// <summary>Adds the budget to the first (or, for the Python step, the last) test whose expected value is 8.</summary>
    private void DeclareBudget(string budget, bool last = false)
    {
        var file = Path.Combine(Root, LessonFile);
        var lines = File.ReadAllLines(file).ToList();
        var at = last ? lines.FindLastIndex(l => l.Trim() == "expected: '8'") : lines.FindIndex(l => l.Trim() == "expected: '8'");
        lines.Insert(at + 1, new string(' ', lines[at].Length - lines[at].TrimStart().Length) + "maxAllocatedBytes: " + budget);
        File.WriteAllLines(file, lines);
    }

    [Fact]
    public void A_zero_allocation_reference_passes_its_budget_and_the_budget_is_packed_with_the_test()
    {
        WritePack();
        DeclareBudget("0");
        var output = Path.Combine(_repository, "budget.zip");

        var report = ContentPacker.Pack(Root, output);

        Assert.True(report.IsValid, string.Join('\n', report.Errors));
        var step = Assert.IsType<WriteCodeStep>(Assert.Single(ContentPackFormat.Load(output).Lessons[0].Steps));
        Assert.Equal([0, (int?)null], step.Tests.Select(t => t.MaxAllocatedBytes));
    }

    [Fact]
    public void A_reference_that_allocates_over_its_budget_blocks_packing_and_shows_the_measured_bytes()
    {
        WritePack("static int Double(int n) { var scratch = new int[16]; scratch[0] = n * 2; return scratch[0]; }");
        DeclareBudget("0");
        var output = Path.Combine(_repository, "allocating.zip");

        var report = ContentPacker.Pack(Root, output);

        Assert.False(report.IsValid);
        Assert.False(File.Exists(output));
        var error = Assert.Single(report.Errors);
        Assert.Contains("step.csharp.double", error.Message);
        Assert.Contains("test 1: Double(4)", error.Message);
        Assert.Matches(@"allocated [1-9][\d,]* bytes per call; the budget is 0 \(CoreCLR\)", error.Message);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1000001")]
    public void An_allocation_budget_outside_zero_to_a_million_bytes_is_rejected(string budget)
    {
        WritePack();
        DeclareBudget(budget);

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains("maxAllocatedBytes") && e.Message.Contains("between 0 and 1,000,000"));
    }

    [Fact]
    public void A_python_test_cannot_declare_an_allocation_budget()
    {
        WritePack(includePython: true);
        DeclareBudget("0", last: true);

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains("step.python.double") && e.Message.Contains("maxAllocatedBytes"));
    }

    private sealed class PythonOnlyRunner : IReferenceSolutionRunner
    {
        public string Language => "python";
        public int Calls { get; private set; }
        public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests)
        {
            Calls++;
            throw new InvalidOperationException("C# must not be dispatched to this Python runner.");
        }
        public TraceResult Trace(string code, IReadOnlyList<Visual> visuals) => throw new InvalidOperationException("No trace steps in this fixture.");
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repository)) Directory.Delete(_repository, recursive: true);
    }
}
