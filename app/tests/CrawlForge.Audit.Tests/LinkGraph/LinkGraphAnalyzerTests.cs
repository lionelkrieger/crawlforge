using CrawlForge.Audit.LinkGraph;
using CrawlForge.Audit.Tests.LinkGraph.Support;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.LinkGraph;

/// <summary>
/// Streaming graph aggregates and orphan detection over the real migrated stack,
/// with hand-computed expectations and audit isolation.
/// </summary>
public sealed class LinkGraphAnalyzerTests : IAsyncLifetime
{
    private LinkGraphTestDatabase _database = null!;
    private LinkGraphAnalyzer _analyzer = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _database = await LinkGraphTestDatabase.CreateAsync();
        _analyzer = new LinkGraphAnalyzer(_database.Provider);
        await _database.SeedAuditAsync(100);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _database.DisposeAsync();

    private async Task<(long A, long B, long C, long D, long E)> SeedGraphAsync()
    {
        var a = await _database.InsertUrlAsync(100, "https://example.com/a", source: DiscoverySource.Seed);
        var b = await _database.InsertUrlAsync(100, "https://example.com/b");
        var c = await _database.InsertUrlAsync(100, "https://example.com/c");
        var d = await _database.InsertUrlAsync(100, "https://example.com/d");
        var e = await _database.InsertUrlAsync(100, "https://example.com/e", source: DiscoverySource.List);

        await _database.InsertEdgeAsync(100, a, b, "https://example.com/b", DomZone.MainContent, "content region");
        await _database.InsertEdgeAsync(100, a, c, "https://example.com/c", DomZone.Navigation, "landmark nav");
        await _database.InsertEdgeAsync(100, b, c, "https://example.com/c", DomZone.Footer, "landmark footer");
        await _database.InsertEdgeAsync(100, c, a, "https://example.com/a", DomZone.Breadcrumb, "breadcrumb marker");
        await _database.InsertEdgeAsync(100, d, d, "https://example.com/d", DomZone.MainContent, "self-loop");
        await _database.InsertEdgeAsync(100, a, null, "https://external.example.org/", isInternal: false);
        await _database.InsertEdgeAsync(100, e, b, "https://example.com/b", DomZone.Sidebar, "landmark aside");
        return (a, b, c, d, e);
    }

    [Fact]
    public async Task AggregateMatchesHandComputedExpectations()
    {
        await SeedGraphAsync();

        var aggregate = await _analyzer.ComputeAggregateAsync(100);

        Assert.Equal(5, aggregate.UrlCount);
        Assert.Equal(6, aggregate.InternalEdgeCount);
        Assert.Equal(1, aggregate.ExternalEdgeCount);
        Assert.Equal(2, aggregate.OrphanCount);
        Assert.Equal(0.4, aggregate.OrphanRate, 10);
        Assert.Equal(2, aggregate.SeedCount);
        Assert.Equal(1.0, aggregate.AverageInDegree, 10);
        Assert.Equal(2, aggregate.MaxInDegree);
        // {A,B,C,E} connected; D isolated (self-loop does not connect).
        Assert.Equal(2, aggregate.ConnectedComponentCount);
        Assert.Equal(2, aggregate.ZoneDistribution[DomZone.MainContent]);
        Assert.Equal(1, aggregate.ZoneDistribution[DomZone.Navigation]);
        Assert.Equal(1, aggregate.ZoneDistribution[DomZone.Footer]);
        Assert.Equal(1, aggregate.ZoneDistribution[DomZone.Breadcrumb]);
        Assert.Equal(1, aggregate.ZoneDistribution[DomZone.Sidebar]);
    }

    [Fact]
    public async Task OrphanStreamYieldsExactlyTheUnlinkedUrlsWithSeedMarkers()
    {
        var (_, _, _, d, e) = await SeedGraphAsync();

        var orphans = new List<OrphanUrlRecord>();
        await foreach (var orphan in _analyzer.StreamOrphansAsync(100))
        {
            orphans.Add(orphan);
        }

        Assert.Equal([d, e], orphans.Select(o => o.UrlId).ToArray());
        Assert.False(orphans[0].IsSeed);
        Assert.True(orphans[1].IsSeed);
        Assert.Equal(DiscoverySource.List, orphans[1].DiscoverySource);
        Assert.Equal("https://example.com/d", orphans[0].CanonicalUrl);
    }

    [Fact]
    public async Task SelfLoopsDoNotCountAsInLinks()
    {
        var d = await _database.InsertUrlAsync(100, "https://example.com/self");
        await _database.InsertEdgeAsync(100, d, d, "https://example.com/self", DomZone.MainContent);

        var aggregate = await _analyzer.ComputeAggregateAsync(100);

        Assert.Equal(1, aggregate.OrphanCount);
        Assert.Equal(0, aggregate.MaxInDegree);
        Assert.Equal(1, aggregate.InternalEdgeCount);
    }

    [Fact]
    public async Task EmptyAuditProducesZeroedAggregateAndNoOrphans()
    {
        var aggregate = await _analyzer.ComputeAggregateAsync(100);

        Assert.Equal(0, aggregate.UrlCount);
        Assert.Equal(0, aggregate.OrphanCount);
        Assert.Equal(0, aggregate.OrphanRate);
        Assert.Equal(0, aggregate.ConnectedComponentCount);
        Assert.Empty(aggregate.ZoneDistribution);

        var orphans = new List<OrphanUrlRecord>();
        await foreach (var orphan in _analyzer.StreamOrphansAsync(100))
        {
            orphans.Add(orphan);
        }

        Assert.Empty(orphans);
    }

    [Fact]
    public async Task AggregatesAreIsolatedPerAudit()
    {
        await SeedGraphAsync();
        await _database.SeedAuditAsync(200);
        var other = await _database.InsertUrlAsync(200, "https://example.com/other", source: DiscoverySource.Seed);
        await _database.InsertEdgeAsync(200, other, other, "https://example.com/other");

        var aggregate = await _analyzer.ComputeAggregateAsync(100);

        Assert.Equal(5, aggregate.UrlCount);
        Assert.Equal(6, aggregate.InternalEdgeCount);
    }

    [Fact]
    public async Task UnclassifiedEdgesAreCountedButAbsentFromZoneDistribution()
    {
        var a = await _database.InsertUrlAsync(100, "https://example.com/a", source: DiscoverySource.Seed);
        var b = await _database.InsertUrlAsync(100, "https://example.com/b");
        await _database.InsertEdgeAsync(100, a, b, "https://example.com/b");

        var aggregate = await _analyzer.ComputeAggregateAsync(100);

        Assert.Equal(1, aggregate.InternalEdgeCount);
        Assert.Empty(aggregate.ZoneDistribution);
        // The seed has no in-links, so it is an orphan entry point (IsSeed-marked).
        Assert.Equal(1, aggregate.OrphanCount);
    }

    [Fact]
    public async Task ChainGraphFormsASingleComponent()
    {
        var previous = await _database.InsertUrlAsync(100, "https://example.com/0", source: DiscoverySource.Seed);
        for (var i = 1; i < 25; i++)
        {
            var next = await _database.InsertUrlAsync(100, $"https://example.com/{i}");
            await _database.InsertEdgeAsync(100, previous, next, $"https://example.com/{i}", DomZone.MainContent);
            previous = next;
        }

        var aggregate = await _analyzer.ComputeAggregateAsync(100);

        Assert.Equal(1, aggregate.ConnectedComponentCount);
        Assert.Equal(1, aggregate.OrphanCount); // only the seed lacks in-links
        Assert.Equal(24, aggregate.InternalEdgeCount);
    }
}
