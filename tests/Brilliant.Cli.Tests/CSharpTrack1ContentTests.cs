using Brilliant.Core.Content;
using Brilliant.Core.Review;

namespace Brilliant.Cli.Tests;

/// <summary>The shipped C# pack (#158): every lesson has enough review questions for "I know this — test me" (#146).</summary>
public sealed class CSharpTrack1ContentTests
{
    private static readonly Lazy<ContentGraph> Shipped = new(() =>
    {
        var manifest = FindManifest();
        var source = AppContentSource.FromManifest(manifest);
        var bytes = ContentPacker.PackToBytes(source.Root, out var report, appManifestPath: manifest);
        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        return ContentPackFormat.Load(new MemoryStream(bytes!));
    });

    [Fact]
    public void Every_csharp_lesson_can_be_tested_out_of()
    {
        var lessons = Shipped.Value.Lessons;
        Assert.True(lessons.Count >= 4);
        foreach (var lesson in lessons)
        {
            var asked = LessonTestOut.Select(lesson);
            Assert.True(asked.Count >= LessonTestOut.MinQuestions, $"{lesson.Id} offers {asked.Count} test-out questions");
        }
    }

    [Fact]
    public void Every_step_a_review_item_names_is_a_question_in_its_own_lesson()
    {
        foreach (var lesson in Shipped.Value.Lessons)
        {
            Assert.NotEmpty(lesson.ReviewItems);
            var steps = lesson.AllSteps().Select(s => s.Id).ToHashSet();
            Assert.All(lesson.ReviewItems, item => Assert.Contains(item.StepId, steps));
        }
    }

    private static string FindManifest()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "apps", "csharp", "manifest.yaml");
            if (File.Exists(path) && Directory.Exists(Path.Combine(directory.FullName, "content", "csharp"))) return path;
        }
        throw new FileNotFoundException("Could not find the repository's apps/csharp/manifest.yaml and content/csharp.");
    }
}
