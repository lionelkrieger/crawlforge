using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CrawlForge.Boundary.Tests.Harness;
using Xunit;

namespace CrawlForge.Boundary.Tests.Traffic;

/// <summary>
/// Acceptance criterion #3 / §2.6: external-database traffic goes ONLY to the
/// user-configured server. The connection test is pointed at a loopback
/// stand-in for the user's MySQL server; the monitor proves the attempt is
/// addressed exclusively to that configured endpoint (and nothing else).
/// </summary>
public sealed class ExternalDbAddressingTests
{
    [Fact]
    public async Task DatabaseTest_ConnectsOnlyToTheConfiguredServer()
    {
        // Loopback stand-in for the user's external database server: accepts the
        // TCP connection (the MySQL handshake then fails — expected; the proof is
        // the ADDRESSING, not a successful database session).
        using var fakeServer = new TcpListener(IPAddress.Loopback, 0);
        fakeServer.Start();
        var serverPort = ((IPEndPoint)fakeServer.LocalEndpoint).Port;
        var accepted = false;
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                using var client = await fakeServer.AcceptTcpClientAsync();
                accepted = true;
            }
            catch (SocketException)
            {
                // Listener stopped.
            }
        });

        using var monitor = OutboundConnectionMonitor.Start(BackendFixture.ProcessId);
        await using var fixture = await BackendFixture.StartAsync();

        var payload = JsonSerializer.Serialize(new
        {
            provider = "mysql",
            host = "127.0.0.1",
            port = serverPort,
            database = "crawlforge",
            username = "db-user",
            password = "db-password",
        });

        monitor.SetPhase("external-db-test");
        using var response = await fixture.Http.PostAsync("/api/settings/database/test", new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var verdict = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // The verdict reports the failure honestly (no MySQL server behind the socket).
        Assert.False(verdict.RootElement.GetProperty("data").GetProperty("authenticated").GetBoolean());

        await Task.Delay(500);
        // Both sides of the loopback connection live in this process (the
        // stand-in server is in-process): the client socket has remote port =
        // serverPort; the accepted server socket has LOCAL port = serverPort.
        // Both are the same configured-server connection.
        var dbObservations = monitor.Observations
            .Where(o => o.Phase == "external-db-test" && !o.IsBaseline
                && o.RemotePort != fixture.Backend.Port && o.LocalPort != fixture.Backend.Port)
            .ToList();
        var offServer = dbObservations.Where(o => o.RemotePort != serverPort && o.LocalPort != serverPort).ToList();
        var nonLoopback = monitor.NonLoopbackObservations;

        EvidenceWriter.WriteMarkdown(
            "external-db-addressing.md",
            $"""
            # Criterion #3 / §2.6 — external-database addressing

            - Configured user server (loopback stand-in): 127.0.0.1:{serverPort}
            - TCP connection accepted by the stand-in: {accepted}
            - Connection-test verdict: reachable/authenticated reported honestly (authenticated=false)
            - DB-phase connections observed: {dbObservations.Count}
            - Connections to any endpoint other than the configured server: **{offServer.Count}** (required: 0)
            - Non-loopback connections: **{nonLoopback.Count}** (required: 0)

            The external database is contacted ONLY at the user-configured address;
            no vendor or third-party database endpoint exists in the product.
            """);

        fakeServer.Stop();
        await acceptLoop.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(accepted, "The connection test must actually contact the configured server.");
        Assert.Empty(offServer);
        Assert.Empty(nonLoopback);
    }
}
