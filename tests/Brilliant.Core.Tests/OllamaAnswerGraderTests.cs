using System.Net;
using System.Text;
using System.Text.Json;
using Brilliant.Core.Feedback;

namespace Brilliant.Core.Tests;

/// <summary>The Ollama provider (#166), against a fake HTTP backend: the request it sends, the structured answer it parses, and every way it can fail.</summary>
public sealed class OllamaAnswerGraderTests
{
    private sealed class Backend(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return await respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Ollama wraps the model's JSON, as a string, in message.content.
    private static HttpResponseMessage Reply(string modelJson) =>
        Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { model = "gemma3:12b", done = true, message = new { role = "assistant", content = modelJson } }));

    private const string Good = """
        {"points":[{"index":0,"hit":true,"note":"You said every instance sees one copy."},{"index":1,"hit":false,"note":"You never mentioned the hop."}],
         "redFlagsFound":[{"index":0,"note":"You called it faster."}]}
        """;

    private static (OllamaAnswerGrader Grader, Backend Backend) Make(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> respond, string model = "gemma3:12b")
    {
        var backend = new Backend(respond);
        return (new OllamaAnswerGrader(new HttpClient(backend), model), backend);
    }

    [Fact]
    public async Task It_posts_the_rubric_prompt_and_schema_to_the_local_chat_endpoint_for_the_named_model()
    {
        var (grader, backend) = Make((_, _) => Task.FromResult(Reply(Good)));

        await grader.GradeAsync(GradingPromptTests.Request());

        var (request, body) = Assert.Single(backend.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:11434/api/chat", request.RequestUri!.ToString());
        var sent = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("gemma3:12b", sent.GetProperty("model").GetString());
        Assert.False(sent.GetProperty("stream").GetBoolean());
        Assert.Equal(0, sent.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal("object", sent.GetProperty("format").GetProperty("type").GetString());
        var messages = sent.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Contains("Several instances need one consistent view", messages[1].GetProperty("content").GetString());
        Assert.Contains("Because every instance sees one copy.", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task It_parses_a_note_for_each_rubric_point_and_the_red_flags_found()
    {
        var (grader, _) = Make((_, _) => Task.FromResult(Reply(Good)));

        var feedback = await grader.GradeAsync(GradingPromptTests.Request());

        Assert.Equal(2, feedback.Points.Count);
        Assert.Equal(new PointFeedback(0, "Several instances need one consistent view", true, "You said every instance sees one copy."), feedback.Points[0]);
        Assert.Equal(new PointFeedback(1, "Accepts the network hop", false, "You never mentioned the hop."), feedback.Points[1]);
        var flag = Assert.Single(feedback.RedFlagsFound);
        Assert.Equal(new FlagFinding(0, "A shared cache is always faster.", "You called it faster."), flag);
    }

    [Fact]
    public async Task Points_come_back_in_rubric_order_whatever_order_the_model_listed_them()
    {
        var (grader, _) = Make((_, _) => Task.FromResult(Reply("""{"points":[{"index":1,"hit":true,"note":"b"},{"index":0,"hit":false,"note":"a"}],"redFlagsFound":[]}""")));

        var feedback = await grader.GradeAsync(GradingPromptTests.Request());

        Assert.Equal([0, 1], feedback.Points.Select(p => p.Index));
        Assert.Empty(feedback.RedFlagsFound);
    }

    [Fact]
    public async Task A_missing_note_is_empty_and_a_long_one_is_cut_short()
    {
        var long400 = new string('x', 900);
        var (grader, _) = Make((_, _) => Task.FromResult(Reply(
            $$"""{"points":[{"index":0,"hit":true},{"index":1,"hit":false,"note":"{{long400}}"}],"redFlagsFound":[]}""")));

        var feedback = await grader.GradeAsync(GradingPromptTests.Request());

        Assert.Equal("", feedback.Points[0].Note);
        Assert.InRange(feedback.Points[1].Note.Length, 1, 400);
    }

    [Fact]
    public async Task The_same_red_flag_reported_twice_is_reported_once()
    {
        var (grader, _) = Make((_, _) => Task.FromResult(Reply(
            """{"points":[{"index":0,"hit":true,"note":""},{"index":1,"hit":true,"note":""}],"redFlagsFound":[{"index":1,"note":"first"},{"index":1,"note":"again"}]}""")));

        var feedback = await grader.GradeAsync(GradingPromptTests.Request());

        Assert.Equal("first", Assert.Single(feedback.RedFlagsFound).Note);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"redFlagsFound":[]}""")]                                                                                 // no points
    [InlineData("""{"points":[{"index":0,"hit":true,"note":""}],"redFlagsFound":[]}""")]                                    // a point short
    [InlineData("""{"points":[{"index":0,"hit":true,"note":""},{"index":0,"hit":true,"note":""}],"redFlagsFound":[]}""")]   // a point twice
    [InlineData("""{"points":[{"index":0,"hit":true,"note":""},{"index":7,"hit":true,"note":""}],"redFlagsFound":[]}""")]   // not a rubric point
    [InlineData("""{"points":[{"index":0,"hit":"yes","note":""},{"index":1,"hit":true,"note":""}],"redFlagsFound":[]}""")]  // hit is not a bool
    [InlineData("""{"points":[{"index":0,"hit":true,"note":""},{"index":1,"hit":true,"note":""}],"redFlagsFound":[{"index":9,"note":""}]}""")]   // a red flag the step doesn't have
    public async Task A_malformed_model_answer_is_a_bad_response_not_a_crash(string modelJson)
    {
        var (grader, _) = Make((_, _) => Task.FromResult(Reply(modelJson)));

        var ex = await Assert.ThrowsAsync<AnswerGraderException>(() => grader.GradeAsync(GradingPromptTests.Request()));

        Assert.Equal(AnswerGraderFailure.BadResponse, ex.Failure);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"message":null}""")]
    [InlineData("""{"message":{"role":"assistant"}}""")]
    [InlineData("<html>not ollama</html>")]
    public async Task An_envelope_that_is_not_what_Ollama_sends_is_a_bad_response(string body)
    {
        var (grader, _) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body)));

        var ex = await Assert.ThrowsAsync<AnswerGraderException>(() => grader.GradeAsync(GradingPromptTests.Request()));

        Assert.Equal(AnswerGraderFailure.BadResponse, ex.Failure);
    }

    [Fact]
    public async Task Ollama_not_running_says_so_in_words_the_learner_can_act_on()
    {
        var (grader, _) = Make((_, _) => throw new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<AnswerGraderException>(() => grader.GradeAsync(GradingPromptTests.Request()));

        Assert.Equal(AnswerGraderFailure.Unavailable, ex.Failure);
        Assert.Contains("Ollama isn't running", ex.Message);
    }

    [Fact]
    public async Task A_request_that_times_out_is_unavailable_too()
    {
        var (grader, _) = Make((_, _) => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));

        var ex = await Assert.ThrowsAsync<AnswerGraderException>(() => grader.GradeAsync(GradingPromptTests.Request()));

        Assert.Equal(AnswerGraderFailure.Unavailable, ex.Failure);
    }

    [Fact]
    public async Task The_learner_cancelling_is_not_an_error()
    {
        using var cts = new CancellationTokenSource();
        var (grader, _) = Make(async (_, _) => { await cts.CancelAsync(); cts.Token.ThrowIfCancellationRequested(); return Reply(Good); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grader.GradeAsync(GradingPromptTests.Request(), cts.Token));
    }

    [Fact]
    public async Task A_model_that_has_not_been_pulled_names_the_command_that_fetches_it()
    {
        var (grader, _) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.NotFound, """{"error":"model 'gemma3:12b' not found"}""")), "gemma3:12b");

        var ex = await Assert.ThrowsAsync<AnswerGraderException>(() => grader.GradeAsync(GradingPromptTests.Request()));

        Assert.Equal(AnswerGraderFailure.ModelMissing, ex.Failure);
        Assert.Contains("ollama pull gemma3:12b", ex.Message);
    }

    [Fact]
    public async Task Any_other_error_status_is_unavailable_and_does_not_echo_the_servers_text()
    {
        var (grader, _) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, """{"error":"CUDA out of memory"}""")));

        var ex = await Assert.ThrowsAsync<AnswerGraderException>(() => grader.GradeAsync(GradingPromptTests.Request()));

        Assert.Equal(AnswerGraderFailure.Unavailable, ex.Failure);
        Assert.DoesNotContain("CUDA", ex.Message);
    }

    [Fact]
    public void A_blank_model_name_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => new OllamaAnswerGrader(new HttpClient(), " "));
    }
}
