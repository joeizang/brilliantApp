using Brilliant.Core.Content;
using Brilliant.Core.Feedback;
using Brilliant.Core.Progress;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The Written answers screen: answers written on any device, each with feedback on request from this device's model (#167).</summary>
public class WrittenAnswersViewTests : ShortcutContext
{
    private sealed class MemoryLog : IProgressEventLog
    {
        public List<ProgressEvent> Items { get; } = [];
        public bool Append(ProgressEvent e) { Items.Add(e); return true; }
        public IReadOnlyList<ProgressEvent> ReadAll() => Items;
        public EventPage ReadSince(long cursor) => new(Items.Skip((int)cursor).ToList(), Items.Count);
    }

    private sealed class FakeGrader : IAnswerGrader
    {
        public List<GradeRequest> Requests { get; } = [];
        public Func<TaskCompletionSource<AnswerFeedback>> Next { get; set; } = () => new();
        public TaskCompletionSource<AnswerFeedback> Reply { get; private set; } = new();
        public bool Enabled { get; set; } = true;

        public Task<AnswerFeedback> GradeAsync(GradeRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            Reply = Next();
            return Reply.Task;
        }
    }

    private static readonly DesignTradeoffStep Tradeoff = new(
        "step.tradeoff.cache", "Where should the cache live?", "Three instances serve one API.",
        [new("In-process MemoryCache", false, "They disagree."), new("Redis shared by all instances", true, "One copy to invalidate.")],
        [new("Several instances need one consistent view", 2), new("Accepts the network hop", 1)],
        "Use a shared cache.",
        ["A shared cache is always faster."]);

    private static readonly Lesson Lesson = new("lesson.cache", "Caching", "track.t", [Tradeoff]);
    private static readonly ContentGraph Content = new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "T", [Lesson.Id])], [Lesson]);

    private static readonly AnswerFeedback Verdict = new(
        [new(0, "Several instances need one consistent view", true, "You said every instance sees one copy."), new(1, "Accepts the network hop", false, "You never mentioned the hop.")],
        []);

    private readonly MemoryLog _log = new();
    private readonly FakeGrader _grader = new();

    public WrittenAnswersViewTests()
    {
        Services.AddSingleton(new ProgressRecorder(_log, "phone"));
        Services.AddSingleton<IAnswerGrader>(_grader);
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/shortcuts.js");
    }

    private void Written(string words = "Because every instance sees one copy.") =>
        new ProgressRecorder(_log, "phone").TradeoffSubmitted(Lesson.Id, Tradeoff.Id, true, 0.8, 1, words, [0], [], GradeRequest.ContextOf(Tradeoff, 1));

    private IRenderedComponent<WrittenAnswersView> Open() => Render<WrittenAnswersView>(p => p.Add(c => c.Content, Content));

    [Fact]
    public void With_nothing_written_the_screen_says_so()
    {
        Assert.Contains("No written answers", Open().Find(".written-none").TextContent);
    }

    [Fact]
    public void An_answer_for_a_step_that_has_since_changed_is_not_offered_and_the_screen_says_so()
    {
        new ProgressRecorder(_log, "phone").TradeoffSubmitted(Lesson.Id, Tradeoff.Id, true, 0.8, 1, "Words.", [0], [], GradeRequest.ContextOf(Tradeoff with { Prompt = "Before the rewrite." }, 1));
        var cut = Open();

        Assert.Empty(cut.FindAll(".written-item"));
        Assert.Contains("1 older answer isn't listed", cut.Find(".written-changed").TextContent);
    }

    [Fact]
    public void An_answer_is_listed_with_its_option_and_words_and_no_feedback_until_asked()
    {
        Written();
        var cut = Open();

        var item = cut.Find(".written-item");
        Assert.Contains("Where should the cache live?", item.TextContent);
        Assert.Contains("Redis shared by all instances", cut.Find(".written-option").TextContent);
        Assert.Equal("Because every instance sees one copy.", cut.Find(".written-justification").TextContent);
        Assert.Empty(cut.FindAll(".feedback"));
        Assert.Empty(_grader.Requests);
    }

    [Fact]
    public async Task Asking_grades_that_answer_as_it_was_written_and_shows_the_feedback()
    {
        Written();
        var cut = Open();

        cut.Find("button.get-feedback").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".feedback-pending")));
        Assert.Empty(cut.FindAll("button.get-feedback"));
        var request = Assert.Single(_grader.Requests);
        Assert.Contains("Because every instance sees one copy.", request.Answer);

        _grader.Reply.SetResult(Verdict);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".feedback-point").Count));
        Assert.Equal("hit", cut.Find(".feedback-point[data-point='0']").GetAttribute("data-verdict"));
        await cut.InvokeAsync(() => { });
    }

    [Fact]
    public void When_the_model_cannot_be_reached_the_reason_shows_and_the_learner_can_try_again()
    {
        Written();
        _grader.Next = () =>
        {
            var tcs = new TaskCompletionSource<AnswerFeedback>();
            tcs.SetException(new AnswerGraderException(AnswerGraderFailure.Unavailable, "Ollama isn't running. Start it on this Mac to get feedback."));
            return tcs;
        };
        var cut = Open();

        cut.Find("button.get-feedback").Click();

        cut.WaitForAssertion(() => Assert.Contains("Ollama isn't running", cut.Find(".feedback-unavailable").TextContent));
        Assert.Equal("Try again", cut.Find("button.get-feedback").TextContent);

        _grader.Next = () =>
        {
            var tcs = new TaskCompletionSource<AnswerFeedback>();
            tcs.SetResult(Verdict);
            return tcs;
        };
        cut.Find("button.get-feedback").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".feedback-point").Count));
        Assert.Equal(2, _grader.Requests.Count);
    }

    [Fact]
    public void Any_other_failure_is_shown_as_unavailable_not_thrown()
    {
        Written();
        _grader.Next = () =>
        {
            var tcs = new TaskCompletionSource<AnswerFeedback>();
            tcs.SetException(new InvalidOperationException("boom"));
            return tcs;
        };
        var cut = Open();

        cut.Find("button.get-feedback").Click();

        cut.WaitForAssertion(() => Assert.Contains("unavailable", cut.Find(".feedback-unavailable").TextContent));
    }

    [Fact]
    public void Each_answer_is_asked_for_separately()
    {
        Written("First words.");
        Written("Second words.");
        var cut = Open();

        cut.FindAll("button.get-feedback")[0].Click();

        Assert.Single(_grader.Requests);
        Assert.Single(cut.FindAll("button.get-feedback"));
    }

    [Fact]
    public async Task Leaving_the_screen_cancels_a_request_in_flight()
    {
        Written();
        var cut = Open();
        cut.Find("button.get-feedback").Click();

        await DisposeComponentsAsync();
        _grader.Reply.SetResult(Verdict);

        Assert.Single(_grader.Requests);
    }

    // ---- Reaching the screen ----

    [Fact]
    public void The_sidebar_and_the_narrow_layout_both_open_the_screen_where_a_model_is_available()
    {
        Written();
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));

        cut.Find("button.side-written").Click();

        Assert.Equal("Written answers", cut.Find("h1.screen-title").TextContent);
        Assert.NotEmpty(cut.FindAll("button.get-feedback"));
        Assert.Contains("current", cut.Find("button.side-written").ClassName);
    }

    [Fact]
    public void A_device_with_no_model_has_no_Written_answers_entry()
    {
        _grader.Enabled = false;
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));

        Assert.Empty(cut.FindAll("button.side-written"));
    }

    [Fact]
    public void A_device_with_no_grader_registered_has_no_Written_answers_entry()
    {
        Services.RemoveAll<IAnswerGrader>();
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));

        Assert.Empty(cut.FindAll("button.side-written"));
    }

    [Fact]
    public void The_tracks_screen_links_to_the_screen_only_when_something_handles_it()
    {
        var opened = false;

        var cut = Render<TracksView>(p => p.Add(c => c.Content, Content).Add(c => c.OnOpenWritten, () => opened = true));
        cut.Find("button.written-link").Click();

        Assert.True(opened);
        Assert.Empty(Render<TracksView>(p => p.Add(c => c.Content, Content)).FindAll("button.written-link"));
    }
}
