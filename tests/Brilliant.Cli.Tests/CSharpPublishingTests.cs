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
            stepTypes: [write-code]
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
                entrypoint: Double
                starter: 'static int Double(int n) => 0;'
                solution: |
            """ + "\n" + indentedSolution + "\n" + """
                tests:
                  - input: '4'
                    expected: '8'
                  - input: '-3'
                    expected: '-6'
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
