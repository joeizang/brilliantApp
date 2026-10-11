using System.Net;
using System.Net.Http.Json;
using Brilliant.Core.Sync;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Brilliant.Lessons.UI.Tests;

/// <summary>The Sync screen: pair with the server using an address and a code, see who you're paired with, forget it.</summary>
public class PairingViewTests : BunitContext
{
    private sealed class MemoryStore : IDeviceCredentialStore
    {
        public DeviceCredentials? Saved;
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) { Saved = credentials; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken ct = default) { Saved = null; return Task.CompletedTask; }
    }

    private sealed class Stub(HttpStatusCode status, object? body = null) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = body is null ? null : JsonContent.Create(body) });
        }
    }

    private sealed class FailingForget : IDeviceCredentialStore
    {
        public Task<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => Task.FromResult<DeviceCredentials?>(new(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1"));
        public Task SaveAsync(DeviceCredentials credentials, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => throw new IOException("disk is read-only");
    }

    private readonly MemoryStore _store = new();
    private Stub _server = new(HttpStatusCode.OK, new PairResponse("dev-1", "tok-1"));

    private IRenderedComponent<PairingView> Open()
    {
        Services.AddSingleton(new PairingService(new PairingClient(new HttpClient(_server)), _store));
        return Render<PairingView>();
    }

    private static void Fill(IRenderedComponent<PairingView> cut, string address = "nuc.local", string code = "ABCD-2345")
    {
        cut.Find("input.pair-address").Change(address);
        cut.Find("input.pair-code").Change(code);
    }

    [Fact]
    public void An_unpaired_device_is_asked_for_an_address_and_a_code()
    {
        var cut = Open();
        Assert.NotNull(cut.Find("input.pair-address"));
        Assert.NotNull(cut.Find("input.pair-code"));
        Assert.Empty(cut.FindAll(".paired"));
    }

    [Fact]
    public void Pairing_stores_the_credentials_and_shows_who_we_are_paired_with()
    {
        var cut = Open();
        Fill(cut);

        cut.Find("form.pair-form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("nuc.local:8080", cut.Find(".paired-server").TextContent));
        Assert.Equal("tok-1", _store.Saved!.Token);
        Assert.Empty(cut.FindAll("form.pair-form"));
    }

    [Fact]
    public void A_device_that_is_already_paired_opens_on_the_paired_screen()
    {
        _store.Saved = new DeviceCredentials(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1");
        var cut = Open();
        cut.WaitForAssertion(() => Assert.Contains("nuc.local:8080", cut.Find(".paired-server").TextContent));
        Assert.Empty(cut.FindAll("input.pair-code"));
    }

    [Fact]
    public void A_wrong_code_says_so_and_stores_nothing()
    {
        _server = new Stub(HttpStatusCode.Unauthorized);
        var cut = Open();
        Fill(cut, code: "WRONG-CODE");

        cut.Find("form.pair-form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("code didn't work", cut.Find(".pair-error").TextContent));
        Assert.Null(_store.Saved);
        Assert.NotNull(cut.Find("input.pair-code"));
    }

    [Fact]
    public void A_bad_address_is_explained_without_calling_the_server()
    {
        var cut = Open();
        Fill(cut, address: "ftp://nope");

        cut.Find("form.pair-form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("server address", cut.Find(".pair-error").TextContent));
        Assert.Equal(0, _server.Calls);
    }

    [Fact]
    public void Leaving_the_fields_empty_asks_for_them_without_calling_the_server()
    {
        var cut = Open();

        cut.Find("form.pair-form").Submit();

        Assert.Contains("Enter", cut.Find(".pair-error").TextContent);
        Assert.Equal(0, _server.Calls);
    }

    [Fact]
    public void An_unreachable_server_is_explained()
    {
        _server = new Stub(HttpStatusCode.BadGateway);
        var cut = Open();
        Fill(cut);

        cut.Find("form.pair-form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Couldn't reach", cut.Find(".pair-error").TextContent));
    }

    [Fact]
    public void Forgetting_the_server_clears_it_and_brings_the_form_back()
    {
        _store.Saved = new DeviceCredentials(new Uri("http://nuc.local:8080/"), "dev-1", "tok-1");
        var cut = Open();
        cut.WaitForElement("button.forget").Click();

        Assert.Null(_store.Saved);
        Assert.NotNull(cut.Find("input.pair-code"));
    }

    [Fact]
    public void A_forget_that_fails_says_so_and_stays_paired()
    {
        Services.AddSingleton(new PairingService(new PairingClient(new HttpClient(_server)), new FailingForget()));
        var cut = Render<PairingView>();

        cut.WaitForElement("button.forget").Click();

        Assert.Contains("Couldn't forget", cut.Find(".forget-error").TextContent);
        Assert.NotNull(cut.Find(".paired"));
    }
}
