using System.Net;
using System.Net.Sockets;
using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Proxy;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Per-subresource transferred-byte capture through the loopback proxy
/// (CR-031; phase-09 notation gap 7): the oversized-image rule can measure
/// plain-HTTP (non-rendered) image payloads. Rendered traffic is measured by
/// the CDP-direct waterfall instead (CR-006).
/// </summary>
public sealed class ProxyCaptureSizeTests
{
    [Fact]
    public async Task ForwardRequest_RecordsExactResponseBodyBytes_AndTransferredTotal()
    {
        const string html = "<html><body>hello-size</body></html>";
        var bodyBytes = Encoding.UTF8.GetByteCount(html);
        await using var page = LoopbackPageServer.StartWithHtml(html);
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = CreateProxiedClient(endpoint);
        var body = await client.GetStringAsync(page.BaseUrl);
        Assert.Contains("hello-size", body);

        var captured = await proxy.DrainDetailedCapturedAsync();
        var entry = Assert.Single(captured);
        Assert.Equal(page.BaseUrl, entry.Url);
        Assert.Equal("GET", entry.Method);
        Assert.Equal(200, entry.StatusCode);
        Assert.Equal(bodyBytes, entry.ResponseBodyBytes);
        // Transferred = response head + body actually relayed.
        Assert.True(entry.TransferredBytes > entry.ResponseBodyBytes);
        Assert.True(entry.TransferredBytes >= bodyBytes + 15);
    }

    [Fact]
    public async Task MultipleSubresources_EachCarryTheirOwnSize()
    {
        var small = new byte[43];
        var large = new byte[4096];
        await using var page = LoopbackPageServer.Start(async context =>
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var payload = path == "/small.gif" ? small : large;
            context.Response.StatusCode = 200;
            context.Response.ContentType = path == "/small.gif" ? "image/gif" : "image/png";
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        });
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = CreateProxiedClient(endpoint);
        await client.GetByteArrayAsync(page.BaseUrl + "small.gif");
        await client.GetByteArrayAsync(page.BaseUrl + "large.png");

        var captured = await proxy.DrainDetailedCapturedAsync();
        Assert.Equal(2, captured.Count);
        var smallEntry = Assert.Single(captured, e => e.Url.EndsWith("/small.gif"));
        var largeEntry = Assert.Single(captured, e => e.Url.EndsWith("/large.png"));
        Assert.Equal(43, smallEntry.ResponseBodyBytes);
        Assert.Equal(4096, largeEntry.ResponseBodyBytes);
        Assert.Contains("image/gif", smallEntry.ContentType);
        Assert.Contains("image/png", largeEntry.ContentType);
    }

    [Fact]
    public async Task ChunkedResponse_CountsTransferredBodyBytes()
    {
        var payload = Encoding.UTF8.GetBytes("chunked-payload-1234567890");
        await using var page = LoopbackPageServer.Start(async context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain";
            context.Response.SendChunked = true;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        });
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = CreateProxiedClient(endpoint);
        var body = await client.GetStringAsync(page.BaseUrl);
        Assert.Equal("chunked-payload-1234567890", body);

        var entry = Assert.Single(await proxy.DrainDetailedCapturedAsync());
        // Raw chunked framing counts toward transferred bytes.
        Assert.True(entry.ResponseBodyBytes >= payload.Length);
    }

    [Fact]
    public async Task BlockedRequest_TransferredNothing()
    {
        var connector = new CountingConnector(throwOnConnect: true);
        var settings = new ProxyRoutingSettings
        {
            BlockingRules = [new ResourceBlockingRule("*.blocked.example")],
        };
        await using var proxy = new LoopbackInterceptingProxy(settings, connector);
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = CreateProxiedClient(endpoint);
        var response = await client.GetAsync("http://ads.blocked.example/tracker.js");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var entry = Assert.Single(await proxy.DrainDetailedCapturedAsync());
        Assert.Equal(403, entry.StatusCode);
        Assert.Equal(0, entry.TransferredBytes);
        Assert.Equal(0, entry.ResponseBodyBytes);
        Assert.Equal(0, connector.Attempts);
    }

    [Fact]
    public async Task UpstreamFailure_TransferredNothing()
    {
        int deadPort;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = CreateProxiedClient(endpoint);
        var response = await client.GetAsync($"http://127.0.0.1:{deadPort}/missing");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var entry = Assert.Single(await proxy.DrainDetailedCapturedAsync());
        Assert.Equal(502, entry.StatusCode);
        Assert.Equal(0, entry.TransferredBytes);
    }

    [Fact]
    public async Task ConnectTunnel_SizeIsOpaque_Zero()
    {
        await using var page = LoopbackPageServer.StartWithHtml("<html>tunnel</html>");
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));
        var pageUri = new Uri(page.BaseUrl);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"CONNECT {pageUri.Host}:{pageUri.Port} HTTP/1.1\r\nHost: {pageUri.Host}:{pageUri.Port}\r\n\r\n"));
        var connectStatus = await ScriptIo.ReadLineAsync(stream, CancellationToken.None);
        Assert.Contains("200", connectStatus);
        await ScriptIo.ReadLineAsync(stream, CancellationToken.None); // empty line after CONNECT response
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET {pageUri.AbsolutePath} HTTP/1.1\r\nHost: {pageUri.Host}\r\nConnection: close\r\n\r\n"));
        var statusLine = await ScriptIo.ReadLineAsync(stream, CancellationToken.None);
        Assert.Contains("200", statusLine);

        var entry = Assert.Single(await proxy.DrainDetailedCapturedAsync());
        Assert.Equal("CONNECT", entry.Method);
        // TLS end-to-end: per-subresource sizes inside the tunnel are opaque;
        // CR-031 reports "not measured", never "pass".
        Assert.Equal(0, entry.TransferredBytes);
    }

    [Fact]
    public async Task DetailedDrain_ConsumesTheQueue_LockedDrainProjectsIdentity()
    {
        await using var page = LoopbackPageServer.StartWithHtml("<html>x</html>");
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = CreateProxiedClient(endpoint);
        await client.GetStringAsync(page.BaseUrl);

        var detailed = await proxy.DrainDetailedCapturedAsync();
        Assert.Single(detailed);
        // Either drain consumes the shared queue.
        Assert.Empty(await proxy.DrainCapturedAsync());
        Assert.Empty(await proxy.DrainDetailedCapturedAsync());

        // Locked-shape projection carries the identity fields unchanged.
        var locked = detailed[0].ToLockedRecord();
        Assert.Equal(detailed[0].Url, locked.Url);
        Assert.Equal(detailed[0].Method, locked.Method);
        Assert.Equal(detailed[0].StatusCode, locked.StatusCode);
        Assert.Equal(detailed[0].ContentType, locked.ContentType);
        Assert.Equal(detailed[0].CapturedAt, locked.CapturedAt);
    }

    [Fact]
    public async Task LockedDrain_StillWorks_WithSizesRecordedAlongside()
    {
        await using var page = LoopbackPageServer.StartWithHtml("<html>locked</html>");
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = CreateProxiedClient(endpoint);
        await client.GetStringAsync(page.BaseUrl);

        IReadOnlyList<ProxyCapturedRequest> locked = await proxy.DrainCapturedAsync();
        var entry = Assert.Single(locked);
        Assert.Equal(200, entry.StatusCode);
        Assert.Empty(await proxy.DrainDetailedCapturedAsync());
    }

    [Fact]
    public async Task PostWithEmptyResponse_RecordsHeadOnly()
    {
        await using var page = LoopbackPageServer.Start(context =>
        {
            context.Response.StatusCode = 201;
            context.Response.ContentLength64 = 0;
            context.Response.Close();
            return Task.CompletedTask;
        });
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        using var client = CreateProxiedClient(endpoint);
        var response = await client.PostAsync(page.BaseUrl, new StringContent("payload", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var entry = Assert.Single(await proxy.DrainDetailedCapturedAsync());
        Assert.Equal(201, entry.StatusCode);
        Assert.Equal(0, entry.ResponseBodyBytes);
        Assert.True(entry.TransferredBytes > 0); // the response head transferred
    }

    private static HttpClient CreateProxiedClient(ProxyEndpoint endpoint) =>
        new(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
}
