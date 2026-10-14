using Brilliant.Core.Content;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Tests;

/// <summary>Flagging a step, and the author's export of everything flagged.</summary>
public class FlaggedStepsTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private static ExplainStep Explain(string id, string title) => new(id, title, "b", [], null);

    private static readonly Lesson L1 = new("lesson.1", "One", "track.a", [Explain("step.1.a", "First"), Explain("step.1.b", "Second")]);
    private static readonly Lesson L2 = new("lesson.2", "Two", "track.a", [Explain("step.2.a", "Third")]);

    private static readonly ContentGraph Content = new(new PackManifest("pack.t", "1.0.0", 1),
        [new Track("track.a", "A", [L1.Id, L2.Id])], [L1, L2]);

    private readonly MemoryLog _log = new();
    private readonly Clock _clock = new();
    private ProgressRecorder Recorder => new(_log, "device", _clock);

    private IReadOnlyList<FlaggedStep> Flags => FlaggedSteps.From(_log.ReadAll(), Content);

    [Fact]
    public void A_flag_records_the_step_and_the_trimmed_note()
    {
        Recorder.StepFlagged("lesson.1", "step.1.a", "  the C# comparison is wrong  ");

        var flag = Assert.Single(Flags);
        Assert.Equal(("lesson.1", "step.1.a"), (flag.LessonId, flag.StepId));
        Assert.Equal("the C# comparison is wrong", flag.Note);
        Assert.Equal("First", flag.StepTitle);
        Assert.Equal("One", flag.LessonTitle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void A_blank_note_is_no_note(string? note)
    {
        Recorder.StepFlagged("lesson.1", "step.1.a", note);

        Assert.Null(Assert.Single(Flags).Note);
    }

    [Fact]
    public void Flags_come_in_content_order_then_oldest_first()
    {
        Recorder.StepFlagged("lesson.2", "step.2.a", "late lesson");
        _clock.Now = _clock.Now.AddMinutes(1);
        Recorder.StepFlagged("lesson.1", "step.1.b", "second step, first time");
        _clock.Now = _clock.Now.AddMinutes(1);
        Recorder.StepFlagged("lesson.1", "step.1.a", "first step");
        _clock.Now = _clock.Now.AddMinutes(1);
        Recorder.StepFlagged("lesson.1", "step.1.b", "second step, again");

        Assert.Equal(["first step", "second step, first time", "second step, again", "late lesson"], Flags.Select(f => f.Note));
    }

    [Fact]
    public void Flags_on_removed_content_are_kept_and_listed_last()
    {
        Recorder.StepFlagged("lesson.gone", "step.gone", "old");
        Recorder.StepFlagged("lesson.1", "step.deleted", "deleted since");
        Recorder.StepFlagged("lesson.2", "step.2.a", "still here");

        Assert.Equal("still here", Flags[0].Note);
        Assert.All(Flags.Skip(1), f => Assert.Null(f.Step));
        Assert.Equal("step.gone", Flags.Single(f => f.Note == "old").StepTitle);
    }

    [Fact]
    public void Flagging_does_not_change_progress()
    {
        var before = LearnerStateProjector.Project([], Content, _clock.Now);
        Recorder.StepFlagged("lesson.1", "step.1.a", "confusing");

        var after = LearnerStateProjector.Project(_log.ReadAll(), Content, _clock.Now);

        Assert.Equal(before.Tracks[0].Lessons[0].Progress.CompletedCount, after.Tracks[0].Lessons[0].Progress.CompletedCount);
        Assert.Equal(before.Today.Streak, after.Today.Streak);
    }

    [Fact]
    public void The_export_lists_lesson_step_id_note_and_date()
    {
        Recorder.StepFlagged("lesson.1", "step.1.a", "line one\nline two");
        Recorder.StepFlagged("lesson.1", "step.1.b");
        _clock.Now = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        Recorder.StepFlagged("lesson.2", "step.2.a", "too fast");

        var md = FlaggedSteps.ToMarkdown(Flags);

        Assert.Equal("""
            # Flagged steps

            ## One (`lesson.1`)

            - **First** (`step.1.a`), 2026-10-10
              > line one
              > line two
            - **Second** (`step.1.b`), 2026-10-10

            ## Two (`lesson.2`)

            - **Third** (`step.2.a`), 2026-10-12
              > too fast

            """.ReplaceLineEndings("\n"), md);
    }

    [Fact]
    public void An_empty_export_says_so()
    {
        Assert.Contains("Nothing is flagged.", FlaggedSteps.ToMarkdown(Flags));
    }

    [Fact]
    public void A_flag_on_a_removed_step_is_marked_in_the_export()
    {
        Recorder.StepFlagged("lesson.1", "step.deleted", "was confusing");

        Assert.Contains("(`step.deleted`), 2026-10-10, step no longer exists", FlaggedSteps.ToMarkdown(Flags));
    }

    [Fact]
    public void A_flagged_practice_problem_is_found_and_sorts_after_its_lessons_steps()
    {
        var problem = new ParsonsStep("step.1.extra", "Extra", "Order these", "python", [new ParsonsLine("x = 1", 0)]) { Pattern = "pattern.p" };
        var lesson = L1 with { Practice = [problem] };
        var content = new ContentGraph(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.a", "A", [lesson.Id])], [lesson]);
        Recorder.StepFlagged("lesson.1", "step.1.extra", "the cue is vague");
        _clock.Now = _clock.Now.AddMinutes(1);
        Recorder.StepFlagged("lesson.1", "step.1.b", null);

        var flags = FlaggedSteps.From(_log.ReadAll(), content);
        Assert.Equal(["step.1.b", "step.1.extra"], flags.Select(f => f.StepId));
        Assert.Equal(("Extra", false), (flags[1].StepTitle, flags[1].Step is null));
    }
}
