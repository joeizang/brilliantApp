using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>Content that changes after a lesson was finished: the lesson stays complete and the new steps are offered.</summary>
public class NewStepsTests : ShortcutContext
{
    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static ExplainStep Explain(string id) => new(id, "Title " + id, "Body", [], null);

    private static Lesson Lesson(params string[] ids) => new("lesson.l", "Lesson", "track.t", ids.Select(Explain).Cast<Step>().ToList());

    private static ContentGraph Graph(Lesson lesson) => new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [lesson.Id])], [lesson]);

    private readonly MemoryLog _log = new();

    private ProgressRecorder Finish(Lesson original)
    {
        var recorder = new ProgressRecorder(_log, "device");
        Services.AddSingleton(recorder);
        foreach (var s in original.Steps) recorder.CompleteStep(original, s.Id);
        return recorder;
    }

    [Fact]
    public void The_track_screen_flags_new_steps_and_offers_to_play_them()
    {
        Finish(Lesson("a", "b"));
        var grown = Lesson("a", "b", "c");

        var cut = Render<TrackView>(p => p.Add(c => c.Content, Graph(grown)).Add(c => c.Track, Graph(grown).Tracks[0]));

        Assert.Contains("Completed · 1 new step", cut.Markup);
        Assert.Equal("Play new step", cut.Find("li.lesson button").TextContent.Trim());
        Assert.Contains("1 of 1 lessons complete", cut.Markup);
    }

    [Fact]
    public void A_finished_lesson_without_new_steps_still_offers_review()
    {
        var done = Lesson("a", "b");
        Finish(done);

        var cut = Render<TrackView>(p => p.Add(c => c.Content, Graph(done)).Add(c => c.Track, Graph(done).Tracks[0]));

        Assert.DoesNotContain("new step", cut.Markup);
        Assert.Equal("Review", cut.Find("li.lesson button").TextContent.Trim());
    }

    [Fact]
    public void Playing_a_finished_lesson_shows_only_the_new_step_and_never_records_completion_again()
    {
        Finish(Lesson("a", "b"));
        var grown = Lesson("a", "b", "c");

        var cut = Render<LessonViewer>(p => p.Add(c => c.Lesson, grown));

        Assert.Contains("Title c", cut.Markup);
        Assert.Contains("1 new step since you finished", cut.Markup);

        cut.Find(".step-actions button.primary").Click();

        Assert.Contains("Lesson complete", cut.Markup);
        Assert.Single(_log.Items, e => e.Type == ProgressEventTypes.LessonCompleted);
    }
}
