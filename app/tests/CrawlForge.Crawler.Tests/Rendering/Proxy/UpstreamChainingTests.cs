using System.Net;
using System.Net.Sockets;
using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Proxy;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Upstream proxy chaining (§5.9): HTTP-connect and SOCKS classes, per-host
/// bypass, and optional direct fallback with failure classification.
/// </summary>
public sealed class UpstreamChainingTests
{
    [Fact]
    public async Task HttpConnectUpstream_TunnelsCrawlTraffic()
    {
        await using var page = LoopbackPageServer.StartWithHtml("<html>via-upstream</html>");
        await using var upstream = ScriptedTcpServer.Start(async (client, ct) =>
        {
            var stream = client.GetStream();
            var requestLine = await ScriptIo.ReadLineAsync(stream, ct);
            Assert.StartsWith("CONNECT", requestLine);
            while ((await ScriptIo.ReadLineAsync(stream, ct)).Length > 0)
            {
                // Drain CONNECT headers.
            }

            var target = requestLine.Split(' ')[1];
            var hostPort = target.Split(':');
            using var targetClient = new TcpClient();
            await targetClient.ConnectAsync(hostPort[0], int.Parse(hostPort[1]), ct);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);
            await ScriptIo.RelayAsync(stream, targetClient.GetStream(), ct);
        });

        var settings = new ProxyRoutingSettings { UpstreamProxyUrl = $"http://127.0.0.1:{upstream.Port}" };
        await using var proxy = new LoopbackInterceptingProxy(settings);
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        var body = await client.GetStringAsync(page.BaseUrl);

        Assert.Contains("via-upstream", body);
        Assert.Equal(1, upstream.Connections);
        Assert.Equal(200, Assert.Single(await proxy.DrainCapturedAsync()).StatusCode);
    }

    [Fact]
    public async Task Socks5Upstream_TunnelsCrawlTraffic()
    {
        await using var page = LoopbackPageServer.StartWithHtml("<html>via-socks5</html>");
        await using var upstream = ScriptedTcpServer.Start(async (client, ct) =>
        {
            var stream = client.GetStream();
            var greeting = await ScriptIo.ReadExactAsync(stream, 2, ct);
            Assert.Equal(0x05, greeting[0]);
            var methods = await ScriptIo.ReadExactAsync(stream, greeting[1], ct);
            Assert.Contains((byte)0x00, methods);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

            var head = await ScriptIo.ReadExactAsync(stream, 4, ct);
            Assert.Equal(0x05, head[0]);
            Assert.Equal(0x01, head[1]);
            Assert.Equal(0x03, head[3]);
            var hostLength = (await ScriptIo.ReadExactAsync(stream, 1, ct))[0];
            var host = Encoding.ASCII.GetString(await ScriptIo.ReadExactAsync(stream, hostLength, ct));
            var portBytes = await ScriptIo.ReadExactAsync(stream, 2, ct);
            var port = (portBytes[0] << 8) | portBytes[1];

            using var targetClient = new TcpClient();
            await targetClient.ConnectAsync(host, port, ct);
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            await ScriptIo.RelayAsync(stream, targetClient.GetStream(), ct);
        });

        var settings = new ProxyRoutingSettings { UpstreamProxyUrl = $"socks5://127.0.0.1:{upstream.Port}" };
        await using var proxy = new LoopbackInterceptingProxy(settings);
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        var body = await client.GetStringAsync(page.BaseUrl);

        Assert.Contains("via-socks5", body);
        Assert.Equal(1, upstream.Connections);
    }

    [Fact]
    public async Task Socks5Upstream_WithCredentials_Authenticates()
    {
        await using var page = LoopbackPageServer.StartWithHtml("<html>auth-ok</html>");
        await using var upstream = ScriptedTcpServer.Start(async (client, ct) =>
        {
            var stream = client.GetStream();
            var greeting = await ScriptIo.ReadExactAsync(stream, 2, ct);
            var methods = await ScriptIo.ReadExactAsync(stream, greeting[1], ct);
            Assert.Contains((byte)0x02, methods);
            await stream.WriteAsync(new byte[] { 0x05, 0x02 }, ct);

            var authHead = await ScriptIo.ReadExactAsync(stream, 2, ct);
            Assert.Equal(0x01, authHead[0]);
            var user = Encoding.UTF8.GetString(await ScriptIo.ReadExactAsync(stream, authHead[1], ct));
            var passLen = (await ScriptIo.ReadExactAsync(stream, 1, ct))[0];
            var pass = Encoding.UTF8.GetString(await ScriptIo.ReadExactAsync(stream, passLen, ct));
            Assert.Equal("crawl-user", user);
            Assert.Equal("crawl-pass", pass);
            await stream.WriteAsync(new byte[] { 0x01, 0x00 }, ct);

            var head = await ScriptIo.ReadExactAsync(stream, 4, ct);
            var hostLen = (await ScriptIo.ReadExactAsync(stream, 1, ct))[0];
            var host = Encoding.ASCII.GetString(await ScriptIo.ReadExactAsync(stream, hostLen, ct));
            var portBytes = await ScriptIo.ReadExactAsync(stream, 2, ct);
            using var targetClient = new TcpClient();
            await targetClient.ConnectAsync(host, (portBytes[0] << 8) | portBytes[1], ct);
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            await ScriptIo.RelayAsync(stream, targetClient.GetStream(), ct);
        });

        var settings = new ProxyRoutingSettings
        {
            UpstreamProxyUrl = $"socks5://crawl-user:crawl-pass@127.0.0.1:{upstream.Port}",
        };
        await using var proxy = new LoopbackInterceptingProxy(settings);
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        Assert.Contains("auth-ok", await client.GetStringAsync(page.BaseUrl));
    }

    [Fact]
    public async Task BypassedHost_ConnectsDirectly_NeverTouchingUpstream()
    {
        await using var page = LoopbackPageServer.StartWithHtml("<html>direct</html>");
        await using var upstream = ScriptedTcpServer.Start((_, _) => Task.CompletedTask);

        var settings = new ProxyRoutingSettings
        {
            UpstreamProxyUrl = $"http://127.0.0.1:{upstream.Port}",
            BypassHosts = ["127.0.0.1"],
        };
        await using var proxy = new LoopbackInterceptingProxy(settings);
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        Assert.Contains("direct", await client.GetStringAsync(page.BaseUrl));
        Assert.Equal(0, upstream.Connections);
    }

    [Fact]
    public async Task UnreachableUpstream_WithFallback_UsesDirectConnection()
    {
        await using var page = LoopbackPageServer.StartWithHtml("<html>fallback</html>");
        var deadPort = LoopbackPageServer.GetFreeLoopbackPort();
        var settings = new ProxyRoutingSettings
        {
            UpstreamProxyUrl = $"http://127.0.0.1:{deadPort}",
            AllowDirectFallback = true,
        };
        await using var proxy = new LoopbackInterceptingProxy(settings);
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        Assert.Contains("fallback", await client.GetStringAsync(page.BaseUrl));
        Assert.Equal(200, Assert.Single(await proxy.DrainCapturedAsync()).StatusCode);
    }

    [Fact]
    public async Task UnreachableUpstream_WithoutFallback_ClassifiesAs502()
    {
        var deadPort = LoopbackPageServer.GetFreeLoopbackPort();
        var settings = new ProxyRoutingSettings
        {
            UpstreamProxyUrl = $"http://127.0.0.1:{deadPort}",
            AllowDirectFallback = false,
        };
        await using var proxy = new LoopbackInterceptingProxy(settings);
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        var response = await client.GetAsync("http://crawl-target.example/page");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(502, Assert.Single(await proxy.DrainCapturedAsync()).StatusCode);
    }

    [Fact]
    public async Task StartAsync_OptionsUpstream_OverridesBaseSettings()
    {
        await using var page = LoopbackPageServer.StartWithHtml("<html>options-upstream</html>");
        await using var upstream = ScriptedTcpServer.Start(async (client, ct) =>
        {
            var stream = client.GetStream();
            var requestLine = await ScriptIo.ReadLineAsync(stream, ct);
            while ((await ScriptIo.ReadLineAsync(stream, ct)).Length > 0)
            {
            }

            var hostPort = requestLine.Split(' ')[1].Split(':');
            using var targetClient = new TcpClient();
            await targetClient.ConnectAsync(hostPort[0], int.Parse(hostPort[1]), ct);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);
            await ScriptIo.RelayAsync(stream, targetClient.GetStream(), ct);
        });

        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions($"http://127.0.0.1:{upstream.Port}"));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        Assert.Contains("options-upstream", await client.GetStringAsync(page.BaseUrl));
        Assert.Equal(1, upstream.Connections);
    }
}
