using Brilliant.Core.Apps;
using Brilliant.Core.Sync;

namespace Brilliant.Cli;

/// <summary>The command line, apart from <c>Main</c> so tests can run it with a fake server and a temporary credentials file.</summary>
public static class CliApp
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpClient http, IDeviceCredentialStore credentials,
        IReferenceSolutionRunner? runner = null, CancellationToken ct = default)
    {
        string? appManifestPath = null;
        if (args is ["validate" or "pack" or "publish", "--app", var appManifest, .. var options])
        {
            try
            {
                var source = AppContentSource.FromManifest(appManifest);
                var operation = args[0];
                appManifestPath = appManifest;
                args = operation == "pack" && options.Length == 0
                    ? [operation, source.Root, "-o", Path.Combine("dist", new FileInfo(appManifest).Directory!.Name, source.App.ContentPack)]
                    : [operation, source.Root, .. options];
            }
            catch (Exception ex) when (ex is Brilliant.Core.Apps.AppManifestException or IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"error: cannot load app manifest: {ex.Message}");
                return 1;
            }
        }
        if (args is ["validate" or "pack" or "publish", ..] && args is [.. var command, "--manifest", var selectedManifest])
        {
            appManifestPath = selectedManifest;
            args = command;
        }
        var appId = AppStoragePaths.DsaAppId;
        try
        {
            if (args is ["pair", ..] && args is [.. var pairingArgs, "--app", var pairingManifest])
            {
                appId = AppContentSource.FromManifest(pairingManifest).App.AppId;
                args = pairingArgs;
            }
            else if (args is ["publish", var publishRoot])
                appId = SelectedAppId(publishRoot, appManifestPath);
        }
        catch (Exception ex) when (ex is AppManifestException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"error: cannot select app: {ex.Message}");
            return 1;
        }
        switch (args)
        {
            case ["app-runtimes", var manifestPath, var outputPath]:
                try
                {
                    AppBuildInputs.WriteRuntimeSelection(manifestPath, outputPath);
                    return 0;
                }
                catch (Exception ex) when (ex is Brilliant.Core.Apps.AppManifestException or IOException or UnauthorizedAccessException)
                {
                    error.WriteLine($"error: {ex.Message}");
                    return 1;
                }

            case ["prepare-app", var manifest, var template, var repository, var generated]:
                try
                {
                    AppBuildInputs.Write(manifest, template, repository, generated);
                    return 0;
                }
                catch (Exception ex) when (ex is Brilliant.Core.Apps.AppManifestException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    error.WriteLine($"error: {ex.Message}");
                    return 1;
                }

            case ["validate", var root]:
                return Report(ContentValidator.Validate(root, runner, appManifestPath), root, output, error, "valid");

            case ["pack", var root, "-o" or "--output", var outPath]:
                return Report(ContentPacker.Pack(root, outPath, runner, appManifestPath), root, output, error, $"packed to {outPath}");

            case ["publish", var root]:
                return await new PackPublisher(new PackClient(http, appId), credentials, runner).PublishAsync(root, output, error, ct, appManifestPath);

            case ["pair", var server, var code]:
                return await Pair(server, code, Environment.MachineName, http, credentials, output, error, ct, appId);

            case ["pair", var server, var code, "--name", var name]:
                return await Pair(server, code, name, http, credentials, output, error, ct, appId);

            default:
                return Usage(error);
        }
    }

    private static Task<int> Pair(string server, string code, string name, HttpClient http, IDeviceCredentialStore credentials, TextWriter output, TextWriter error, CancellationToken ct, string appId) =>
        new CliPairing(new PairingService(new PairingClient(http, appId), credentials)).PairAsync(server, code, name, output, error, ct);

    internal static string SelectedAppId(string contentRoot, string? manifestPath) =>
        (manifestPath is null ? AppContentSource.Infer(contentRoot) : AppContentSource.FromManifest(manifestPath).App)?.AppId
            ?? AppStoragePaths.DsaAppId;

    /// <summary>Retains the existing DSA credentials file; other app identities receive their own sibling directory.</summary>
    public static string CredentialPath(string[] args, string legacyPath)
    {
        string? manifest = null;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] is "--app" or "--manifest") manifest = args[i + 1];
        var appId = manifest is not null ? AppContentSource.FromManifest(manifest).App.AppId
            : args is ["publish", var root, ..] ? SelectedAppId(root, null) : AppStoragePaths.DsaAppId;
        return appId == AppStoragePaths.DsaAppId ? legacyPath
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(legacyPath))!, "apps", appId, Path.GetFileName(legacyPath));
    }

    private static int Report(ValidationReport report, string root, TextWriter output, TextWriter error, string success)
    {
        WriteWarnings(report, error);
        if (report.IsValid)
        {
            output.WriteLine($"OK: {root} {success}");
            return 0;
        }
        foreach (var e in report.Errors) error.WriteLine($"error: {e}");
        error.WriteLine($"{report.Errors.Count} error(s); nothing was published.");
        return 1;
    }

    /// <summary>Warnings never fail a command; they are printed whether or not the content is valid.</summary>
    internal static void WriteWarnings(ValidationReport report, TextWriter error)
    {
        foreach (var w in report.Warnings) error.WriteLine($"warning: {w}");
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("usage: brilliant validate <contentRoot> [--manifest <apps/app/manifest.yaml>]");
        error.WriteLine("       brilliant <validate|pack|publish> --app <apps/app/manifest.yaml> [pack: -o <pack.zip>]");
        error.WriteLine("       brilliant prepare-app <manifest.yaml> <index.html> <repositoryRoot> <outputDirectory>");
        error.WriteLine("       brilliant pack <contentRoot> -o <pack.zip> [--manifest <manifest.yaml>]");
        error.WriteLine("       brilliant pair <server> <code> [--name <name>] [--app <manifest.yaml>]    (once; the code comes from the server: docs/server.md)");
        error.WriteLine("       brilliant publish <contentRoot> [--manifest <manifest.yaml>]  validate, then upload to the paired server");
        return 2;
    }
}
