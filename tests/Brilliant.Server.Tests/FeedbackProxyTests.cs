using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Brilliant.Core.Feedback;
using Brilliant.Core.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Server.Tests;

/// <summary>The grading proxy (#167): a paired phone's request forwarded to the Ollama the server is configured with, and a quick, plain refusal when that Ollama isn't there.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FeedbackProxyTests(PostgresFixture postgres)
{
    private const string CSharpAppId = "com.joeizang.brilliantapp.csharp";

    private sealed class FakeOllama(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri? Url, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.RequestUri, body));
            return respond(request, body);
        }
    }

    private const string ModelJson = """
        {"points":[{"index":0,"hit":true,"note":"You said one copy."},{"index":1,"hit":false,"note":"No hop."}],"redFlagsFound":[{"index":0,"note":"Called it faster."}]}
        """;

    private static HttpResponseMessage Chat(string modelJson) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { message = new { role = "assistant", content = modelJson } }), Encoding.UTF8, "application/json"),
    };

    private static GradeRequest Request() => new(
        "Cache or database?", "A shared cache", "Everyone sees one copy.", "A shared cache gives one view.",
        ["Several instances need one view", "Accepts the network hop"], ["It is always faster"]);

    private Task<ServerHarness> Start(FakeOllama? ollama, string? url = "http://ollama.test:11434", params (string Key, string Value)[] more)
    {
        var settings = new Dictionary<string, string?> { ["Feedback:OllamaUrl"] = url };
        foreach (var (key, value) in more) settings[key] = value;
        return ServerHarness.StartAsync(postgres, settings: settings, configureServices: ollama is null ? null : services =>
            services.AddHttpClient(FeedbackEndpoints.OllamaClient).ConfigurePrimaryHttpMessageHandler(() => ollama));
    }

    private static async Task<(string Failure, string Error)> Refusal(HttpResponseMessage response)
    {
        var body = (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!;
        return (body["failure"], body["error"]);
    }

    [Fact]
    public async Task A_paired_phone_gets_feedback_from_the_configured_ollama_using_the_apps_grader_model()
    {
        var ollama = new FakeOllama((_, _) => Chat(ModelJson));
        await using var server = await Start(ollama);
        var (client, _) = await server.PairDeviceAsync(appId: CSharpAppId);

        var response = await client.PostAsJsonAsync("/feedback/grade", Request());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var feedback = (await response.Content.ReadFromJsonAsync<AnswerFeedback>())!;
        Assert.Equal([true, false], feedback.Points.Select(p => p.Hit));
        Assert.Equal("Several instances need one view", feedback.Points[0].Point);
        Assert.Equal("It is always faster", Assert.Single(feedback.RedFlagsFound).Flag);

        var (url, body) = Assert.Single(ollama.Seen);
        Assert.Equal("http://ollama.test:11434/api/chat", url!.ToString());
        using var sent = JsonDocument.Parse(body);
        Assert.Equal("gemma3:12b", sent.RootElement.GetProperty("model").GetString());
        Assert.Contains("Everyone sees one copy.", body);
    }

    private sealed class StoredCredentials(DeviceCredentials credentials) : IDeviceCredentialStore
    {
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult<DeviceCredentials?>(credentials);
        public Task SaveAsync(DeviceCredentials value, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class On : IFeedbackSettings
    {
        public bool UseServer { get; set; } = true;
    }

    [Fact]
    public async Task The_phones_own_client_and_the_server_agree_on_the_wire_format_end_to_end()
    {
        var ollama = new FakeOllama((_, _) => Chat(ModelJson));
        await using var server = await Start(ollama);
        var (_, device) = await server.PairDeviceAsync(appId: CSharpAppId);
        var http = server.CreateClient();
        var credentials = new DeviceCredentials(http.BaseAddress!, device.DeviceId, device.Token, CSharpAppId);
        var grader = new ProxyAnswerGrader(http, new StoredCredentials(credentials), new On(), CSharpAppId);

        var feedback = await grader.GradeAsync(Request());

        Assert.Equal([true, false], feedback.Points.Select(p => p.Hit));
        Assert.Equal("You said one copy.", feedback.Points[0].Note);
        Assert.Equal("It is always faster", Assert.Single(feedback.RedFlagsFound).Flag);
        Assert.Single(ollama.Seen);
    }

    [Fact]
    public async Task The_phones_own_client_reports_an_unreachable_mac_as_unavailable_not_as_a_crash()
    {
        await using var server = await Start(null, url: null);
        var (_, device) = await server.PairDeviceAsync(appId: CSharpAppId);
        var http = server.CreateClient();
        var credentials = new DeviceCredentials(http.BaseAddress!, device.DeviceId, device.Token, CSharpAppId);
        var grader = new ProxyAnswerGrader(http, new StoredCredentials(credentials), new On(), CSharpAppId);

        var ex = await Assert.ThrowsAsync<AnswerGraderException>(() => grader.GradeAsync(Request()));

        Assert.Equal(AnswerGraderFailure.Unavailable, ex.Failure);
        Assert.Equal(ProxyAnswerGrader.UnavailableMessage, ex.Message);
    }

    [Fact]
    public async Task A_request_without_a_device_token_is_refused_and_ollama_is_never_asked()
    {
        var ollama = new FakeOllama((_, _) => Chat(ModelJson));
        await using var server = await Start(ollama);

        var response = await server.CreateClient().PostAsJsonAsync("/feedback/grade", Request());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(ollama.Seen);
    }

    [Fact]
    public async Task An_app_whose_manifest_names_no_grader_model_gets_no_feedback()
    {
        var ollama = new FakeOllama((_, _) => Chat(ModelJson));
        await using var server = await Start(ollama);
        var (client, _) = await server.PairDeviceAsync();   // the DSA app

        var response = await client.PostAsJsonAsync("/feedback/grade", Request());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(ollama.Seen);
    }

    [Fact]
    public async Task A_server_with_no_ollama_url_says_feedback_is_unavailable_at_once()
    {
        await using var server = await Start(null, url: null);
        var (client, _) = await server.PairDeviceAsync(appId: CSharpAppId);

        var response = await client.PostAsJsonAsync("/feedback/grade", Request());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(nameof(AnswerGraderFailure.Unavailable), (await Refusal(response)).Failure);
    }

    [Fact]
    public async Task A_blank_ollama_url_counts_as_none()
    {
        await using var server = await Start(null, url: "");
        var (client, _) = await server.PairDeviceAsync(appId: CSharpAppId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/feedback/grade", Request())).StatusCode);
    }

    [Fact]
    public async Task An_ollama_that_cannot_be_reached_fails_fast_with_unavailable_and_without_leaking_the_address()
    {
        // The real HTTP stack against a port nothing listens on: a refused connection is immediate.
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        await using var server = await Start(null, url: $"http://127.0.0.1:{port}");
        var (client, _) = await server.PairDeviceAsync(appId: CSharpAppId);

        var clock = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync("/feedback/grade", Request());
        clock.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var (failure, error) = await Refusal(response);
        Assert.Equal(nameof(AnswerGraderFailure.Unavailable), failure);
        Assert.DoesNotContain(port.ToString(), error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task An_ollama_that_accepts_the_connection_and_never_answers_is_given_up_on_after_the_configured_timeout()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            _ = Task.Run(async () => { while (true) (await silent.AcceptTcpClientAsync()).GetStream(); });   // accepts, then says nothing
            await using var server = await Start(null, url: $"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}", ("Feedback:TimeoutSeconds", "1"));
            var (client, _) = await server.PairDeviceAsync(appId: CSharpAppId);

            var clock = Stopwatch.StartNew();
            var response = await client.PostAsJsonAsync("/feedback/grade", Request());
            clock.Stop();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(nameof(AnswerGraderFailure.Unavailable), (await Refusal(response)).Failure);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        }
        finally { silent.Stop(); }
    }

    [Fact]
    public async Task A_model_ollama_does_not_have_is_reported_with_the_fix()
    {
        await using var server = await Start(new FakeOllama((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var (client, _) = await server.PairDeviceAsync(appId: CSharpAppId);

        var response = await client.PostAsJsonAsync("/feedback/grade", Request());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var (failure, error) = await Refusal(response);
        Assert.Equal(nameof(AnswerGraderFailure.ModelMissing), failure);
        Assert.Contains("ollama pull gemma3:12b", error);
    }

    [Fact]
    public async Task A_model_reply_that_does_not_fit_the_schema_is_a_bad_gateway()
    {
        await using var server = await Start(new FakeOllama((_, _) => Chat("""{"points":[]}""")));
        var (client, _) = await server.PairDeviceAsync(appId: CSharpAppId);

        var response = await client.PostAsJsonAsync("/feedback/grade", Request());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(nameof(AnswerGraderFailure.BadResponse), (await Refusal(response)).Failure);
    }

    [Fact]
    public async Task Ollama_answering_with_an_error_is_unavailable()
    {
        await using var server = await Start(new FakeOllama((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var (client, _) = await server.PairDeviceAsync(appId: CSharpAppId);

        var response = await client.PostAsJsonAsync("/feedback/grade", Request());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(nameof(AnswerGraderFailure.Unavailable), (await Refusal(response)).Failure);
    }

    public static TheoryData<string> BadRequests => new()
    {
        "null",
        """{"scenario":"s","option":"o","answer":"","modelAnswer":"m","rubricPoints":["p"],"redFlags":[]}""",
        """{"scenario":"s","option":"o","answer":"a","modelAnswer":"m","rubricPoints":[],"redFlags":[]}""",
        """{"scenario":"s","option":"o","answer":"a","modelAnswer":"m","rubricPoints":[null],"redFlags":[]}""",
        """{"scenario":"s","option":"o","answer":"a","modelAnswer":"m","rubricPoints":["p"],"redFlags":null}""",
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task A_request_that_is_not_a_gradable_answer_is_refused_before_ollama_is_asked(string json)
    {
        var ollama = new FakeOllama((_, _) => Chat(ModelJson));
        await using var server = await Start(ollama);
        var (client, _) = await server.PairDeviceAsync(appId: CSharpAppId);

        var response = await client.PostAsync("/feedback/grade", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(ollama.Seen);
    }

    [Fact]
    public async Task An_oversized_answer_is_refused()
    {
        var ollama = new FakeOllama((_, _) => Chat(ModelJson));
        await using var server = await Start(ollama);
        var (client, _) = await server.PairDeviceAsync(appId: CSharpAppId);

        var response = await client.PostAsJsonAsync("/feedback/grade", Request() with { Answer = new string('x', FeedbackEndpoints.MaxTextLength + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(ollama.Seen);
    }
}
