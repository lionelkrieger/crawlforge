using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CrawlForge.Contracts.Security;
using CrawlForge.Security.Handshake;
using CrawlForge.Security.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Security.Tests.Handshake;

/// <summary>
/// Loopback port handshake (FS-CRAWL-001 §2.2): port + per-launch readiness
/// token published to a local file, loopback-only enforcement, preferred-port
/// fallback, token validation, and a descriptor free of identity dimensions.
/// </summary>
public sealed class LoopbackHandshakeTests
{
    [Fact]
    public async Task StartAsync_PublishesHandshakeFile_WithPortAndToken()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.RootPath, "handshake.json");
        var handshake = new LoopbackPortHandshake(path);

        LoopbackHandshake result = await handshake.StartAsync();
        try
        {
            Assert.InRange(result.Port, 1, 65535);
            Assert.False(string.IsNullOrEmpty(result.SessionToken));
            Assert.Equal(Path.GetFullPath(path), result.HandshakeFilePath);
            Assert.True(File.Exists(result.HandshakeFilePath));
            Assert.True(handshake.IsStarted);

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(result.HandshakeFilePath));
            Assert.Equal(LoopbackPortHandshake.HandshakeFileVersion, doc.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(result.Port, doc.RootElement.GetProperty("port").GetInt32());
            Assert.Equal(result.SessionToken, doc.RootElement.GetProperty("sessionToken").GetString());
            Assert.True(doc.RootElement.TryGetProperty("createdAtUtc", out _));

            Assert.True(handshake.ValidateSessionToken(result.SessionToken));
        }
        finally
        {
            await handshake.StopAsync();
        }
    }

    [Fact]
    public async Task StartAsync_UsesFreePreferredPort()
    {
        using var temp = new TempDirectory();
        int freePort = FindFreeLoopbackPort();
        var handshake = new LoopbackPortHandshake(Path.Combine(temp.RootPath, "handshake.json"), freePort);

        LoopbackHandshake result = await handshake.StartAsync();
        try
        {
            Assert.Equal(freePort, result.Port);
        }
        finally
        {
            await handshake.StopAsync();
        }
    }

    [Fact]
    public async Task StartAsync_OccupiedPreferredPort_FallsBackToEphemeral()
    {
        using var temp = new TempDirectory();
        var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        int occupiedPort = ((IPEndPoint)occupier.LocalEndpoint).Port;

        var handshake = new LoopbackPortHandshake(Path.Combine(temp.RootPath, "handshake.json"), occupiedPort);
        try
        {
            LoopbackHandshake result = await handshake.StartAsync();

            Assert.NotEqual(occupiedPort, result.Port);
            Assert.InRange(result.Port, 1, 65535);
            Assert.True(handshake.ValidateSessionToken(result.SessionToken));
        }
        finally
        {
            await handshake.StopAsync();
            occupier.Stop();
        }
    }

    [Fact]
    public async Task StartAsync_TwiceWithoutStop_Throws()
    {
        using var temp = new TempDirectory();
        var handshake = new LoopbackPortHandshake(Path.Combine(temp.RootPath, "handshake.json"));

        await handshake.StartAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => handshake.StartAsync());
        }
        finally
        {
            await handshake.StopAsync();
        }
    }

    [Fact]
    public async Task StopAsync_RemovesFile_AndInvalidatesToken()
    {
        using var temp = new TempDirectory();
        var handshake = new LoopbackPortHandshake(Path.Combine(temp.RootPath, "handshake.json"));
        LoopbackHandshake result = await handshake.StartAsync();

        await handshake.StopAsync();

        Assert.False(File.Exists(result.HandshakeFilePath));
        Assert.False(handshake.IsStarted);
        Assert.False(handshake.ValidateSessionToken(result.SessionToken));
    }

    [Fact]
    public async Task StopAsync_WithoutStart_IsNoOp()
    {
        using var temp = new TempDirectory();
        var handshake = new LoopbackPortHandshake(Path.Combine(temp.RootPath, "handshake.json"));

        await handshake.StopAsync();

        Assert.False(handshake.IsStarted);
        Assert.False(File.Exists(handshake.HandshakeFilePath));
    }

    [Fact]
    public async Task Restart_PublishesFreshPerLaunchToken()
    {
        using var temp = new TempDirectory();
        var handshake = new LoopbackPortHandshake(Path.Combine(temp.RootPath, "handshake.json"));

        LoopbackHandshake first = await handshake.StartAsync();
        await handshake.StopAsync();
        LoopbackHandshake second = await handshake.StartAsync();
        try
        {
            Assert.NotEqual(first.SessionToken, second.SessionToken);
            Assert.False(handshake.ValidateSessionToken(first.SessionToken));
            Assert.True(handshake.ValidateSessionToken(second.SessionToken));
            Assert.True(File.Exists(second.HandshakeFilePath));
        }
        finally
        {
            await handshake.StopAsync();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    public async Task ValidateSessionToken_RejectsWrongTokens(string? token)
    {
        using var temp = new TempDirectory();
        var handshake = new LoopbackPortHandshake(Path.Combine(temp.RootPath, "handshake.json"));
        await handshake.StartAsync();
        try
        {
            Assert.False(handshake.ValidateSessionToken(token!));
        }
        finally
        {
            await handshake.StopAsync();
        }
    }

    [Fact]
    public async Task HandshakeFile_ContainsOnlyContractFields_NoIdentityDimensions()
    {
        using var temp = new TempDirectory();
        var handshake = new LoopbackPortHandshake(Path.Combine(temp.RootPath, "handshake.json"));
        LoopbackHandshake result = await handshake.StartAsync();
        try
        {
            string raw = File.ReadAllText(result.HandshakeFilePath);
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "version", "port", "sessionToken", "createdAtUtc",
            };

            using JsonDocument doc = JsonDocument.Parse(raw);
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                Assert.Contains(property.Name, allowed);
            }

            if (!string.IsNullOrEmpty(Environment.UserName))
            {
                Assert.DoesNotContain(Environment.UserName, raw, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(Environment.MachineName, raw, StringComparison.Ordinal);
        }
        finally
        {
            await handshake.StopAsync();
        }
    }

    [Fact]
    public async Task SessionTokens_AreRandomAcrossIndependentLaunches()
    {
        using var temp = new TempDirectory();
        var handshakeA = new LoopbackPortHandshake(Path.Combine(temp.RootPath, "a.json"));
        var handshakeB = new LoopbackPortHandshake(Path.Combine(temp.RootPath, "b.json"));

        LoopbackHandshake a = await handshakeA.StartAsync();
        LoopbackHandshake b = await handshakeB.StartAsync();
        try
        {
            Assert.NotEqual(a.SessionToken, b.SessionToken);
            Assert.False(handshakeA.ValidateSessionToken(b.SessionToken));
            Assert.False(handshakeB.ValidateSessionToken(a.SessionToken));
        }
        finally
        {
            await handshakeA.StopAsync();
            await handshakeB.StopAsync();
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Constructor_RejectsInvalidPreferredPort(int port)
    {
        using var temp = new TempDirectory();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LoopbackPortHandshake(Path.Combine(temp.RootPath, "handshake.json"), port));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsEmptyPath(string path)
    {
        Assert.Throws<ArgumentException>(() => new LoopbackPortHandshake(path));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("8.8.8.8")]
    [InlineData("192.168.1.10")]
    [InlineData("203.0.113.7")]
    public void LoopbackGuard_RejectsNonLoopbackAddresses(string address)
    {
        IPAddress parsed = IPAddress.Parse(address);

        Assert.False(LoopbackGuard.IsLoopback(parsed));
        Assert.Throws<ArgumentException>(() => LoopbackGuard.EnsureLoopback(parsed));
        Assert.Throws<ArgumentException>(() => LoopbackGuard.EnsureLoopback(new IPEndPoint(parsed, 8080)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    public void LoopbackGuard_AcceptsLoopbackAddresses(string address)
    {
        IPAddress parsed = IPAddress.Parse(address);

        Assert.True(LoopbackGuard.IsLoopback(parsed));
        Assert.Null(Record.Exception(() => LoopbackGuard.EnsureLoopback(parsed)));
        Assert.Null(Record.Exception(() => LoopbackGuard.EnsureLoopback(new IPEndPoint(parsed, 8080))));
    }

    private static int FindFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}