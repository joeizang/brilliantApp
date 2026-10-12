using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Brilliant.Core.Content;
using Brilliant.Core.Sync;

namespace Brilliant.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PackEndpointTests(PostgresFixture postgres)
{
    private static byte[] Build(string version, string packId = "pack.t", int format = ContentPackFormat.CurrentFormatVersion, params string[] stepIds)
    {
        if (stepIds.Length == 0) stepIds = ["step.a", "step.b"];
        var lesson = new Lesson("lesson.1", "One", "track.a", stepIds.Select(id => (Step)new ExplainStep(id, id, "body", [], null)).ToList());
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest(packId, version, format), [new Track("track.a", "A", [lesson.Id])], [lesson]);
        return ms.ToArray();
    }

    private static Task<HttpResponseMessage> Publish(HttpClient client, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return client.PostAsync("/packs", content);
    }

    private static async Task<string> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"];

    private static async Task<PublishPackResponse> Published(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PublishPackResponse>())!;
    }

    private static async Task<IReadOnlyList<PackInfo>> List(HttpClient client) =>
        (await client.GetFromJsonAsync<PackListResponse>("/packs"))!.Packs;

    [Fact]
    public async Task A_pack_whose_references_lead_nowhere_is_refused_and_nothing_is_stored()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        var lesson = new Lesson("lesson.1", "One", "track.a", [new ExplainStep("step.a", "a", "body", [], null)]);
        using var ms = new MemoryStream();
        ContentPackFormat.Write(ms, new PackManifest("pack.t", "2.0.0", ContentPackFormat.CurrentFormatVersion), [new Track("track.a", "A", ["lesson.missing"])], [lesson]);

        var response = await Publish(client, ms.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("lesson.missing", await Error(response));
        Assert.Empty(await List(client));
    }

    [Fact]
    public async Task A_valid_pack_is_published_and_listed()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        var bytes = Build("1.0.0");

        var result = await Published(await Publish(client, bytes));

        Assert.True(result.Created);
        Assert.Empty(result.RemovedIds);
        var listed = Assert.Single(await List(client));
        Assert.Equal(("pack.t", "1.0.0", ContentPackFormat.CurrentFormatVersion, bytes.Length), (listed.PackId, listed.Version, listed.FormatVersion, (int)listed.Size));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), listed.Sha256);
        Assert.Equal(server.Clock.GetUtcNow(), listed.PublishedAt);
    }

    [Fact]
    public async Task A_published_pack_downloads_byte_for_byte_and_the_client_accepts_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, device) = await server.PairDeviceAsync();
        var bytes = Build("1.0.0");
        await Publish(client, bytes);

        var response = await client.GetAsync("/packs/pack.t/1.0.0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal($"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))}\"", response.Headers.ETag!.Tag);

        // and the app's own client reads it the way the app will
        var creds = new DeviceCredentials(client.BaseAddress!, device.DeviceId, device.Token);
        var packClient = new PackClient(client);
        var info = Assert.Single(await packClient.ListAsync(creds));
        Assert.Equal(bytes, await packClient.DownloadAsync(creds, info));
    }

    [Fact]
    public async Task The_list_offers_only_the_newest_version_of_each_pack_compared_as_numbers()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        foreach (var v in new[] { "0.9.0", "0.10.0", "0.2.0".Replace("0.2.0", "0.10.0") }) await Publish(client, Build(v));
        await Publish(client, Build("3.0.0", "pack.other"));

        var listed = await List(client);

        Assert.Equal([("pack.other", "3.0.0"), ("pack.t", "0.10.0")], listed.Select(p => (p.PackId, p.Version)));
    }

    [Fact]
    public async Task Older_versions_stay_downloadable()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        var v1 = Build("1.0.0");
        await Publish(client, v1);
        await Publish(client, Build("1.1.0"));

        Assert.Equal(v1, await client.GetByteArrayAsync("/packs/pack.t/1.0.0"));
    }

    [Fact]
    public async Task Publishing_identical_bytes_again_changes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        var bytes = Build("1.0.0");
        var first = await Published(await Publish(client, bytes));
        server.Clock.Advance(TimeSpan.FromHours(1));

        var again = await Published(await Publish(client, bytes));

        Assert.True(first.Created);
        Assert.False(again.Created);
        Assert.Equal(first.Pack, again.Pack);   // including when it was first published
        Assert.Single(await List(client));
    }

    [Fact]
    public async Task The_same_version_with_different_contents_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        var original = Build("1.0.0");
        await Publish(client, original);

        var response = await Publish(client, Build("1.0.0", stepIds: ["step.a", "step.changed"]));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("different contents", await Error(response));
        Assert.Equal(original, await client.GetByteArrayAsync("/packs/pack.t/1.0.0"));
    }

    [Fact]
    public async Task A_version_lower_than_the_newest_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        await Publish(client, Build("1.2.0"));

        var response = await Publish(client, Build("1.1.0"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("1.2.0", await Error(response));
        Assert.Equal("1.2.0", Assert.Single(await List(client)).Version);
    }

    [Fact]
    public async Task A_lower_version_is_refused_even_when_the_text_would_sort_higher()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        await Publish(client, Build("1.10.0"));

        Assert.Equal(HttpStatusCode.Conflict, (await Publish(client, Build("1.9.0"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Publish(client, Build("1.11.0"))).StatusCode);
    }

    [Fact]
    public async Task What_the_new_version_dropped_is_reported_so_the_author_can_see_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        await Publish(client, Build("1.0.0", stepIds: ["step.a", "step.b", "step.c"]));

        var result = await Published(await Publish(client, Build("1.1.0", stepIds: ["step.a", "step.d"])));

        Assert.Equal(["step.b", "step.c"], result.RemovedIds);
    }

    [Fact]
    public async Task A_file_that_is_not_a_pack_is_refused_with_a_reason()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        var garbage = await Publish(client, "not a zip"u8.ToArray());
        var empty = await Publish(client, []);

        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await Error(garbage)));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Empty(await List(client));
    }

    [Fact]
    public async Task A_zip_without_the_pack_files_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("readme.txt");

        Assert.Equal(HttpStatusCode.BadRequest, (await Publish(client, ms.ToArray())).StatusCode);
    }

    [Fact]
    public async Task A_pack_with_a_format_the_server_cannot_read_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        var response = await Publish(client, Build("1.0.0", format: ContentPackFormat.CurrentFormatVersion + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await List(client));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-beta")]
    public async Task A_version_that_is_not_three_numbers_is_refused(string version)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        var response = await Publish(client, Build(version));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("1.2.3", await Error(response));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    [InlineData("has space")]
    public async Task A_pack_id_that_could_steer_a_path_or_url_is_refused(string packId)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        var response = await Publish(client, Build("1.0.0", packId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await List(client));
    }

    [Fact]
    public async Task A_pack_over_the_size_limit_is_refused_without_being_kept()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        var response = await Publish(client, new byte[PackLimits.MaxBytes + 1]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await List(client));
    }

    [Fact]
    public async Task Everything_needs_a_paired_device()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        await Publish(client, Build("1.0.0"));
        var stranger = server.CreateClient();
        var revoked = server.Authorised("not-a-token");

        foreach (var anonymous in new[] { stranger, revoked })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Publish(anonymous, Build("2.0.0"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/packs")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/packs/pack.t/1.0.0")).StatusCode);
        }
        Assert.Equal("1.0.0", Assert.Single(await List(client)).Version);
    }

    [Theory]
    [InlineData("/packs/pack.t/9.9.9")]
    [InlineData("/packs/pack.missing/1.0.0")]
    [InlineData("/packs/pack.t/latest")]
    [InlineData("/packs/pack.t/1.0")]
    [InlineData("/packs/.hidden/1.0.0")]
    public async Task A_pack_that_is_not_there_is_not_found(string path)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();
        await Publish(client, Build("1.0.0"));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Two_publishes_of_the_same_version_at_once_leave_exactly_one()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (client, _) = await server.PairDeviceAsync();

        var codes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
            (await Publish(client, Build("1.0.0", stepIds: ["step.a", $"step.v{i % 2}"]))).StatusCode));

        Assert.All(codes, c => Assert.True(c is HttpStatusCode.OK or HttpStatusCode.Conflict, c.ToString()));
        Assert.Contains(HttpStatusCode.OK, codes);
        Assert.Single(await List(client));
    }
}
