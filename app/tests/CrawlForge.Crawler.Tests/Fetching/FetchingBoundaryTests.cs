using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Tests.Fetching.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Network-boundary evidence (FS-CRAWL-001 §2.6, criterion #3, exclusion rows
/// 1/2/3): outbound traffic classes are crawl targets and the configured
/// loopback proxy only; no OS/hardware identity is consumed; no telemetry or
/// remote issuance exists; socket usage is confined to the fetcher.
/// </summary>
public sealed class FetchingBoundaryTests
{
    [Theory]
    [InlineData("ftp://target.example.com/file")]
    [InlineData("file:///C:/Windows/system.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("gopher://target.example.com/")]
    public async Task Fetch_NonHttpScheme_RejectedBeforeAnyTransport(string url)
    {
        var handler = new ScriptedHttpMessageHandler();
        using var client = new HttpClient(handler);
        using var fetcher = new HttpFetcher(new HttpFetcherOptions(), client);

        await Assert.ThrowsAsync<ArgumentException>(() => fetcher.FetchAsync(new HttpFetchRequest(url)));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Proxy_EndpointMustBeLoopback()
    {
        var proxy = new FakeLoopbackInterceptingProxy(new ProxyEndpoint("proxy.remote.example.com", 8080));
        using var client = new HttpClient();
        using var fetcher = new HttpFetcher(new HttpFetcherOptions(), client, interceptingProxy: proxy);

        await Assert.ThrowsAsync<ArgumentException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://crawl-target.example/")));
    }

    [Fact]
    public void FetchingSources_ConsumeNoOsOrHardwareIdentity()
    {
        string[] forbidden =
        {
            "Environment.MachineName",
            "Environment.UserName",
            "Environment.UserDomainName",
            "Microsoft.Win32",
            "RegistryKey",
            "System.Management",
            "MachineGuid",
            "Dns.",
            "GetHostEntry",
            "GetAdaptersInfo",
            "NetworkInterface",
        };

        foreach (string file in FetchingSourceFiles())
        {
            string source = File.ReadAllText(file);
            foreach (string token in forbidden)
            {
                Assert.False(
                    source.Contains(token, StringComparison.Ordinal),
                    $"Identity/telemetry token '{token}' found in {Path.GetFileName(file)}.");
            }
        }
    }

    [Fact]
    public void FetchingSources_ContainNoTelemetryOrRemoteIssuance()
    {
        string[] forbidden =
        {
            "telemetry",
            "analytics",
            "phone-home",
            "license-server",
            "activation",
        };

        foreach (string file in FetchingSourceFiles())
        {
            string source = File.ReadAllText(file);
            foreach (string token in forbidden)
            {
                Assert.False(
                    source.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"Outbound-traffic token '{token}' found in {Path.GetFileName(file)}.");
            }
        }
    }

    [Fact]
    public void SocketAndHttpClientUsage_ConfinedToFetcher()
    {
        string[] transportTokens = { "HttpClient", "WebProxy", "Socket", "IPAddress", "HttpListener" };

        // Transport construction is confined to the fetcher and the dedicated
        // crawl-target transport factory (WP-09-11 follow-up: the factory is
        // the approved NET-02 boundary-scope construction point the execution
        // engine composes; every OTHER fetching source stays transport-free).
        string[] transportFiles = { "HttpFetcher.cs", "CrawlTargetTransportFactory.cs" };

        foreach (string file in FetchingSourceFiles())
        {
            if (transportFiles.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            foreach (string token in transportTokens)
            {
                Assert.False(
                    source.Contains(token, StringComparison.Ordinal),
                    $"Transport token '{token}' outside HttpFetcher.cs/CrawlTargetTransportFactory.cs: {Path.GetFileName(file)}.");
            }
        }
    }

    private static IReadOnlyList<string> FetchingSourceFiles()
    {
        string? directory = new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName;
        while (directory is not null && !File.Exists(Path.Combine(directory, "CrawlForge.sln")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.NotNull(directory);
        string fetchingDir = Path.Combine(directory!, "src", "backend", "CrawlForge.Crawler", "Fetching");
        Assert.True(Directory.Exists(fetchingDir), $"Fetching source directory not found: {fetchingDir}");
        return Directory.GetFiles(fetchingDir, "*.cs", SearchOption.AllDirectories);
    }
}