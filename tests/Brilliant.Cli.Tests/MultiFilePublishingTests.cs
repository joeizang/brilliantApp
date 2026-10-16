using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

/// <summary>Multi-file C# write-code exercises (#152): the validator checks the files and the reference solution is compiled from all of them.</summary>
public sealed class MultiFilePublishingTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-multifile-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_repository, "content", "sample");
    private const string LessonFile = "tracks/one/area/lesson.yaml";

    private const string Shape = """
                      - name: IShape.cs
                        starter: |
                          public interface IShape
                          {
                              int Area();
                          }
                        solution: |
                          public interface IShape
                          {
                              int Area();
                          }
        """;

    private const string Square = """
                      - name: Square.cs
                        starter: |
                          public record Square(int Side) : IShape
                          {
                              public int Area() => 0;
                          }
                        solution: |
                          public record Square(int Side) : IShape
                          {
                              public int Area() => Side * Side;
                          }
        """;

    private const string Program = """
                      - name: Program.cs
                        starter: |
                          int TotalArea(int n) => 0;
                        solution: |
                          int TotalArea(int n)
                          {
                              var shapes = new List<IShape>();
                              for (var i = 1; i <= n; i++) shapes.Add(new Square(i));
                              return shapes.Sum(s => s.Area());
                          }
        """;

    private void WritePack(string filesYaml, string extra = "", string language = "csharp", string type = "write-code")
    {
        Write("apps/sample/manifest.yaml", """
            appId: example.csharp.multifile
            displayName: Multi-file checks
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [csharp]
            stepTypes: [write-code, fill-blank]
            """.Replace("__BLANK__", "{{side}}"));
        Write("content/sample/pack.yaml", "id: pack.csharp.multifile\nversion: 1.0.0\ntargetFramework: net10.0\nlangVersion: '14'\n");
        Write("content/sample/tracks/one/track.yaml", "id: track.csharp.multifile\ntitle: C# samples\n");
        Write("content/sample/" + LessonFile, $$"""
            id: lesson.csharp.area
            title: Total area
            steps:
              - id: step.csharp.area
                type: {{type}}
                title: Total area
                prompt: Sum the areas of squares with sides 1 to n.
                language: {{language}}
                largeScreenPreferred: true
                entrypoint: TotalArea
            {{extra}}
                files:
            {{filesYaml}}
                tests:
                  - input: '3'
                    expected: '14'
                  - input: '0'
                    expected: '0'
                phoneAlternative:
                  id: step.csharp.area.fill
                  type: fill-blank
                  title: Complete Square
                  prompt: Finish Area.
                  language: csharp
                  template: 'public int Area() => Side * __BLANK__;'
                  blanks:
                    - id: side
                      accepted: ['Side']
            """.Replace("__BLANK__", "{{side}}"));
    }

    private static string Files(params string[] files) => string.Join('\n', files);

    [Fact]
    public void A_multi_file_step_is_checked_by_compiling_all_its_solution_files_together_and_packed_with_its_starter_files()
    {
        WritePack(Files(Shape, Square, Program));
        var output = Path.Combine(_repository, "files.zip");

        var report = ContentPacker.Pack(Root, output);

        Assert.True(report.IsValid, string.Join('\n', report.Errors));
        var step = Assert.IsType<WriteCodeStep>(Assert.Single(ContentPackFormat.Load(output).Lessons[0].Steps));
        Assert.True(step.IsMultiFile);
        Assert.Equal(["IShape.cs", "Square.cs", "Program.cs"], step.Files!.Select(f => f.Name));
        Assert.Contains("=> 0;", step.Files![1].Content);           // the starter, not the solution
        Assert.DoesNotContain("Side * Side", string.Concat(step.Files!.Select(f => f.Content)));
        Assert.Equal(step.Files[0].Content, step.Starter);
    }

    [Fact]
    public void A_wrong_solution_file_blocks_packing_and_shows_expected_and_actual()
    {
        WritePack(Files(Shape, Square.Replace("Side * Side", "Side + Side"), Program));

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "wrong.zip"));

        var error = Assert.Single(report.Errors);
        Assert.Contains("step.csharp.area", error.Message);
        Assert.Contains("test 1: TotalArea(3)", error.Message);
        Assert.Contains("expected: 14", error.Message);
    }

    [Fact]
    public void A_compile_error_in_a_solution_file_names_that_file_and_line()
    {
        WritePack(Files(Shape, Square.Replace("Side * Side", "Sidee * Side"), Program));

        var report = ContentPacker.Pack(Root, Path.Combine(_repository, "broken.zip"));

        var error = Assert.Single(report.Errors);
        Assert.Contains("C# compilation failed in Square.cs, line 3", error.Message);
        Assert.Contains("Square.cs(3,", error.Message);
        Assert.Contains("CS0103", error.Message);
    }

    [Fact]
    public void Files_can_not_be_combined_with_the_steps_own_starter_or_solution()
    {
        WritePack(Files(Shape, Square, Program), extra: "    starter: 'int TotalArea(int n) => 0;'\n    solution: 'int TotalArea(int n) => 0;'");

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains("remove the step's own 'starter' and 'solution'"));
    }

    [Fact]
    public void One_file_is_not_a_multi_file_exercise()
    {
        WritePack(Files(Program));

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains("between 2 and 8 files"));
    }

    [Fact]
    public void More_than_eight_files_are_rejected()
    {
        WritePack(Files(Enumerable.Range(0, 9).Select(i => $"              - name: F{i}.cs\n                solution: 'class F{i} {{}}'").ToArray()));

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains("between 2 and 8 files"));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("..Square.cs")]
    [InlineData("test-1.cs")]
    [InlineData("reference.cs")]
    [InlineData("usings.cs")]
    public void Bad_file_names_are_rejected(string name)
    {
        WritePack(Files(Shape, Square.Replace("Square.cs", name), Program));

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains(name) && e.Message.Contains("files[1]"));
    }

    [Fact]
    public void Repeated_file_names_are_rejected_ignoring_case()
    {
        WritePack(Files(Shape, Square, Program.Replace("Program.cs", "square.cs")));

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains("used twice"));
    }

    [Fact]
    public void A_file_without_a_solution_is_rejected()
    {
        WritePack(Files(Shape, "              - name: Square.cs\n                starter: 'record Square(int Side);'", Program));

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains("files[1]") && e.Message.Contains("needs a 'solution'"));
    }

    [Fact]
    public void Only_csharp_has_several_files()
    {
        WritePack(Files(Shape, Square, Program), language: "python");

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains("only C# exercises can have several files"));
    }

    [Fact]
    public void A_multi_file_step_can_not_have_a_walkthrough()
    {
        WritePack(Files(Shape, Square, Program), extra: "    walkthrough:\n      call: TotalArea(2)\n      explanation: Sum them.");

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains("can't have a 'walkthrough'"));
    }

    [Fact]
    public void Files_only_apply_to_write_code_steps()
    {
        WritePack(Files(Shape, Square, Program), type: "choice");

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains("'files' only applies to write-code steps"));
    }

    [Fact]
    public void A_step_id_too_long_for_its_draft_keys_is_rejected()
    {
        WritePack(Files(Shape, Square, Program));
        var file = Path.Combine(Root, LessonFile);
        File.WriteAllText(file, File.ReadAllText(file).Replace("id: step.csharp.area\n", "id: step.csharp.area." + new string('x', 50) + "\n"));

        var report = ContentValidator.Load(Root, out _);

        Assert.Contains(report.Errors, e => e.Message.Contains("too long for a draft to sync"));
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
