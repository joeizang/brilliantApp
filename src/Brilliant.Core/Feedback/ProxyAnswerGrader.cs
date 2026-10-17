using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Brilliant.Core.Sync;

namespace Brilliant.Core.Feedback;

/// <summary>
/// The phone's grader: sends the written answer to the household server's <c>POST /feedback/grade</c>, which asks the Ollama on the Mac.
/// It is <see cref="IAnswerGrader.Enabled"/> only while the learner has opted in (<see cref="IFeedbackSettings"/>). Whatever goes
/// wrong, including the Mac being asleep, comes back as an <see cref="AnswerGraderException"/> whose message is plain enough to show.
/// The wording of each point comes from the local request, never from the server's reply.
/// </summary>
public sealed class ProxyAnswerGrader(HttpClient http, IDeviceCredentialStore credentials, IFeedbackSettings settings, string appId) : IAnswerGrader
{
    public const string UnavailableMessage = "Feedback unavailable right now. Your answer is saved; ask for feedback on it later from your Mac.";
    private const int MaxNote = 400;

    public bool Enabled => settings.UseServer;

    public async Task<AnswerFeedback> GradeAsync(GradeRequest request, CancellationToken ct = default)
    {
        if (!Enabled) throw new AnswerGraderException(AnswerGraderFailure.Unavailable, "Feedback through the server is turned off.");
        var device = await credentials.LoadAsync(ct);
        if (device is null || !string.Equals(device.AppId, appId, StringComparison.Ordinal))
            throw new AnswerGraderException(AnswerGraderFailure.Unavailable, "Pair this device with the server to get feedback.");

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(device.Server, "feedback/grade")) { Content = JsonContent.Create(request) };
            message.Headers.Add(AppSync.AppIdHeader, appId);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.Token);
            using var response = await http.SendAsync(message, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new AnswerGraderException(AnswerGraderFailure.Unavailable, "The server no longer accepts this device. Pair it again to get feedback.");
            if (!response.IsSuccessStatusCode) throw await Refusal(response, ct);
            return Check(await response.Content.ReadFromJsonAsync<AnswerFeedback>(ct), request);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            throw new AnswerGraderException(AnswerGraderFailure.Unavailable, UnavailableMessage);
        }
        catch (JsonException)
        {
            throw Bad();
        }
    }

    // The server's { failure, error } says why; anything else it might send (an old server, a proxy's error page) is just "unavailable".
    private static async Task<AnswerGraderException> Refusal(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("failure", out var failure) && failure.ValueKind == JsonValueKind.String)
            {
                var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                switch (failure.GetString())
                {
                    case nameof(AnswerGraderFailure.ModelMissing) when !string.IsNullOrWhiteSpace(error):
                        return new AnswerGraderException(AnswerGraderFailure.ModelMissing, error);
                    case nameof(AnswerGraderFailure.BadResponse):
                        return Bad();
                }
            }
        }
        catch (JsonException) { }
        return new AnswerGraderException(AnswerGraderFailure.Unavailable, UnavailableMessage);
    }

    // Rebuilt from the local request: every rubric point exactly once, and only red flags the step has.
    private static AnswerFeedback Check(AnswerFeedback? reply, GradeRequest request)
    {
        if (reply?.Points is null || reply.RedFlagsFound is null || reply.Points.Any(p => p is null) || reply.RedFlagsFound.Any(f => f is null)) throw Bad();
        if (reply.Points.Count != request.RubricPoints.Count || reply.Points.Select(p => p.Index).Distinct().Count() != reply.Points.Count) throw Bad();
        if (reply.Points.Any(p => p.Index < 0 || p.Index >= request.RubricPoints.Count)) throw Bad();
        if (reply.RedFlagsFound.Any(f => f.Index < 0 || f.Index >= request.RedFlags.Count)) throw Bad();

        return new AnswerFeedback(
            [.. reply.Points.OrderBy(p => p.Index).Select(p => new PointFeedback(p.Index, request.RubricPoints[p.Index], p.Hit, Note(p.Note)))],
            [.. reply.RedFlagsFound.DistinctBy(f => f.Index).OrderBy(f => f.Index).Select(f => new FlagFinding(f.Index, request.RedFlags[f.Index], Note(f.Note)))]);
    }

    private static string Note(string? note)
    {
        var text = (note ?? "").Trim();
        return text.Length <= MaxNote ? text : text[..(MaxNote - 1)] + "…";
    }

    private static AnswerGraderException Bad() => new(AnswerGraderFailure.BadResponse, "The model's feedback couldn't be understood.");
}
