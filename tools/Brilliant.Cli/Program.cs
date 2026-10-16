using Brilliant.Cli;
using Brilliant.Core.Sync;

if (args is ["--csharp-reference-worker"])
    return CSharpReferenceSolutionRunner.RunWorker(Console.In, Console.Out);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
var credentialsPath = Environment.GetEnvironmentVariable("BRILLIANT_CLI_CREDENTIALS")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".brilliant", "cli-credentials.json");

try
{
    return await CliApp.RunAsync(args, Console.Out, Console.Error, http,
        new FileDeviceCredentialStore(CliApp.CredentialPath(args, credentialsPath)));
}
catch (Exception ex) when (ex is Brilliant.Core.Apps.AppManifestException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"error: cannot select app credentials: {ex.Message}");
    return 1;
}
