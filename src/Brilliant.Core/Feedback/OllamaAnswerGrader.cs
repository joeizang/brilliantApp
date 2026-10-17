using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Brilliant.Core.Feedback;

/// <summary>
/// Asks a model served by Ollama on this machine for feedback. The call uses Ollama's JSON-schema <c>format</c>, so the reply is
/// structured; anything that still doesn't fit is a <see cref="AnswerGraderFailure.BadResponse"/>, never a guess. The
/// <see cref="HttpClient"/> is injected so tests can answer for Ollama, and carries the timeout (local models can be slow).
/// </summary>
public sealed class OllamaAnswerGrader : IAnswerGrader
{
    public static readonly Uri DefaultEndpoint = new("http://localhost:11434/");
    private const int MaxNote = 400;

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly Uri _chat;

    public OllamaAnswerGrader(HttpClient http, string model, Uri? endpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _http = http;
        _model = model;
        _chat = new Uri(endpoint ?? DefaultEndpoint, "api/chat");
    }

    public async Task<AnswerFeedback> GradeAsync(GradeRequest request, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["format"] = GradingPrompt.Schema(request),
            ["options"] = new JsonObject { ["temperature"] = 0 },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = GradingPrompt.System },
                new JsonObject { ["role"] = "user", ["content"] = GradingPrompt.User(request) }),
        };

        string envelope;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _chat) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(message, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new AnswerGraderException(AnswerGraderFailure.ModelMissing,
                    $"Ollama doesn't have the model '{_model}'. Fetch it with: ollama pull {_model}");
            if (!response.IsSuccessStatusCode)
                throw new AnswerGraderException(AnswerGraderFailure.Unavailable, $"Ollama couldn't give feedback right now (HTTP {(int)response.StatusCode}).");
            envelope = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            throw new AnswerGraderException(AnswerGraderFailure.Unavailable, "Ollama took too long to answer.");
        }
        catch (HttpRequestException)
        {
            throw new AnswerGraderException(AnswerGraderFailure.Unavailable, "Ollama isn't running. Start it on this Mac to get feedback.");
        }

        return Parse(envelope, request);
    }

    private static AnswerFeedback Parse(string envelope, GradeRequest request)
    {
        try
        {
            using var outer = JsonDocument.Parse(envelope);
            if (outer.RootElement.ValueKind != JsonValueKind.Object
                || !outer.RootElement.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                throw Bad();

            using var doc = JsonDocument.Parse(content.GetString()!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad();
            return new AnswerFeedback(Points(root, request), RedFlags(root, request));
        }
        catch (JsonException) { throw Bad(); }
    }

    private static List<PointFeedback> Points(JsonElement root, GradeRequest request)
    {
        if (!root.TryGetProperty("points", out var array) || array.ValueKind != JsonValueKind.Array) throw Bad();
        var seen = new SortedDictionary<int, PointFeedback>();
        foreach (var item in array.EnumerateArray())
        {
            var index = Index(item, request.RubricPoints.Count);
            if (!item.TryGetProperty("hit", out var hit) || hit.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad();
            if (!seen.TryAdd(index, new PointFeedback(index, request.RubricPoints[index], hit.GetBoolean(), Note(item)))) throw Bad();
        }
        if (seen.Count != request.RubricPoints.Count) throw Bad();
        return [.. seen.Values];
    }

    // A model can report a flag it saw twice; the learner needs to hear it once. An index the step doesn't have is a bad answer.
    private static List<FlagFinding> RedFlags(JsonElement root, GradeRequest request)
    {
        if (!root.TryGetProperty("redFlagsFound", out var array) || array.ValueKind != JsonValueKind.Array) throw Bad();
        var seen = new SortedDictionary<int, FlagFinding>();
        foreach (var item in array.EnumerateArray())
        {
            var index = Index(item, request.RedFlags.Count);
            seen.TryAdd(index, new FlagFinding(index, request.RedFlags[index], Note(item)));
        }
        return [.. seen.Values];
    }

    private static int Index(JsonElement item, int count)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var i)
            || i < 0 || i >= count)
            throw Bad();
        return i;
    }

    private static string Note(JsonElement item)
    {
        if (!item.TryGetProperty("note", out var note) || note.ValueKind == JsonValueKind.Null) return "";
        if (note.ValueKind != JsonValueKind.String) throw Bad();
        var text = note.GetString()!.Trim();
        return text.Length <= MaxNote ? text : text[..(MaxNote - 1)] + "…";
    }

    private static AnswerGraderException Bad() => new(AnswerGraderFailure.BadResponse, "The model's feedback couldn't be understood.");
}
