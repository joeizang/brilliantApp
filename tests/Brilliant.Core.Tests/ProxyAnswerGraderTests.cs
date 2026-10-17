using System.Net;
using System.Text;
using System.Text.Json;
using Brilliant.Core.Feedback;
using Brilliant.Core.Sync;

namespace Brilliant.Core.Tests;

/// <summary>The phone's grader (#167): the same <see cref="IAnswerGrader"/> as the Mac's, but asking the household server, only when the learner has turned that on.</summary>
public sealed class ProxyAnswerGraderTests
{
    private const string AppId = "com.joeizang.brilliantapp.csharp";

    private sealed class Backend(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return respond(request, body);
        }
    }

    private sealed class Credentials(DeviceCredentials? current) : IDeviceCredentialStore
    {
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult(current);
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Settings(bool on) : IFeedbackSettings
    {
        public bool UseServer { get; set; } = on;
    }

    private static readonly DeviceCredentials Paired = new(new Uri("https://home.test:8443/"), "device-1", "secret-token", AppId);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string Good = """
        {"points":[{"index":0,"point":"Several instances need one consistent view","hit":true,"note":"One copy."},{"index":1,"point":"Accepts the network hop","hit":false,"note":""}],
         "redFlagsFound":[{"index":1,"flag":"Redis never goes down.","note":"You said it cannot fail."}]}
        """;

    private static (ProxyAnswerGrader Grader, Backend Backend) Make(Func<HttpRequestMessage, string?, HttpResponseMessage> respond, bool on = true, DeviceCredentials? credentials = null, bool unpaired = false)
    {
        var backend = new Backend(respond);
        return (new ProxyAnswerGrader(new HttpClient(backend), new Credentials(unpaired ? null : credentials ?? Paired), new Settings(on), AppId), backend);
    }

    private static async Task<AnswerGraderException> Fails(ProxyAnswerGrader grader) =>
        await Assert.ThrowsAsync<AnswerGraderException>(() => grader.GradeAsync(GradingPromptTests.Request()));

    [Fact]
    public async Task It_posts_the_request_to_the_servers_grading_endpoint_with_the_device_token_and_app()
    {
        var (grader, backend) = Make((_, _) => Json(HttpStatusCode.OK, Good));

        var feedback = await grader.GradeAsync(GradingPromptTests.Request("Because every instance sees one copy."));

        var (request, body) = Assert.Single(backend.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://home.test:8443/feedback/grade", request.RequestUri!.ToString());
        Assert.Equal("Bearer secret-token", request.Headers.Authorization!.ToString());
        Assert.Equal(AppId, request.Headers.GetValues(AppSync.AppIdHeader).Single());
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("Because every instance sees one copy.", sent.RootElement.GetProperty("answer").GetString());
        Assert.Equal(2, sent.RootElement.GetProperty("rubricPoints").GetArrayLength());

        Assert.Equal([true, false], feedback.Points.Select(p => p.Hit));
        Assert.Equal("One copy.", feedback.Points[0].Note);
        Assert.Equal(new FlagFinding(1, "Redis never goes down.", "You said it cannot fail."), Assert.Single(feedback.RedFlagsFound));
    }

    [Fact]
    public async Task The_wording_shown_comes_from_the_local_request_not_from_the_server()
    {
        var (grader, _) = Make((_, _) => Json(HttpStatusCode.OK, Good.Replace("Accepts the network hop", "Injected text")));

        var feedback = await grader.GradeAsync(GradingPromptTests.Request());

        Assert.Equal("Accepts the network hop", feedback.Points[1].Point);
    }

    [Fact]
    public void It_is_on_only_while_the_learner_has_opted_in()
    {
        var settings = new Settings(false);
        var grader = new ProxyAnswerGrader(new HttpClient(new Backend((_, _) => Json(HttpStatusCode.OK, Good))), new Credentials(Paired), settings, AppId);

        Assert.False(grader.Enabled);
        settings.UseServer = true;
        Assert.True(grader.Enabled);
    }

    [Fact]
    public async Task Off_means_the_server_is_never_asked()
    {
        var (grader, backend) = Make((_, _) => Json(HttpStatusCode.OK, Good), on: false);

        var e = await Fails(grader);

        Assert.Equal(AnswerGraderFailure.Unavailable, e.Failure);
        Assert.Empty(backend.Seen);
    }

    [Fact]
    public async Task A_phone_that_is_not_paired_is_told_to_pair_and_nothing_is_sent()
    {
        var (grader, backend) = Make((_, _) => Json(HttpStatusCode.OK, Good), unpaired: true);

        var e = await Fails(grader);

        Assert.Equal(AnswerGraderFailure.Unavailable, e.Failure);
        Assert.Contains("Pair", e.Message);
        Assert.Empty(backend.Seen);
    }

    [Fact]
    public async Task Credentials_for_another_app_are_not_used()
    {
        var (grader, backend) = Make((_, _) => Json(HttpStatusCode.OK, Good), credentials: Paired with { AppId = "com.other" });

        Assert.Equal(AnswerGraderFailure.Unavailable, (await Fails(grader)).Failure);
        Assert.Empty(backend.Seen);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_feedback_unavailable_right_now_and_the_answer_can_be_graded_later()
    {
        var (grader, _) = Make((_, _) => throw new HttpRequestException("no route to host"));

        var e = await Fails(grader);

        Assert.Equal(AnswerGraderFailure.Unavailable, e.Failure);
        Assert.Equal(ProxyAnswerGrader.UnavailableMessage, e.Message);
        Assert.Contains("unavailable right now", e.Message);
        Assert.Contains("Mac", e.Message);
    }

    [Fact]
    public async Task A_server_that_takes_too_long_is_unavailable_too()
    {
        var (grader, _) = Make((_, _) => throw new TaskCanceledException("timed out"));

        Assert.Equal(ProxyAnswerGrader.UnavailableMessage, (await Fails(grader)).Message);
    }

    [Fact]
    public async Task The_caller_cancelling_is_not_a_failure()
    {
        var (grader, _) = Make((_, _) => throw new TaskCanceledException("cancelled"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grader.GradeAsync(GradingPromptTests.Request(), cts.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_server_that_says_it_has_no_feedback_to_give_is_unavailable(HttpStatusCode status)
    {
        var (grader, _) = Make((_, _) => Json(status, """{"failure":"Unavailable","error":"The server isn't set up."}"""));

        var e = await Fails(grader);

        Assert.Equal(AnswerGraderFailure.Unavailable, e.Failure);
        Assert.Equal(ProxyAnswerGrader.UnavailableMessage, e.Message);
    }

    [Fact]
    public async Task A_model_the_macs_ollama_lacks_is_reported_with_the_servers_fix()
    {
        var (grader, _) = Make((_, _) => Json(HttpStatusCode.ServiceUnavailable,
            """{"failure":"ModelMissing","error":"Ollama doesn't have the model 'gemma3:12b'. Fetch it with: ollama pull gemma3:12b"}"""));

        var e = await Fails(grader);

        Assert.Equal(AnswerGraderFailure.ModelMissing, e.Failure);
        Assert.Contains("ollama pull gemma3:12b", e.Message);
    }

    [Fact]
    public async Task A_model_reply_the_server_could_not_understand_is_a_bad_response()
    {
        var (grader, _) = Make((_, _) => Json(HttpStatusCode.BadGateway, """{"failure":"BadResponse","error":"The model's feedback couldn't be understood."}"""));

        Assert.Equal(AnswerGraderFailure.BadResponse, (await Fails(grader)).Failure);
    }

    [Fact]
    public async Task A_revoked_device_is_told_to_pair_again()
    {
        var (grader, _) = Make((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var e = await Fails(grader);

        Assert.Equal(AnswerGraderFailure.Unavailable, e.Failure);
        Assert.Contains("pair", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"points":[],"redFlagsFound":[]}""")]
    [InlineData("""{"points":[{"index":0,"hit":true},{"index":0,"hit":false}],"redFlagsFound":[]}""")]
    [InlineData("""{"points":[{"index":0,"hit":true},{"index":2,"hit":false}],"redFlagsFound":[]}""")]
    [InlineData("""{"points":[{"index":0,"hit":true},{"index":1,"hit":false}],"redFlagsFound":[{"index":9}]}""")]
    [InlineData("""{"points":[{"index":0,"hit":true},{"index":1,"hit":false}]}""")]
    public async Task A_success_that_is_not_a_complete_feedback_is_a_bad_response(string body)
    {
        var (grader, _) = Make((_, _) => Json(HttpStatusCode.OK, body));

        Assert.Equal(AnswerGraderFailure.BadResponse, (await Fails(grader)).Failure);
    }

    [Fact]
    public async Task Notes_are_cut_to_the_same_length_as_the_macs()
    {
        var (grader, _) = Make((_, _) => Json(HttpStatusCode.OK, Good.Replace("One copy.", new string('x', 900))));

        var feedback = await grader.GradeAsync(GradingPromptTests.Request());

        Assert.Equal(400, feedback.Points[0].Note.Length);
    }
}
