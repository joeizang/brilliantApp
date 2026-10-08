using Brilliant.Cli;
using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

public sealed class ValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brilliant-tests-" + Guid.NewGuid().ToString("N"));

    public ValidatorTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void WriteValidPack(string lessonYaml = ValidLesson)
    {
        Write("pack.yaml", "id: pack.t\nversion: 1.0.0\n");
        Write("tracks/t/track.yaml", "id: track.t\ntitle: T\n");
        Write("tracks/t/l1/lesson.yaml", lessonYaml);
    }

    private const string ValidLesson = """
        id: lesson.one
        title: One
        steps:
          - id: step.one.intro
            type: explain
            title: Intro
            body: Hello
            comparison:
              csharp: var x = 1;
              python: x = 1
        """;

    [Fact]
    public void Valid_content_passes_and_packs_to_a_loadable_pack()
    {
        WriteValidPack();
        var outPath = Path.Combine(_root, "out", "p.zip");

        var report = ContentPacker.Pack(_root, outPath);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var graph = ContentPackFormat.Load(outPath);
        Assert.Equal("1.0.0", graph.Manifest.Version);
        Assert.Equal(["lesson.one"], graph.Get<Track>("track.t").LessonIds);
        Assert.Equal("track.t", graph.Get<Lesson>("lesson.one").TrackId);
    }

    [Fact]
    public void Missing_directory_is_reported()
    {
        var report = ContentValidator.Validate(Path.Combine(_root, "nope"));
        Assert.Contains(report.Errors, e => e.Message.Contains("does not exist"));
    }

    [Fact]
    public void Bad_id_is_reported_with_file_and_step()
    {
        WriteValidPack(ValidLesson.Replace("step.one.intro", "Step One"));
        var report = ContentValidator.Validate(_root);

        var error = Assert.Single(report.Errors);
        Assert.Equal("tracks/t/l1/lesson.yaml", error.File);
        Assert.Contains("steps[0]", error.Message);
        Assert.Contains("'Step One' is invalid", error.Message);
    }

    [Fact]
    public void Duplicate_ids_across_files_are_reported()
    {
        WriteValidPack();
        Write("tracks/t/l2/lesson.yaml", ValidLesson.Replace("lesson.one", "lesson.two"));

        var report = ContentValidator.Validate(_root);

        Assert.Contains(report.Errors, e => e.Message.Contains("duplicate id 'step.one.intro'"));
    }

    [Fact]
    public void Missing_required_fields_and_incomplete_comparison_are_reported()
    {
        WriteValidPack("""
            id: lesson.one
            title: One
            steps:
              - id: step.one.intro
                type: explain
                title: Intro
                comparison:
                  csharp: var x = 1;
            """);

        var messages = ContentValidator.Validate(_root).Errors.Select(e => e.Message).ToList();

        Assert.Contains(messages, m => m.Contains("'body' is required"));
        Assert.Contains(messages, m => m.Contains("comparison needs 'python'"));
    }

    [Fact]
    public void Unsupported_step_type_is_reported()
    {
        WriteValidPack(ValidLesson.Replace("type: explain", "type: quiz"));
        Assert.Contains(ContentValidator.Validate(_root).Errors, e => e.Message.Contains("unsupported step type 'quiz'"));
    }

    [Fact]
    public void Malformed_yaml_reports_file_and_line()
    {
        WriteValidPack("id: lesson.one\nsteps: [unclosed\n");
        var error = Assert.Single(ContentValidator.Validate(_root).Errors);
        Assert.Equal("tracks/t/l1/lesson.yaml", error.File);
        Assert.Contains("line", error.Message);
    }

    [Fact]
    public void Unknown_field_is_reported_rather_than_silently_ignored()
    {
        WriteValidPack(ValidLesson.Replace("title: Intro", "title: Intro\n    tittle: typo"));
        Assert.NotEmpty(ContentValidator.Validate(_root).Errors);
    }

    [Fact]
    public void Invalid_content_produces_no_pack()
    {
        WriteValidPack(ValidLesson.Replace("step.one.intro", "bad"));
        var outPath = Path.Combine(_root, "p.zip");

        var report = ContentPacker.Pack(_root, outPath);

        Assert.False(report.IsValid);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void Bad_version_is_reported()
    {
        WriteValidPack();
        Write("pack.yaml", "id: pack.t\nversion: one\n");
        Assert.Contains(ContentValidator.Validate(_root).Errors, e => e.File == "pack.yaml" && e.Message.Contains("version"));
    }
}
