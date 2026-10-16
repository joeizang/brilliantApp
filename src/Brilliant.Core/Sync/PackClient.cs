using Brilliant.Core.Apps;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Brilliant.Core.Sync;

/// <summary>The server refused a pack (HTTP <see cref="Status"/>) and says why in <see cref="Exception.Message"/>.</summary>
public sealed class PackRejectedException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status => status;
}

/// <summary>Talks to the server's <c>/packs</c> endpoints. The <see cref="HttpClient"/> is injected so tests can answer for the server.</summary>
public sealed class PackClient(HttpClient http, string appId = AppStoragePaths.DsaAppId)
{
    public string AppId => appId;

    public async Task<IReadOnlyList<PackInfo>> ListAsync(DeviceCredentials credentials, CancellationToken ct = default)
    {
        using var request = Authorised(HttpMethod.Get, new Uri(credentials.Server, "packs"), credentials);
        var list = await SendAsync(request, async response =>
            await response.Content.ReadFromJsonAsync<PackListResponse>(ct) ?? throw new SyncException(SyncFailure.ServerError, "The server's answer was empty."), ct);
        // A 200 can still carry nonsense ({"packs":null}, null entries): that is the server misbehaving, not something to blow up on later.
        if (list.Packs is null || list.Packs.Any(p => p is null || p.PackId is null || p.Version is null || p.Sha256 is null || p.AppId != appId))
            throw new SyncException(SyncFailure.ServerError, "The server's list of packs was malformed.");
        return list.Packs;
    }

    /// <summary>The pack's bytes, checked against <see cref="PackInfo.Sha256"/> and <see cref="PackInfo.Size"/>.</summary>
    public async Task<byte[]> DownloadAsync(DeviceCredentials credentials, PackInfo pack, CancellationToken ct = default)
    {
        if (pack.AppId != appId) throw new SyncException(SyncFailure.ServerError, "The server listed a pack for a different app.");
        // The pack's ID comes from the server; it must not be able to steer the request anywhere but /packs.
        if (!Content.PackIds.IsValid(pack.PackId) || !Content.PackVersion.TryParse(pack.Version, out var version))
            throw new SyncException(SyncFailure.ServerError, "The server listed a pack with an unusable ID or version.");

        using var request = Authorised(HttpMethod.Get, new Uri(credentials.Server, $"packs/{Uri.EscapeDataString(pack.PackId)}/{version}"), credentials);
        var bytes = await SendAsync(request, async response => await response.Content.ReadAsByteArrayAsync(ct), ct);
        if (bytes.LongLength != pack.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), pack.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new SyncException(SyncFailure.ServerError, "The downloaded pack does not match what the server listed.");
        return bytes;
    }

    /// <summary>Uploads a pack. A pack the server refuses throws <see cref="PackRejectedException"/> with its reason.</summary>
    public async Task<PublishPackResponse> PublishAsync(DeviceCredentials credentials, byte[] pack, CancellationToken ct = default)
    {
        using var request = Authorised(HttpMethod.Post, new Uri(credentials.Server, "packs"), credentials);
        request.Content = new ByteArrayContent(pack);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return await SendAsync(request, async response =>
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge)
                throw new PackRejectedException(response.StatusCode, await ReasonAsync(response, ct));
            if (!response.IsSuccessStatusCode) throw new SyncException(SyncFailure.ServerError, $"The server answered {(int)response.StatusCode}.");
            var published = await response.Content.ReadFromJsonAsync<PublishPackResponse>(ct) ?? throw new SyncException(SyncFailure.ServerError, "The server's answer was empty.");
            if (published.Pack is null || published.Pack.AppId != appId)
                throw new SyncException(SyncFailure.ServerError, "The server published a pack for a different app.");
            return published;
        }, ct, passRefusals: true);
    }

    private static async Task<string> ReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) return error.GetString()!;
        }
        catch (JsonException) { }
        return $"The server refused the pack ({(int)response.StatusCode}).";
    }

    private HttpRequestMessage Authorised(HttpMethod method, Uri uri, DeviceCredentials credentials)
    {
        AppSync.RequireCredentials(credentials, appId);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(AppSync.AppIdHeader, appId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        return request;
    }

    // passRefusals: let the caller see a 400, 409 or 413, which publishing turns into a reason for the person at the keyboard.
    private async Task<T> SendAsync<T>(HttpRequestMessage request, Func<HttpResponseMessage, Task<T>> read, CancellationToken ct, bool passRefusals = false)
    {
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SyncException(SyncFailure.Unauthorized);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new SyncException(SyncFailure.ServerError, "The server has no such pack (is it an older server?).");
            if (!response.IsSuccessStatusCode && !(passRefusals && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge))
                throw new SyncException(SyncFailure.ServerError, $"The server answered {(int)response.StatusCode}.");
            return await read(response);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new SyncException(e is JsonException ? SyncFailure.ServerError : UntrustedCertificate.Explains(e) ? SyncFailure.Untrusted : SyncFailure.Unreachable, e.GetBaseException().Message, e);
        }
    }
}
