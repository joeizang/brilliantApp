using Brilliant.Cli;
using Brilliant.Core.Sync;

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
var credentialsPath = Environment.GetEnvironmentVariable("BRILLIANT_CLI_CREDENTIALS")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".brilliant", "cli-credentials.json");

return await CliApp.RunAsync(args, Console.Out, Console.Error, http, new FileDeviceCredentialStore(credentialsPath));
