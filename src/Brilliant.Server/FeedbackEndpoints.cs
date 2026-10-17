using Brilliant.Core.Feedback;
using Brilliant.Server.Pairing;
using Microsoft.Extensions.Options;

namespace Brilliant.Server;

/// <summary>Where the server finds Ollama (the Mac's LAN address), and how long it waits. Blank <see cref="OllamaUrl"/> means feedback is off.</summary>
public sealed class FeedbackOptions
{
    public string? OllamaUrl { get; set; }

    /// <summary>How long to wait to connect. An Ollama that is asleep or off the network fails in this long, not after the whole <see cref="TimeoutSeconds"/>.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 3;

    /// <summary>How long a model gets to answer, once connected. A local 12B model can take a while on a first request.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Grading proxy for devices that can't run Ollama themselves (the phone): <c>POST /feedback/grade</c> forwards a written answer to the
/// Ollama the server is configured with and returns the structured feedback (docs/csharp-answer-feedback.md). It reuses
/// <see cref="OllamaAnswerGrader"/>, so a phone gets exactly what the Mac would. Refusals carry <c>{ failure, error }</c>, the failure
/// being an <see cref="AnswerGraderFailure"/> name; the error never names the Ollama address.
/// </summary>
public static class FeedbackEndpoints
{
    public const string OllamaClient = "ollama";

    /// <summary>The longest any one text in a grading request may be; a justification is a few sentences.</summary>
    public const int MaxTextLength = 8_000;
    private const int MaxListItems = 20;

    public static void MapFeedbackEndpoints(this RouteGroupBuilder group) => group.MapPost("/feedback/grade", Grade);

    /// <summary>Registers the HTTP client the proxy uses for Ollama, with the configured connect and response timeouts.</summary>
    public static void AddFeedbackProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FeedbackOptions>(configuration.GetSection("Feedback"));
        services.AddHttpClient(OllamaClient)
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, sp.GetRequiredService<IOptions<FeedbackOptions>>().Value.ConnectTimeoutSeconds)),
            })
            .ConfigureHttpClient((sp, http) =>
                http.Timeout = TimeSpan.FromSeconds(Math.Max(1, sp.GetRequiredService<IOptions<FeedbackOptions>>().Value.TimeoutSeconds)));
    }

    private static async Task<IResult> Grade(GradeRequest? request, System.Security.Claims.ClaimsPrincipal user, ServerApps apps,
        IOptions<FeedbackOptions> options, IHttpClientFactory http, ILogger<FeedbackOptions> log, CancellationToken ct)
    {
        if (Problem(request) is { } why) return Results.BadRequest(new { error = why });
        if (apps.GraderModel(user.AppId()) is not { } model)
            return Results.NotFound(new { failure = nameof(AnswerGraderFailure.Unavailable), error = "This app doesn't give written-answer feedback." });
        if (!Uri.TryCreate(options.Value.OllamaUrl?.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            return Refuse(AnswerGraderFailure.Unavailable, "The server isn't set up to give feedback (Feedback:OllamaUrl is not set).");

        try
        {
            var feedback = await new OllamaAnswerGrader(http.CreateClient(OllamaClient), model, endpoint).GradeAsync(request!, ct);
            return Results.Ok(feedback);
        }
        catch (AnswerGraderException e)
        {
            // OllamaAnswerGrader's own words suit the Mac ("start it on this Mac"); the phone is told the Mac can't be reached, never where.
            log.LogWarning("Feedback unavailable: {Failure}: {Message}", e.Failure, e.Message);
            return e.Failure switch
            {
                AnswerGraderFailure.ModelMissing => Refuse(e.Failure, e.Message),
                AnswerGraderFailure.BadResponse => Refuse(e.Failure, e.Message, StatusCodes.Status502BadGateway),
                _ => Refuse(AnswerGraderFailure.Unavailable, "The server couldn't get feedback from Ollama right now."),
            };
        }
    }

    private static IResult Refuse(AnswerGraderFailure failure, string error, int status = StatusCodes.Status503ServiceUnavailable) =>
        Results.Json(new { failure = failure.ToString(), error }, statusCode: status);

    private static string? Problem(GradeRequest? r)
    {
        if (r is null || r.RubricPoints is null || r.RedFlags is null) return "Send the scenario, option, answer, model answer, rubric points and red flags.";
        if (r.RubricPoints.Count is 0 or > MaxListItems || r.RedFlags.Count > MaxListItems) return $"Send between 1 and {MaxListItems} rubric points and at most {MaxListItems} red flags.";
        if (string.IsNullOrWhiteSpace(r.Answer)) return "The answer is empty.";
        foreach (var text in new[] { r.Scenario, r.Option, r.Answer, r.ModelAnswer }.Concat(r.RubricPoints).Concat(r.RedFlags))
        {
            if (text is null) return "A text in the request is missing.";
            if (text.Length > MaxTextLength) return $"A text in the request is over {MaxTextLength} characters.";
        }
        return null;
    }
}
