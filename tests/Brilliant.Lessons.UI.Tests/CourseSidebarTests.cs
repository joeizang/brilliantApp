using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The wide-window sidebar's tracks fold and unfold.</summary>
public class CourseSidebarTests : BunitContext
{
    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static Lesson OneLesson(string id, string trackId) =>
        new(id, "Lesson " + id, trackId, [new ExplainStep(id + ".1", "T", "Body", [], null), new ExplainStep(id + ".2", "T", "Body", [], null)]);

    private static readonly Lesson A = OneLesson("lesson.a", "track.a");
    private static readonly Lesson B = OneLesson("lesson.b", "track.b");
    private static readonly Track TrackA = new("track.a", "Track A", [A.Id]);
    private static readonly Track TrackB = new("track.b", "Track B", [B.Id]);
    private static readonly ContentGraph Content = new(new PackManifest("pack.t", "1.0.0", 1), [TrackA, TrackB], [A, B]);

    private readonly ProgressRecorder _recorder = new(new MemoryLog(), "device");

    public CourseSidebarTests() => Services.AddSingleton(_recorder);

    private IRenderedComponent<CourseSidebar> Open(Track? track = null, Lesson? lesson = null, Action<Track>? onTrack = null) =>
        Render<CourseSidebar>(p => p.Add(c => c.Content, Content).Add(c => c.CurrentTrack, track).Add(c => c.CurrentLesson, lesson)
            .Add(c => c.OnOpenTrack, t => onTrack?.Invoke(t)));

    private static string Expanded(IRenderedComponent<CourseSidebar> cut, int track) =>
        cut.FindAll(".side-track-title")[track].GetAttribute("aria-expanded")!;

    [Fact]
    public void Only_the_first_unfinished_track_starts_open_and_a_folded_one_is_inert()
    {
        var cut = Open();

        Assert.Equal(["true", "false"], [Expanded(cut, 0), Expanded(cut, 1)]);
        Assert.False(cut.FindAll(".side-track-panel")[0].HasAttribute("inert"));
        Assert.True(cut.FindAll(".side-track-panel")[1].HasAttribute("inert"));
        Assert.Equal("side-track-track-b", cut.FindAll(".side-track-title")[1].GetAttribute("aria-controls"));
    }

    [Fact]
    public void Clicking_a_track_folds_or_unfolds_it_alone()
    {
        var cut = Open();

        cut.FindAll(".side-track-title")[1].Click();
        Assert.Equal(["true", "true"], [Expanded(cut, 0), Expanded(cut, 1)]);

        cut.FindAll(".side-track-title")[0].Click();
        Assert.Equal(["false", "true"], [Expanded(cut, 0), Expanded(cut, 1)]);
    }

    [Fact]
    public void Option_click_folds_or_unfolds_every_track()
    {
        var cut = Open();

        cut.FindAll(".side-track-title")[1].Click(new MouseEventArgs { AltKey = true });
        Assert.Equal(["true", "true"], [Expanded(cut, 0), Expanded(cut, 1)]);

        cut.FindAll(".side-track-title")[0].Click(new MouseEventArgs { AltKey = true });
        Assert.Equal(["false", "false"], [Expanded(cut, 0), Expanded(cut, 1)]);
    }

    [Fact]
    public void Arriving_at_a_lesson_unfolds_its_track_even_after_it_was_folded()
    {
        var cut = Open();
        cut.FindAll(".side-track-title")[0].Click();
        Assert.Equal("false", Expanded(cut, 0));

        cut.Render(p => p.Add(c => c.CurrentTrack, TrackA).Add(c => c.CurrentLesson, A));

        Assert.Equal(["true", "false"], [Expanded(cut, 0), Expanded(cut, 1)]);
        Assert.Contains("current", cut.FindAll(".side-lesson")[0].ClassList);
    }

    [Fact]
    public void Overview_opens_the_track_and_is_current_on_its_page()
    {
        Track? opened = null;
        var cut = Open(onTrack: t => opened = t);
        cut.FindAll(".side-track-title")[1].Click();

        cut.FindAll(".side-overview")[1].Click();
        Assert.Equal(TrackB, opened);

        cut.Render(p => p.Add(c => c.CurrentTrack, TrackB));
        Assert.Equal("page", cut.FindAll(".side-overview")[1].GetAttribute("aria-current"));
        Assert.Null(cut.FindAll(".side-overview")[0].GetAttribute("aria-current"));
    }

    [Fact]
    public void A_finished_track_shows_its_count_and_the_next_one_opens_by_default()
    {
        foreach (var s in A.Steps) _recorder.CompleteStep(A, s.Id);

        var cut = Open();

        Assert.Contains("1/1", cut.FindAll(".side-track-title")[0].TextContent);
        Assert.NotNull(cut.FindAll(".side-track-title")[0].QuerySelector("[aria-label='1 of 1 lessons done']"));
        Assert.Single(cut.FindAll(".side-track-title")[0].QuerySelectorAll(".progress-ring .stroke-success"));
        Assert.Equal(["false", "true"], [Expanded(cut, 0), Expanded(cut, 1)]);
    }
}
