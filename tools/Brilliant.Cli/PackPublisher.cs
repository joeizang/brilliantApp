using Brilliant.Core.Content;
using Brilliant.Core.Sync;

namespace Brilliant.Cli;

/// <summary>
/// <c>brilliant publish</c>: validates the content and, only if it is valid, uploads the pack to the paired server
/// (docs/content-distribution.md). Any paired device may publish; the server checks the pack again.
/// </summary>
public sealed class PackPublisher(PackClient client, IDeviceCredentialStore credentials, IReferenceSolutionRunner? runner = null)
{
    public async Task<int> PublishAsync(string contentRoot, TextWriter output, TextWriter error, CancellationToken ct = default, string? appManifestPath = null)
    {
        var bytes = ContentPacker.PackToBytes(contentRoot, out var report, runner, appManifestPath);
        CliApp.WriteWarnings(report, error);
        if (bytes is null)
        {
            foreach (var e in report.Errors) error.WriteLine($"error: {e}");
            error.WriteLine($"{report.Errors.Count} error(s); nothing was published.");
            return 1;
        }

        var paired = await credentials.LoadAsync(ct);
        if (paired is null)
        {
            error.WriteLine("error: this computer is not paired with a server. Run: brilliant pair <server> <code>");
            return 1;
        }

        try
        {
            var result = await client.PublishAsync(paired, bytes, ct);
            var pack = result.Pack;
            output.WriteLine(result.Created
                ? $"OK: published {pack.PackId} {pack.Version} ({pack.Size:N0} bytes) to {paired.Server}"
                : $"OK: {pack.PackId} {pack.Version} was already published with these exact contents; nothing changed.");
            if (result.RemovedIds.Count > 0)
            {
                output.WriteLine($"warning: this version no longer has {result.RemovedIds.Count} ID(s) the previous version had. Progress on them is kept, but they stop showing:");
                foreach (var id in result.RemovedIds) output.WriteLine($"  {id}");
            }
            return 0;
        }
        catch (PackRejectedException e)
        {
            error.WriteLine($"error: the server refused the pack: {e.Message}");
            return 1;
        }
        catch (SyncException e)
        {
            error.WriteLine($"error: could not publish: {Describe(e)}");
            return 1;
        }
    }

    private static string Describe(SyncException e) => e.Failure switch
    {
        SyncFailure.Unauthorized => "the server no longer accepts this computer. Pair it again: brilliant pair <server> <code>",
        SyncFailure.Untrusted => CliPairing.UntrustedAdvice,
        SyncFailure.Unreachable => $"the server could not be reached ({e.Detail}).",
        _ => e.Detail ?? e.Failure.ToString(),
    };
}

/// <summary><c>brilliant pair</c>: pairs this computer with the household server so it can publish.</summary>
public sealed class CliPairing(PairingService pairing)
{
    /// <summary>The CLI trusts what the computer trusts, so it works once the household's certificate authority is installed here.</summary>
    public const string UntrustedAdvice = "this computer does not trust the server's certificate. Install the household's certificate authority on it (docs/https.md).";

    public async Task<int> PairAsync(string server, string code, string name, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        var outcome = await pairing.PairAsync(server, code, name, ct);
        if (outcome.Credentials is { } paired)
        {
            output.WriteLine($"OK: paired with {paired.Server} as '{name}'.");
            return 0;
        }
        error.WriteLine("error: " + outcome.Failure switch
        {
            PairingFailure.BadAddress => "that is not a server address (try 192.168.1.20:8443; the server speaks https).",
            PairingFailure.CodeRejected => "the code is wrong, expired or already used. Mint a new one: docker compose exec -T server dotnet Brilliant.Server.dll pairing-code",
            PairingFailure.TooManyAttempts => "too many wrong tries; wait a minute.",
            PairingFailure.Rejected => "the server refused the request (is the device name empty?).",
            PairingFailure.CouldNotSave => $"paired, but the credentials could not be saved ({outcome.Detail}). Revoke the device on the server and try again.",
            PairingFailure.Untrusted => UntrustedAdvice,
            _ => $"the server could not be reached ({outcome.Detail}).",
        });
        return 1;
    }
}
