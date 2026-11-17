using System.Net;
using System.Net.Sockets;
using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Proxy;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Forward-proxy behavior: absolute-URI capture, resource blocking with zero
/// outbound, CONNECT tunneling, and failure classification (FS-CRAWL-001 §5.9).
/// </summary>
public sealed class ProxyForwardTests
{
    [Fact]
    public async Task AbsoluteUriRequest_IsForwarded_AndCaptured()
    {
        await using var page = LoopbackPageServer.StartWithHtml("<html><body>hello</body></html>");
        var connector = new CountingConnector(new TcpOutboundConnector(new ProxyRoutingSettings()));
        await using var proxy = new LoopbackInterceptingProxy(connector: connector);
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        var body = await client.GetStringAsync(page.BaseUrl);

        Assert.Contains("hello", body);
        Assert.Equal(1, connector.Attempts);

        var captured = await proxy.DrainCapturedAsync();
        var entry = Assert.Single(captured);
        Assert.Equal(page.BaseUrl, entry.Url);
        Assert.Equal("GET", entry.Method);
        Assert.Equal(200, entry.StatusCode);
        Assert.Contains("text/html", entry.ContentType);

        // Drain semantics: a second drain is empty.
        Assert.Empty(await proxy.DrainCapturedAsync());
    }

    [Fact]
    public async Task PostWithBody_IsForwardedIntact()
    {
        string? receivedBody = null;
        await using var page = LoopbackPageServer.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.InputStream);
            receivedBody = await reader.ReadToEndAsync();
            context.Response.StatusCode = 201;
            context.Response.Close();
        });
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        var response = await client.PostAsync(page.BaseUrl, new StringContent("payload-123", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("payload-123", receivedBody);
        var captured = await proxy.DrainCapturedAsync();
        Assert.Equal(201, Assert.Single(captured).StatusCode);
    }

    [Fact]
    public async Task BlockingRule_ReturnsLocal403_WithZeroOutbound()
    {
        var connector = new CountingConnector(throwOnConnect: true);
        var settings = new ProxyRoutingSettings
        {
            BlockingRules = [new ResourceBlockingRule("*.blocked.example")],
        };
        await using var proxy = new LoopbackInterceptingProxy(settings, connector);
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        var response = await client.GetAsync("http://ads.blocked.example/tracker.js");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, connector.Attempts);
        var captured = await proxy.DrainCapturedAsync();
        var entry = Assert.Single(captured);
        Assert.Equal(403, entry.StatusCode);
        Assert.Equal("http://ads.blocked.example/tracker.js", entry.Url);
    }

    [Fact]
    public async Task BlockingRule_PathFilter_NarrowsTheRule()
    {
        await using var page = LoopbackPageServer.StartWithHtml("<html>ok</html>");
        var settings = new ProxyRoutingSettings
        {
            BlockingRules = [new ResourceBlockingRule("127.0.0.1", "/ads/")],
        };
        await using var proxy = new LoopbackInterceptingProxy(settings);
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });

        var blocked = await client.GetAsync(page.BaseUrl + "ads/banner.js");
        var allowed = await client.GetAsync(page.BaseUrl + "content/page.html");

        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task ConnectTunnel_RelaysBytes_AndCapturesTheTunnel()
    {
        await using var echo = ScriptedTcpServer.Start(async (client, ct) =>
        {
            var stream = client.GetStream();
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, ct);
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
        });
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        var stream = client.GetStream();

        var connect = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\nHost: 127.0.0.1:{echo.Port}\r\n\r\n");
        await stream.WriteAsync(connect);
        var statusLine = await ScriptIo.ReadLineAsync(stream, CancellationToken.None);
        Assert.Contains("200", statusLine);
        await ScriptIo.ReadLineAsync(stream, CancellationToken.None); // empty line

        var payload = Encoding.ASCII.GetBytes("tunnel-ping");
        await stream.WriteAsync(payload);
        var echoed = await ScriptIo.ReadExactAsync(stream, payload.Length, CancellationToken.None);
        Assert.Equal(payload, echoed);

        var captured = await proxy.DrainCapturedAsync();
        var entry = Assert.Single(captured);
        Assert.Equal("CONNECT", entry.Method);
        Assert.Equal(200, entry.StatusCode);
        Assert.Equal($"https://127.0.0.1:{echo.Port}", entry.Url);
    }

    [Fact]
    public async Task UnreachableTarget_IsClassifiedAs502()
    {
        var deadPort = LoopbackPageServer.GetFreeLoopbackPort();
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        var response = await client.GetAsync($"http://127.0.0.1:{deadPort}/missing");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var captured = await proxy.DrainCapturedAsync();
        Assert.Equal(502, Assert.Single(captured).StatusCode);
    }

    [Fact]
    public async Task OriginFormRequest_IsRejectedWith400()
    {
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /relative HTTP/1.1\r\nHost: example.test\r\n\r\n"));

        var statusLine = await ScriptIo.ReadLineAsync(stream, CancellationToken.None);
        Assert.Contains("400", statusLine);
    }
}
