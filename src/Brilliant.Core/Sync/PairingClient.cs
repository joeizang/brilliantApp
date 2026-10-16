using Brilliant.Core.Apps;
using System.Net.Http.Json;

namespace Brilliant.Core.Sync;

public enum PairingFailure
{
    /// <summary>The address isn't one we can use.</summary>
    BadAddress,

    /// <summary>The server couldn't be reached, or didn't answer like a Brilliant server.</summary>
    Unreachable,

    /// <summary>The server answered, but this device doesn't trust its certificate: the household's certificate authority isn't installed on it (docs/https.md).</summary>
    Untrusted,

    /// <summary>The server answered, but the code is wrong, expired or already used.</summary>
    CodeRejected,

    /// <summary>Too many wrong tries; wait a minute.</summary>
    TooManyAttempts,

    /// <summary>The server refused the request itself (e.g. an empty device name).</summary>
    Rejected,

    /// <summary>The server paired this device, but the token couldn't be kept on it. The device is listed on the server and can be revoked there.</summary>
    CouldNotSave,
}

/// <summary><paramref name="Detail"/> is the underlying error for an unreachable server (e.g. "Connection refused"), for the screen to show beside its explanation.</summary>
public sealed record PairingOutcome(DeviceCredentials? Credentials, PairingFailure? Failure, string? Detail = null)
{
    public bool Succeeded => Credentials is not null;
    public static PairingOutcome Success(DeviceCredentials credentials) => new(credentials, null);
    public static PairingOutcome Failed(PairingFailure failure, string? detail = null) => new(null, failure, detail);
}

/// <summary>Talks to the server's <c>POST /pair</c>. The <see cref="HttpClient"/> is injected so tests can answer for the server.</summary>
public sealed class PairingClient(HttpClient http, string appId = AppStoragePaths.DsaAppId)
{
    public async Task<PairingOutcome> PairAsync(string address, string code, string deviceName, CancellationToken ct = default)
    {
        if (!ServerAddress.TryParse(address, out var server)) return PairingOutcome.Failed(PairingFailure.BadAddress);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "pair"));
            request.Headers.Add(AppSync.AppIdHeader, appId);
            request.Content = JsonContent.Create(new PairRequest(code, deviceName, appId));
            using var response = await http.SendAsync(request, ct);
            switch ((int)response.StatusCode)
            {
                case 200:
                    var paired = await response.Content.ReadFromJsonAsync<PairResponse>(ct);
                    return paired is { Token.Length: > 0, DeviceId.Length: > 0 } && paired.AppId == appId
                        ? PairingOutcome.Success(new DeviceCredentials(server, paired.DeviceId, paired.Token, appId))
                        : PairingOutcome.Failed(PairingFailure.Unreachable);
                case 401: return PairingOutcome.Failed(PairingFailure.CodeRejected);
                case 429: return PairingOutcome.Failed(PairingFailure.TooManyAttempts);
                case 400: return PairingOutcome.Failed(PairingFailure.Rejected);
                default: return PairingOutcome.Failed(PairingFailure.Unreachable);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            return PairingOutcome.Failed(UntrustedCertificate.Explains(e) ? PairingFailure.Untrusted : PairingFailure.Unreachable, e.GetBaseException().Message);
        }
    }
}

/// <summary>Pairs this device and remembers the result. The learner's whole view of "am I connected to my server" goes through here.</summary>
public sealed class PairingService(PairingClient client, IDeviceCredentialStore store)
{
    public Task<DeviceCredentials?> CurrentAsync(CancellationToken ct = default) => store.LoadAsync(ct);

    public async Task<PairingOutcome> PairAsync(string address, string code, string deviceName, CancellationToken ct = default)
    {
        var outcome = await client.PairAsync(address, code, deviceName, ct);
        if (outcome.Credentials is not { } credentials) return outcome;

        try
        {
            await store.SaveAsync(credentials, ct);
            return outcome;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return PairingOutcome.Failed(PairingFailure.CouldNotSave, e.GetBaseException().Message);
        }
    }

    /// <summary>Forgets the server on this device. It stays in the server's device list until it is revoked there.</summary>
    public Task ForgetAsync(CancellationToken ct = default) => store.ClearAsync(ct);
}
