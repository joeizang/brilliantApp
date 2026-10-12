using Brilliant.Core.Sync;

namespace Brilliant.Cli;

/// <summary>The command line, apart from <c>Main</c> so tests can run it with a fake server and a temporary credentials file.</summary>
public static class CliApp
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpClient http, IDeviceCredentialStore credentials,
        IReferenceSolutionRunner? runner = null, CancellationToken ct = default)
    {
        switch (args)
        {
            case ["validate", var root]:
                return Report(ContentValidator.Validate(root, runner), root, output, error, "valid");

            case ["pack", var root, "-o" or "--output", var outPath]:
                return Report(ContentPacker.Pack(root, outPath, runner), root, output, error, $"packed to {outPath}");

            case ["publish", var root]:
                return await new PackPublisher(new PackClient(http), credentials, runner).PublishAsync(root, output, error, ct);

            case ["pair", var server, var code]:
                return await Pair(server, code, Environment.MachineName, http, credentials, output, error, ct);

            case ["pair", var server, var code, "--name", var name]:
                return await Pair(server, code, name, http, credentials, output, error, ct);

            default:
                return Usage(error);
        }
    }

    private static Task<int> Pair(string server, string code, string name, HttpClient http, IDeviceCredentialStore credentials, TextWriter output, TextWriter error, CancellationToken ct) =>
        new CliPairing(new PairingService(new PairingClient(http), credentials)).PairAsync(server, code, name, output, error, ct);

    private static int Report(ValidationReport report, string root, TextWriter output, TextWriter error, string success)
    {
        if (report.IsValid)
        {
            output.WriteLine($"OK: {root} {success}");
            return 0;
        }
        foreach (var e in report.Errors) error.WriteLine($"error: {e}");
        error.WriteLine($"{report.Errors.Count} error(s); nothing was published.");
        return 1;
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("usage: brilliant validate <contentRoot>");
        error.WriteLine("       brilliant pack <contentRoot> -o <pack.zip>");
        error.WriteLine("       brilliant pair <server> <code> [--name <name>]    (once; the code comes from the server: docs/server.md)");
        error.WriteLine("       brilliant publish <contentRoot>                   validate, then upload the pack to the paired server");
        return 2;
    }
}
