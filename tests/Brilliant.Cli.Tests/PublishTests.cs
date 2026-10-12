using System.Net;
using System.Net.Http.Json;
using Brilliant.Core.Sync;

namespace Brilliant.Cli.Tests;

public sealed class PublishTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brilliant-publish-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new(), _err = new();
    private static readonly DeviceCredentials Paired = new(new Uri("http://nuc:8080/"), "dev", "tok-1");

    public PublishTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class MemoryStore(DeviceCredentials? creds) : IDeviceCredentialStore
    {
        public DeviceCredentials? Saved = creds;
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) { Saved = credentials; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct = default) { Saved = null; return Task.CompletedTask; }
    }

    /// <summary>A server that records what it was sent and answers as told.</summary>
    private sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth, byte[] Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status) { Content = JsonContent.Create(body) };

    private static HttpResponseMessage Accepted(bool created = true, IReadOnlyList<string>? removed = null) =>
        Json(HttpStatusCode.OK, new PublishPackResponse(new PackInfo("pack.t", "1.0.0", 1, "abc", 1234, DateTimeOffset.UnixEpoch), created, removed ?? []));

    private void WriteContent(string lessonId = "lesson.one", string stepId = "step.one.intro")
    {
        Write("pack.yaml", "id: pack.t\nversion: 1.0.0\n");
        Write("tracks/t/track.yaml", "id: track.t\ntitle: T\n");
        Write("tracks/t/l1/lesson.yaml", $"""
            id: {lessonId}
            title: One
            steps:
              - id: {stepId}
                type: explain
                title: Intro
                body: Hello
                comparison:
                  csharp: var x = 1;
                  python: x = 1
            """);
    }

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private Task<int> Run(FakeServer server, MemoryStore store, params string[] args) =>
        CliApp.RunAsync(args, _out, _err, new HttpClient(server), store);

    [Fact]
    public async Task Valid_content_is_uploaded_as_the_same_pack_that_pack_would_write()
    {
        WriteContent();
        var server = new FakeServer(_ => Accepted());

        var code = await Run(server, new MemoryStore(Paired), "publish", _root);

        Assert.Equal(0, code);
        var sent = Assert.Single(server.Requests);
        Assert.Equal((HttpMethod.Post, "/packs", "Bearer tok-1"), (sent.Method, sent.Path, sent.Auth));
        var viaPack = Path.Combine(_root, "out.zip");
        Assert.Equal(0, await Run(server, new MemoryStore(Paired), "pack", _root, "-o", viaPack));
        Assert.Equal(await File.ReadAllBytesAsync(viaPack), sent.Body);   // byte-reproducible, so the server can tell a republish from a change
        Assert.Contains("published pack.t 1.0.0", _out.ToString());
    }

    [Fact]
    public async Task Invalid_content_uploads_nothing_and_says_so()
    {
        WriteContent(stepId: "Not A Valid Id");
        var server = new FakeServer(_ => Accepted());

        var code = await Run(server, new MemoryStore(Paired), "publish", _root);

        Assert.Equal(1, code);
        Assert.Empty(server.Requests);
        Assert.Contains("error:", _err.ToString());
        Assert.Contains("nothing was published", _err.ToString());
    }

    [Fact]
    public async Task Content_with_no_pack_yaml_uploads_nothing()
    {
        var server = new FakeServer(_ => Accepted());

        Assert.Equal(1, await Run(server, new MemoryStore(Paired), "publish", _root));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task An_unpaired_computer_is_told_how_to_pair_and_sends_nothing()
    {
        WriteContent();
        var server = new FakeServer(_ => Accepted());

        var code = await Run(server, new MemoryStore(null), "publish", _root);

        Assert.Equal(1, code);
        Assert.Empty(server.Requests);
        Assert.Contains("brilliant pair", _err.ToString());
    }

    [Fact]
    public async Task A_republish_of_the_same_pack_says_nothing_changed()
    {
        WriteContent();

        var code = await Run(new FakeServer(_ => Accepted(created: false)), new MemoryStore(Paired), "publish", _root);

        Assert.Equal(0, code);
        Assert.Contains("already published", _out.ToString());
    }

    [Fact]
    public async Task Dropped_ids_are_listed_as_a_warning_but_the_publish_still_succeeds()
    {
        WriteContent();

        var code = await Run(new FakeServer(_ => Accepted(removed: ["step.old.a", "step.old.b"])), new MemoryStore(Paired), "publish", _root);

        Assert.Equal(0, code);
        Assert.Contains("warning:", _out.ToString());
        Assert.Contains("step.old.a", _out.ToString());
        Assert.Contains("step.old.b", _out.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "pack.t 1.0.0 is already published with different contents.")]
    [InlineData(HttpStatusCode.BadRequest, "That is not a usable Content Pack.")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "The pack is over 20 MB.")]
    public async Task A_refusal_shows_the_servers_reason_and_fails(HttpStatusCode status, string reason)
    {
        WriteContent();

        var code = await Run(new FakeServer(_ => Json(status, new { error = reason })), new MemoryStore(Paired), "publish", _root);

        Assert.Equal(1, code);
        Assert.Contains(reason, _err.ToString());
    }

    [Fact]
    public async Task A_server_that_no_longer_knows_this_computer_says_to_pair_again()
    {
        WriteContent();

        var code = await Run(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), new MemoryStore(Paired), "publish", _root);

        Assert.Equal(1, code);
        Assert.Contains("Pair it again", _err.ToString());
    }

    [Fact]
    public async Task A_server_that_is_away_is_reported_not_thrown()
    {
        WriteContent();
        var server = new FakeServer(_ => throw new HttpRequestException("Connection refused"));

        var code = await Run(server, new MemoryStore(Paired), "publish", _root);

        Assert.Equal(1, code);
        Assert.Contains("could not be reached", _err.ToString());
    }

    [Fact]
    public async Task Pairing_stores_the_credentials_for_publish_to_use()
    {
        var server = new FakeServer(_ => Json(HttpStatusCode.OK, new PairResponse("dev-9", "tok-9")));
        var store = new MemoryStore(null);

        var code = await Run(server, store, "pair", "nuc", "ABCD-2345", "--name", "Joseph's Mac");

        Assert.Equal(0, code);
        Assert.Equal(new DeviceCredentials(new Uri("http://nuc:8080/"), "dev-9", "tok-9"), store.Saved);
        Assert.DoesNotContain("tok-9", _out.ToString() + _err.ToString());   // the token is never printed
    }

    [Fact]
    public async Task A_wrong_code_is_explained_and_nothing_is_stored()
    {
        var store = new MemoryStore(null);

        var code = await Run(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), store, "pair", "nuc", "WRONG-CODE");

        Assert.Equal(1, code);
        Assert.Null(store.Saved);
        Assert.Contains("code is wrong", _err.ToString());
    }

    [Theory]
    [InlineData()]
    [InlineData("publish")]
    [InlineData("publish", "a", "b")]
    [InlineData("pair", "nuc")]
    [InlineData("frobnicate", "x")]
    public async Task Unknown_or_incomplete_commands_print_usage(params string[] args)
    {
        var code = await Run(new FakeServer(_ => Accepted()), new MemoryStore(Paired), args);

        Assert.Equal(2, code);
        Assert.Contains("usage:", _err.ToString());
        Assert.Contains("publish", _err.ToString());
    }

    [Fact]
    public async Task Validate_and_pack_still_work_through_the_new_entry_point()
    {
        WriteContent();
        var server = new FakeServer(_ => Accepted());

        Assert.Equal(0, await Run(server, new MemoryStore(null), "validate", _root));
        Assert.Empty(server.Requests);
        Assert.Contains("valid", _out.ToString());
    }
}
