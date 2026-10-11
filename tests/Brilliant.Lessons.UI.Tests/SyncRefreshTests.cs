using System.Net.Http.Json;
using Brilliant.Core.Content;
using Brilliant.Core.Progress;
using Brilliant.Core.Sync;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>Progress that arrives from another device shows up on the open screen without a restart.</summary>
public class SyncRefreshTests : ShortcutContext
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private sealed class MemoryLog : IProgressEventLog
    {
        private readonly object _gate = new();
        private readonly List<ProgressEvent> _items = [];
        public bool Append(ProgressEvent e) { lock (_gate) { if (_items.Any(x => x.Id == e.Id)) return false; _items.Add(e); return true; } }
        public IReadOnlyList<ProgressEvent> ReadAll() { lock (_gate) return _items.ToList(); }
        public EventPage ReadSince(long cursor) { lock (_gate) return new(_items.Skip((int)cursor).ToList(), _items.Count); }
    }

    private sealed class Credentials : IDeviceCredentialStore
    {
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult<DeviceCredentials?>(new(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1"));
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class State : ISyncStateStore
    {
        private SyncState _state = SyncState.None;
        public SyncState Load() => _state;
        public void Save(SyncState state) => _state = state;
    }

    /// <summary>A server that has one event from another device, and takes whatever is pushed.</summary>
    private sealed class Server(params ProgressEvent[] events) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage
        {
            Content = request.Method == HttpMethod.Post
                ? JsonContent.Create(new PushEventsResponse(0, 0))
                : JsonContent.Create(new PullEventsResponse(request.RequestUri!.Query.EndsWith("=0") ? events : [], events.Length, false)),
        });
    }

    private static readonly Lesson Basics = new("lesson.basics", "Basics", "track.t", [new ExplainStep("step.intro", "Intro", "body", [], null)]);
    private static readonly ContentGraph Content = new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "Tools", [Basics.Id])], [Basics]);

    private readonly MemoryLog _log = new();

    public SyncRefreshTests()
    {
        Services.AddSingleton(new ProgressRecorder(_log, "this-device", new ManualTimeProvider()));
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Brilliant.Lessons.UI/shortcuts.js");
    }

    private SyncService AddSync(params ProgressEvent[] fromServer)
    {
        var sync = new SyncService(_log, "this-device", new Credentials(), new SyncClient(new HttpClient(new Server(fromServer))), new State());
        Services.AddSingleton(sync);
        return sync;
    }

    [Fact]
    public async Task A_goal_set_on_another_device_appears_on_Today_after_a_sync()
    {
        var sync = AddSync(new ProgressEvent("e1", "phone", T0, ProgressEventTypes.DailyGoalSet, "", "", """{"steps":7}"""));
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));
        Assert.Contains("of 10 steps", cut.Find(".today-goal").TextContent);

        await Task.Run(() => sync.SyncAsync());   // not the renderer's thread, like the periodic sync

        cut.WaitForAssertion(() => Assert.Contains("of 7 steps", cut.Find(".today-goal").TextContent));
    }

    private static ChoiceStep Q(string id) =>
        new(id, "Question " + id, "Pick the right one", false, [new ChoiceOption("right", true, null), new ChoiceOption("wrong", false, null)]);

    private static readonly Lesson Quiz = new("lesson.quiz", "Quiz", "track.t", [new ExplainStep("step.intro", "Intro", "b", [], null), Q("step.q1")])
    {
        Concepts = [new Concept("concept.c", "C")],
        ReviewItems = [new ReviewItem("review.q1", "concept.c", "step.q1")],
    };

    private static readonly ContentGraph QuizContent = new(new PackManifest("pack.t", "1.0.0", 1), [new Track("track.t", "Tools", [Quiz.Id])], [Quiz]);

    /// <summary>What the phone would have recorded finishing the lesson.</summary>
    private static ProgressEvent[] PhoneFinishesQuiz()
    {
        var phone = new MemoryLog();
        var recorder = new ProgressRecorder(phone, "phone", new ManualTimeProvider());
        foreach (var step in Quiz.Steps) recorder.CompleteStep(Quiz, step.Id);
        return [.. phone.ReadAll()];
    }

    [Fact]
    public async Task An_open_empty_Review_shows_the_questions_a_sync_unlocks()
    {
        var sync = AddSync(PhoneFinishesQuiz());
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, QuizContent));
        cut.Find("button.side-review").Click();
        Assert.Contains("Nothing to review yet", cut.Markup);

        await Task.Run(() => sync.SyncAsync());

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Nothing to review yet", cut.Markup);
            Assert.Contains("Question step.q1", cut.Markup);
        });
    }

    [Fact]
    public async Task A_sync_that_brings_nothing_leaves_the_screen_alone()
    {
        var sync = AddSync();
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));
        var renders = cut.RenderCount;

        await Task.Run(() => sync.SyncAsync());
        await Task.Delay(100);

        Assert.Equal(renders, cut.RenderCount);
    }

    [Fact]
    public void A_host_without_sync_still_shows_the_shell()
    {
        var cut = Render<CourseShell>(p => p.Add(c => c.Content, Content));

        Assert.NotNull(cut.Find(".today-goal"));
    }
}
