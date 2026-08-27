using System.Net;
using BTCPayServer.Lightning;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

public class AddressAndSecurityTests
{
    [Theory]
    [InlineData("jens@coinsnap.app", "jens@coinsnap.app")]
    [InlineData("jens.usd@coinsnap.app", "jens.usd@coinsnap.app")]
    [InlineData("jens@COINSNAP.APP", "jens@coinsnap.app")]
    public void AcceptsSupportedAddresses(string input, string expected)
    {
        Assert.Equal(expected, CoinsnapLightningAddress.Parse(input).Value);
    }

    [Theory]
    [InlineData("jens@example.com")]
    [InlineData("@coinsnap.app")]
    [InlineData("jens")]
    [InlineData("jens@@coinsnap.app")]
    [InlineData("jens..usd@coinsnap.app")]
    [InlineData("jens;type=x@coinsnap.app")]
    public void RejectsUnsupportedOrMalformedAddresses(string input)
    {
        Assert.False(CoinsnapLightningAddress.TryParse(input, out _, out _));
    }

    [Theory]
    [InlineData("http://coinsnap.app/verify/x")]
    [InlineData("https://example.com/verify/x")]
    [InlineData("https://localhost/verify/x")]
    [InlineData("https://127.0.0.1/verify/x")]
    [InlineData("file:///tmp/x")]
    [InlineData("https://coinsnap.app:444/verify/x")]
    [InlineData("https://user@coinsnap.app/verify/x")]
    public void UrlPolicyRejectsUnsafeTargets(string value)
    {
        Assert.Throws<FormatException>(() => CoinsnapUrlPolicy.ParseAndValidate(value, "test URL"));
    }

    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("2001:4860:4860::8888", true)]
    public void DnsPolicyRejectsPrivateAndReservedAddresses(string value, bool expected)
    {
        Assert.Equal(expected, CoinsnapNetworkPolicy.IsPublicAddress(IPAddress.Parse(value)));
    }

    [Fact]
    public async Task RejectsCrossDomainRedirect()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson("", HttpStatusCode.Found, r => r.Headers.Location = new Uri("https://evil.example/steal"));
        var client = new CoinsnapHttpClient(new HttpClient(handler));

        await Assert.ThrowsAsync<FormatException>(() =>
            client.GetAsync(new Uri("https://coinsnap.app/lnurlp/jens"), "test URL", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AllowsBoundedSameHostRedirect()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson("", HttpStatusCode.Found, r => r.Headers.Location = new Uri("/lnurlp/jens2", UriKind.Relative));
        handler.EnqueueJson("{}", HttpStatusCode.OK);
        var client = new CoinsnapHttpClient(new HttpClient(handler));

        var response = await client.GetAsync(
            new Uri("https://coinsnap.app/lnurlp/jens"), "test URL", CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("https://coinsnap.app/lnurlp/jens2", response.FinalUri.ToString());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsTooManyRedirects()
    {
        var handler = new ScriptedHttpHandler();
        for (var i = 0; i <= CoinsnapConstants.MaxRedirects; i++)
            handler.EnqueueJson("", HttpStatusCode.Found, r => r.Headers.Location = new Uri("/next", UriKind.Relative));
        var client = new CoinsnapHttpClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(new Uri("https://coinsnap.app/start"), "test URL", CancellationToken.None));
        Assert.Equal(CoinsnapConstants.MaxRedirects + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsOversizedResponseBeforeParsing()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[CoinsnapConstants.MaxResponseBytes + 1])
        }));
        var client = new CoinsnapHttpClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(new Uri("https://coinsnap.app/lnurlp/jens"), "test URL", CancellationToken.None));
    }

    [Fact]
    public void ProductionBoltParserRejectsWrongNetwork()
    {
        var parser = new CoinsnapBolt11Parser();
        var parsed = parser.Parse(TestData.MainnetBolt11, Network.Main);
        Assert.NotNull(parsed.Amount);
        Assert.Throws<FormatException>(() => parser.Parse(TestData.MainnetBolt11, Network.TestNet));
    }

    [Fact]
    public async Task ConnectionHandlerAcceptsOnlyCoinsnapAddressAndBuildsStoreIdentity()
    {
        var handler = new ScriptedHttpHandler();
        var client = new HttpClient(handler);
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        var connectionHandler = new CoinsnapLightningConnectionStringHandler(
            new SingleHttpClientFactory(client),
            parser,
            repository,
            clock,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var lightning = connectionHandler.Create(
            "type=coinsnap;ln-address=jens@coinsnap.app;store-id=abc;",
            Network.Main,
            out var error);
        Assert.NotNull(lightning);
        Assert.Null(error);

        Assert.Null(connectionHandler.Create(
            "type=coinsnap;ln-address=jens@example.com;store-id=abc;",
            Network.Main,
            out error));
        Assert.Contains("@coinsnap.app", error);

        Assert.Null(connectionHandler.Create(
            "type=coinsnap;ln-address=jens@coinsnap.app;api-key=secret;",
            Network.Main,
            out error));
        Assert.Contains("not supported", error);

        Assert.Null(connectionHandler.Create(
            "type=coinsnap;ln-address=jens@coinsnap.app;store-id=abc;",
            Network.TestNet,
            out error));
        Assert.Contains("mainnet", error);
        await Task.CompletedTask;
    }
}
