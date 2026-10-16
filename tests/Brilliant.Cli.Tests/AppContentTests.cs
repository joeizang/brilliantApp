using Brilliant.Core.Content;
using Brilliant.Core.Sync;
using Brilliant.Core.Code;
using System.Net;
using System.Net.Http.Json;

namespace Brilliant.Cli.Tests;

public sealed class AppContentTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-app-content-" + Guid.NewGuid());
    private string Root => Path.Combine(_repository, "content", "sample");
    private string Manifest => Path.Combine(_repository, "apps", "sample", "manifest.yaml");

    public AppContentTests()
    {
        Write("apps/sample/manifest.yaml", """
            appId: example.sample
            displayName: Sample
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [python]
            stepTypes: [choice]
            """);
        Write("content/sample/pack.yaml", "id: pack.sample\nversion: 1.0.0\n");
        Write("content/sample/tracks/t/track.yaml", "id: track.sample\ntitle: Sample\n");
        Write("content/sample/tracks/t/l/lesson.yaml", """
            id: lesson.sample
            title: Sample
            steps:
              - id: step.sample
                type: explain
                title: Intro
                body: Hello
            """);
    }

    [Fact]
    public void Content_folder_infers_its_app_and_rejects_disabled_lesson_steps()
    {
        var report = ContentValidator.Validate(Root);

        var error = Assert.Single(report.Errors);
        Assert.Equal("tracks/t/l/lesson.yaml", error.File);
        Assert.Contains("step.sample", error.Message);
        Assert.Contains("explain", error.Message);
        Assert.Contains("example.sample", error.Message);
        Assert.Contains("not enabled", error.Message);
    }

    [Fact]
    public async Task Explicit_manifest_selects_shared_content_and_its_own_allow_list()
    {
        Write("apps/other/manifest.yaml", File.ReadAllText(Manifest)
            .Replace("example.sample", "example.other") + "\ncontentRoot: content/sample\n");
        var errors = new StringWriter();
        using var http = new HttpClient();
        var code = await CliApp.RunAsync(["validate", Root, "--manifest", Path.Combine(_repository, "apps/other/manifest.yaml")],
            new StringWriter(), errors, http, new FileDeviceCredentialStore(Path.Combine(_repository, "credentials.json")));

        Assert.Equal(1, code);
        Assert.Contains("example.other", errors.ToString());
        Assert.Contains("not enabled", errors.ToString());
    }

    [Theory]
    [InlineData("explain")]
    [InlineData("choice")]
    [InlineData("predict-output")]
    [InlineData("write-code")]
    [InlineData("fill-blank")]
    [InlineData("parsons")]
    [InlineData("trace")]
    public void Every_known_step_type_obeys_the_app_allow_list(string type)
    {
        File.WriteAllText(Manifest, File.ReadAllText(Manifest).Replace("[choice]", type == "explain" ? "[choice]" : "[explain]"));
        var lesson = Path.Combine(Root, "tracks/t/l/lesson.yaml");
        File.WriteAllText(lesson, File.ReadAllText(lesson).Replace("type: explain", $"type: {type}"));

        Assert.Contains(ContentValidator.Validate(Root).Errors, e => e.Message.Contains($"step type '{type}' is not enabled"));
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("pack")]
    [InlineData("publish")]
    public async Task Disabled_content_is_rejected_by_all_commands_before_writing_or_uploading(string operation)
    {
        var path = Path.Combine(_repository, "output.zip");
        string[] args = operation == "pack" ? [operation, Root, "-o", path] : [operation, Root];
        using var server = new Server();
        using var http = new HttpClient(server);
        var errors = new StringWriter();
        var code = await CliApp.RunAsync(args, new StringWriter(), errors, http,
            new FileDeviceCredentialStore(Path.Combine(_repository, "credentials.json")));

        Assert.Equal(1, code);
        Assert.Contains("not enabled", errors.ToString());
        Assert.False(File.Exists(path));
        Assert.Equal(0, server.Requests);
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("pack")]
    [InlineData("publish")]
    public async Task App_selection_validates_packs_and_publishes_the_selected_sources(string operation)
    {
        File.WriteAllText(Manifest, File.ReadAllText(Manifest).Replace("[choice]", "[explain]"));
        var path = Path.Combine(_repository, "output.zip");
        string[] args = operation == "pack" ? [operation, "--app", Manifest, "-o", path] : [operation, "--app", Manifest];
        var store = new FileDeviceCredentialStore(Path.Combine(_repository, "credentials.json"));
        await store.SaveAsync(new DeviceCredentials(new Uri("https://test.invalid"), "test-device", "fake-token"));
        using var server = new Server();
        using var http = new HttpClient(server);
        var errors = new StringWriter();

        Assert.Equal(0, await CliApp.RunAsync(args, new StringWriter(), errors, http, store));
        if (operation == "pack")
            Assert.Equal("pack.sample", ContentPackFormat.Load(path).Manifest.PackId);
        if (operation == "publish")
        {
            Assert.Equal(1, server.Requests);
            Assert.Equal(ContentPacker.PackToBytes(Root, out _), server.Body);
        }
    }

    [Fact]
    public void Missing_or_invalid_app_manifests_do_not_silently_disable_the_allow_list()
    {
        File.WriteAllText(Manifest, "not: a manifest");
        Assert.Contains(ContentValidator.Validate(Root).Errors, e => e.Message.Contains("app manifest"));
        File.Delete(Manifest);
        Assert.Contains(ContentValidator.Validate(Root).Errors, e => e.Message.Contains("app manifest"));
    }

    [Fact]
    public void Practice_problems_obey_the_same_allow_list_as_lessons()
    {
        File.WriteAllText(Manifest, File.ReadAllText(Manifest).Replace("[choice]", "[explain]"));
        var lesson = Path.Combine(Root, "tracks/t/l/lesson.yaml");
        File.AppendAllText(lesson, "\npatterns:\n  - id: pattern.sample\n    title: Sample\n");
        Write("content/sample/tracks/t/l/practice.yaml", """
            problems:
              - id: step.practice
                type: parsons
                title: Arrange
                prompt: Arrange these lines
                solution: |
                  x = 1
                  y = 2
                pattern: pattern.sample
                patternCue: Recognize this problem
            """);
        var error = Assert.Single(ContentValidator.Validate(Root).Errors);
        Assert.Equal("tracks/t/l/practice.yaml", error.File);
        Assert.Contains("parsons' is not enabled", error.Message);
        File.WriteAllText(Manifest, File.ReadAllText(Manifest).Replace("[explain]", "[explain, parsons]"));
        Assert.True(ContentValidator.Validate(Root).IsValid);
    }

    [Fact]
    public void Phone_alternatives_are_checked_even_when_the_primary_type_is_enabled()
    {
        Write("content/sample/tracks/t/l/lesson.yaml", """
            id: lesson.sample
            title: Sample
            steps:
              - id: step.sample
                type: choice
                title: Choose
                prompt: Choose one
                options:
                  - text: Yes
                    correct: true
                  - text: No
                largeScreenPreferred: true
                phoneAlternative:
                  id: step.sample.phone
                  type: fill-blank
                  title: Fill
                  prompt: Fill the blank
                  template: 'x = {{value}}'
                  blanks:
                    - id: value
                      accepted: ['1']
            """);

        var error = Assert.Single(ContentValidator.Validate(Root).Errors);
        Assert.Contains("phoneAlternative", error.Message);
        Assert.Contains("fill-blank' is not enabled", error.Message);
        File.WriteAllText(Manifest, File.ReadAllText(Manifest).Replace("[choice]", "[choice, fill-blank]"));
        Assert.True(ContentValidator.Validate(Root).IsValid);
    }

    [Fact]
    public async Task A_manifest_cannot_be_used_to_validate_another_apps_source_directory()
    {
        Write("apps/other/manifest.yaml", File.ReadAllText(Manifest).Replace("example.sample", "example.other"));
        var errors = new StringWriter();
        using var http = new HttpClient();
        var code = await CliApp.RunAsync(["pack", Root, "-o", Path.Combine(_repository, "out.zip"),
            "--manifest", Path.Combine(_repository, "apps/other/manifest.yaml")], new StringWriter(), errors, http,
            new FileDeviceCredentialStore(Path.Combine(_repository, "credentials.json")));

        Assert.Equal(1, code);
        Assert.Contains("selects content directory", errors.ToString());
        Assert.False(File.Exists(Path.Combine(_repository, "out.zip")));
    }

    [Fact]
    public void Disabled_reference_solutions_are_not_executed()
    {
        Write("content/sample/tracks/t/l/lesson.yaml", """
            id: lesson.sample
            title: Sample
            steps:
              - id: step.sample
                type: write-code
                title: Add
                prompt: Add the numbers
                language: python
                entrypoint: add
                starter: 'def add(a, b): pass'
                solution: 'def add(a, b): return a + b'
                tests:
                  - input: '1, 2'
                    expected: '3'
            """);
        var error = Assert.Single(ContentValidator.Validate(Root, new NoRunner()).Errors);
        Assert.Contains("write-code' is not enabled", error.Message);
    }

    private sealed class NoRunner : IReferenceSolutionRunner
    {
        public TestRunResult Run(string code, string entrypoint, IReadOnlyList<CodeTest> tests) => throw new InvalidOperationException("Must not execute disabled content.");
        public TraceResult Trace(string code, IReadOnlyList<Visual> visuals) => throw new InvalidOperationException("Must not trace disabled content.");
    }

    private sealed class Server : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public byte[]? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Body = await request.Content!.ReadAsByteArrayAsync(ct);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PublishPackResponse(
                    new PackInfo("pack.sample", "1.0.0", 1, "hash", Body.Length, DateTimeOffset.UnixEpoch), true, [])),
            };
        }
    }

    private void Write(string relative, string value)
    {
        var path = Path.Combine(_repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    public void Dispose() => Directory.Delete(_repository, recursive: true);
}
