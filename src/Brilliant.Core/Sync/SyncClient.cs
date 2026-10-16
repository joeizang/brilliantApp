using Brilliant.Core.Apps;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Brilliant.Core.Progress;

namespace Brilliant.Core.Sync;

public enum SyncFailure
{
    /// <summary>This device isn't paired with a server.</summary>
    NotPaired,

    /// <summary>The server couldn't be reached (away from home, switched off, wrong network).</summary>
    Unreachable,

    /// <summary>The server answered, but this device doesn't trust its certificate: the household's certificate authority isn't installed on it (docs/https.md).</summary>
    Untrusted,

    /// <summary>The server answered but no longer accepts this device's token: it was revoked, or the server was reset.</summary>
    Unauthorized,

    /// <summary>The server answered with something unexpected.</summary>
    ServerError,

    /// <summary>This device couldn't read or write its own data (a full disk, a locked file).</summary>
    LocalError,
}

public sealed class SyncException(SyncFailure failure, string? detail = null, Exception? inner = null) : Exception(detail ?? failure.ToString(), inner)
{
    public SyncFailure Failure => failure;
    public string? Detail => detail;
}

/// <summary>Talks to the server's <c>/events</c> and <c>/drafts</c> endpoints. The <see cref="HttpClient"/> is injected so tests can answer for the server.</summary>
public sealed class SyncClient(HttpClient http, string appId = AppStoragePaths.DsaAppId)
{
    public async Task<PushEventsResponse> PushAsync(DeviceCredentials credentials, IReadOnlyList<ProgressEvent> events, CancellationToken ct = default)
    {
        using var request = Authorised(HttpMethod.Post, new Uri(credentials.Server, "events"), credentials);
        request.Content = JsonContent.Create(new PushEventsRequest(events));
        return await SendAsync<PushEventsResponse>(request, ct);
    }

    public async Task<PullEventsResponse> PullAsync(DeviceCredentials credentials, long after, CancellationToken ct = default)
    {
        using var request = Authorised(HttpMethod.Get, new Uri(credentials.Server, $"events?after={after}"), credentials);
        return await SendAsync<PullEventsResponse>(request, ct);
    }

    public async Task<PushDraftsResponse> PushDraftsAsync(DeviceCredentials credentials, IReadOnlyList<Drafts.CodeDraft> drafts, CancellationToken ct = default)
    {
        using var request = Authorised(HttpMethod.Post, new Uri(credentials.Server, "drafts"), credentials);
        request.Content = JsonContent.Create(new PushDraftsRequest(drafts));
        return await SendAsync<PushDraftsResponse>(request, ct);
    }

    public async Task<PullDraftsResponse> PullDraftsAsync(DeviceCredentials credentials, long after, CancellationToken ct = default)
    {
        using var request = Authorised(HttpMethod.Get, new Uri(credentials.Server, $"drafts?after={after}"), credentials);
        return await SendAsync<PullDraftsResponse>(request, ct);
    }

    private HttpRequestMessage Authorised(HttpMethod method, Uri uri, DeviceCredentials credentials)
    {
        AppSync.RequireCredentials(credentials, appId);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(AppSync.AppIdHeader, appId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SyncException(SyncFailure.Unauthorized);
            if (!response.IsSuccessStatusCode) throw new SyncException(SyncFailure.ServerError, $"The server answered {(int)response.StatusCode}.");
            return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new SyncException(SyncFailure.ServerError, "The server's answer was empty.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new SyncException(e is JsonException ? SyncFailure.ServerError : UntrustedCertificate.Explains(e) ? SyncFailure.Untrusted : SyncFailure.Unreachable, e.GetBaseException().Message, e);
        }
    }
}
