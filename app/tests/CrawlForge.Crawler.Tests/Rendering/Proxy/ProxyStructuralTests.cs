using System.Net;
using System.Net.Sockets;
using System.Reflection;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Proxy;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Criterion #22 structural evidence: the built-in proxy binds loopback ONLY,
/// by construction — no API accepts a bind address, the listener endpoint is
/// <see cref="IPAddress.Loopback"/>, and the proxy is a crawl-traffic tool, not
/// a tunnel (no enrollment/liveness surface).
/// </summary>
public sealed class ProxyStructuralTests
{
    [Fact]
    public async Task StartAsync_ReturnsLoopbackEndpoint_AndActuallyListens()
    {
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        Assert.Equal("127.0.0.1", endpoint.Host);
        Assert.True(endpoint.Port > 0);
        Assert.True(IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address));

        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        Assert.True(probe.Connected);
    }

    [Fact]
    public async Task Listener_IsBoundToLoopbackByConstruction()
    {
        await using var proxy = new LoopbackInterceptingProxy();
        await proxy.StartAsync(new ProxyOptions(null));

        var listenerField = typeof(LoopbackInterceptingProxy)
            .GetField("_listener", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(listenerField);
        var listener = (TcpListener?)listenerField!.GetValue(proxy);
        Assert.NotNull(listener);
        var local = (IPEndPoint)listener!.LocalEndpoint;
        Assert.Equal(IPAddress.Loopback, local.Address);
    }

    [Fact]
    public void NoPublicApi_AcceptsABindAddress()
    {
        // The locked contract carries no bind address...
        var optionProperties = typeof(ProxyOptions).GetProperties();
        Assert.Single(optionProperties);
        Assert.Equal("UpstreamProxyUrl", optionProperties[0].Name);

        // ...and no public member of the implementation accepts one.
        foreach (var method in typeof(LoopbackInterceptingProxy).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (var parameter in method.GetParameters())
            {
                Assert.NotEqual(typeof(IPAddress), parameter.ParameterType);
                Assert.NotEqual(typeof(IPEndPoint), parameter.ParameterType);
                Assert.DoesNotContain("bind", parameter.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("listenAddress", parameter.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var constructor in typeof(LoopbackInterceptingProxy).GetConstructors())
        {
            foreach (var parameter in constructor.GetParameters())
            {
                Assert.NotEqual(typeof(IPAddress), parameter.ParameterType);
                Assert.NotEqual(typeof(IPEndPoint), parameter.ParameterType);
            }
        }
    }

    [Fact]
    public async Task IdleProxy_OpensZeroOutboundConnections_AndCapturesNothing()
    {
        // Criterion #3: no destinations configured, no audit running → the
        // connector must never be invoked. A throwing double proves it.
        var connector = new CountingConnector(throwOnConnect: true);
        await using var proxy = new LoopbackInterceptingProxy(connector: connector);
        await proxy.StartAsync(new ProxyOptions(null));

        await Task.Delay(150);

        Assert.Equal(0, connector.Attempts);
        Assert.Empty(await proxy.DrainCapturedAsync());
    }

    [Fact]
    public async Task StartAsync_IsIdempotent_AndStopReleasesTheListener()
    {
        var proxy = new LoopbackInterceptingProxy();
        var first = await proxy.StartAsync(new ProxyOptions(null));
        var second = await proxy.StartAsync(new ProxyOptions(null));
        Assert.Equal(first, second);

        await proxy.StopAsync();
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, first.Port));
        await proxy.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_RejectsInvalidUpstreamConfiguration_Locally()
    {
        await using var proxy = new LoopbackInterceptingProxy();
        await Assert.ThrowsAsync<ArgumentException>(() => proxy.StartAsync(new ProxyOptions("ftp://proxy.invalid:21")));
    }
}
