using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CrawlForge.Boundary.Tests.Harness;
using CrawlForge.Crawler.Proxy;
using Xunit;

namespace CrawlForge.Boundary.Tests.Listeners;

/// <summary>
/// Acceptance criterion #22: every listener the product opens is loopback-only.
/// The sweep enumerates the OS listener table for the backend process while
/// Kestrel (REST + hub/SSE), the MCP server, and the intercepting proxy are all
/// active, and proves non-loopback interfaces cannot reach the backend.
/// </summary>
public sealed class LoopbackListenerSweepTests
{
    [Fact]
    public async Task AllProductListeners_AreLoopbackOnly()
    {
        await using var fixture = await BackendFixture.StartAsync();
        await using var proxy = new LoopbackInterceptingProxy();
        var proxyEndpoint = await proxy.StartAsync(new CrawlForge.Contracts.Crawl.ProxyOptions(null));

        // Let every listener settle.
        await Task.Delay(500);

        var listeners = OutboundConnectionMonitor.ReadListeners(BackendFixture.ProcessId);
        var nonLoopback = listeners
            .Where(l => !IPAddress.TryParse(l.Address, out var address) || !IPAddress.IsLoopback(address))
            .ToList();

        EvidenceWriter.WriteMarkdown(
            "listener-sweep.md",
            $"""
            # Criterion #22 — loopback-only listener sweep

            Active surfaces: Kestrel REST + SignalR hub/SSE (port {fixture.Backend.Port}),
            MCP loopback server, intercepting proxy (port {proxyEndpoint.Port}).

            | Local address | Port | Loopback |
            |---|---|---|
            {string.Join("\n", listeners.Select(l => $"| {l.Address} | {l.Port} | {(IPAddress.TryParse(l.Address, out var a) && IPAddress.IsLoopback(a))} |"))}

            - Listeners observed: {listeners.Count}
            - Non-loopback listeners: **{nonLoopback.Count}** (required: 0)
            - Backend port present: {listeners.Any(l => l.Port == fixture.Backend.Port)}
            - Proxy port present: {listeners.Any(l => l.Port == proxyEndpoint.Port)}
            """);

        Assert.NotEmpty(listeners);
        Assert.Contains(listeners, l => l.Port == fixture.Backend.Port);
        Assert.Contains(listeners, l => l.Port == proxyEndpoint.Port);
        Assert.Empty(nonLoopback);
    }

    [Fact]
    public async Task Backend_IsUnreachableViaNonLoopbackInterfaces()
    {
        await using var fixture = await BackendFixture.StartAsync();

        var nonLoopbackAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => !IPAddress.IsLoopback(a) && a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .ToList();

        var results = new List<string>();
        foreach (var address in nonLoopbackAddresses)
        {
            using var client = new TcpClient();
            string outcome;
            try
            {
                var connect = client.ConnectAsync(address, fixture.Backend.Port);
                var completed = await Task.WhenAny(connect, Task.Delay(2000));
                outcome = completed == connect && client.Connected
                    ? "REACHABLE (VIOLATION)"
                    : "unreachable";
            }
            catch (SocketException)
            {
                outcome = "unreachable (refused)";
            }

            results.Add($"- {address}:{fixture.Backend.Port} → {outcome}");
            Assert.DoesNotContain("VIOLATION", outcome);
        }

        EvidenceWriter.WriteMarkdown(
            "non-loopback-unreachability.md",
            $"""
            # Criterion #22/#16 — non-loopback unreachability

            Backend port {fixture.Backend.Port} probed via every non-loopback IPv4
            interface of this machine:

            {string.Join("\n", results)}

            Interfaces probed: {nonLoopbackAddresses.Count} (0 means the machine has no
            non-loopback IPv4 interface; the listener sweep still proves loopback-only binds).
            """);
    }
}
