using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// Criterion #43 discovery-bounding evidence: list-only disables link and
/// sitemap discovery entirely; list-bounded enqueues only on-list URLs and
/// records off-list discoveries as skipped with reason <c>out-of-list</c>.
/// </summary>
public class ListDiscoveryGateTests
{
    private static ListModePolicy ListOnlyPolicy() =>
        new(ListDiscoveryMode.ListOnly, DepthCeiling: 0, DiscoveryEnabled: false,
            AllowedCanonicalUrls: new HashSet<string>(StringComparer.Ordinal) { "https://example.com/a" });

    private static ListModePolicy BoundedPolicy() =>
        new(ListDiscoveryMode.ListBounded, DepthCeiling: 1, DiscoveryEnabled: true,
            AllowedCanonicalUrls: new HashSet<string>(StringComparer.Ordinal) { "https://example.com/a", "https://example.com/b" });

    private static FrontierEntry Discovery(string url, DiscoverySource source = DiscoverySource.Link, int depth = 1) =>
        new(url, "example.com", depth, Priority: 0.5, source);

    /// <summary>List-only skips every link and sitemap discovery.</summary>
    [Theory]
    [InlineData(DiscoverySource.Link)]
    [InlineData(DiscoverySource.Sitemap)]
    [InlineData(DiscoverySource.Redirect)]
    public void ListOnly_Skips_All_Discovery(DiscoverySource source)
    {
        var gate = new ListDiscoveryGate(ListOnlyPolicy());

        var decision = gate.Decide(Discovery("https://example.com/a", source));

        Assert.False(decision.Enqueue);
        Assert.Equal(ListDiscoveryGate.DiscoveryDisabledReason, decision.SkipReason);
        Assert.Equal(1, gate.SkipCount);
    }

    /// <summary>Seeds and list entries always pass, even in list-only mode.</summary>
    [Theory]
    [InlineData(DiscoverySource.Seed)]
    [InlineData(DiscoverySource.List)]
    public void ListOnly_Always_Allows_Seeds_And_List(DiscoverySource source)
    {
        var gate = new ListDiscoveryGate(ListOnlyPolicy());

        var decision = gate.Decide(Discovery("https://example.com/anything", source, depth: 0));

        Assert.True(decision.Enqueue);
        Assert.Null(decision.SkipReason);
        Assert.Equal(0, gate.SkipCount);
    }

    /// <summary>List-bounded enqueues on-list discoveries.</summary>
    [Fact]
    public void ListBounded_Allows_On_List_Discovery()
    {
        var gate = new ListDiscoveryGate(BoundedPolicy());

        var decision = gate.Decide(Discovery("https://example.com/b"));

        Assert.True(decision.Enqueue);
        Assert.Equal(0, gate.SkipCount);
    }

    /// <summary>List-bounded skips off-list discoveries with reason out-of-list.</summary>
    [Fact]
    public void ListBounded_Skips_Off_List_Discovery()
    {
        var gate = new ListDiscoveryGate(BoundedPolicy());

        var decision = gate.Decide(Discovery("https://example.com/off-list"));

        Assert.False(decision.Enqueue);
        Assert.Equal(ListDiscoveryGate.OutOfListReason, decision.SkipReason);
        Assert.Equal(1, gate.OutOfListSkipCount);
        var skip = Assert.Single(gate.Skips);
        Assert.Equal("https://example.com/off-list", skip.Url);
        Assert.Equal(DiscoverySource.Link, skip.Source);
        Assert.Equal("out-of-list", skip.Reason);
    }

    /// <summary>Discoveries beyond the depth ceiling are skipped.</summary>
    [Fact]
    public void ListBounded_Skips_Beyond_Depth_Ceiling()
    {
        var gate = new ListDiscoveryGate(BoundedPolicy());

        var decision = gate.Decide(Discovery("https://example.com/a", depth: 2));

        Assert.False(decision.Enqueue);
        Assert.Equal(ListDiscoveryGate.DepthCeilingReason, decision.SkipReason);
    }

    /// <summary>Skip diagnostics are bounded while counters keep the full total.</summary>
    [Fact]
    public void Skip_Diagnostics_Are_Bounded()
    {
        var gate = new ListDiscoveryGate(BoundedPolicy());

        for (var i = 0; i < ListDiscoveryGate.MaxSkipDiagnostics + 25; i++)
        {
            gate.Decide(Discovery($"https://example.com/off-{i}"));
        }

        Assert.Equal(ListDiscoveryGate.MaxSkipDiagnostics + 25, gate.SkipCount);
        Assert.Equal(ListDiscoveryGate.MaxSkipDiagnostics, gate.Skips.Count);
    }
}
