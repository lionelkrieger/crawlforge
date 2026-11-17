using CrawlForge.Audit.LinkGraph;
using CrawlForge.Audit.Tests.LinkGraph.Support;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.LinkGraph;

/// <summary>
/// Graph read-model contract for the Sigma.js/Graphology surfaces: bounded
/// snapshots ordered by weighted equity, zone/external filters, and full
/// streaming feeds with degrees and equity attributes.
/// </summary>
public sealed class LinkGraphReadModelTests : IAsyncLifetime
{
    private LinkGraphTestDatabase _database = null!;
    private LinkGraphReadModelProvider _readModel = null!;
    private long _hub;
    private long _a;
    private long _b;
    private long _orphan;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _database = await LinkGraphTestDatabase.CreateAsync();
        _readModel = new LinkGraphReadModelProvider(_database.Provider, new LinkGraphAnalyzer(_database.Provider));
        await _database.SeedAuditAsync(400);

        _hub = await _database.InsertUrlAsync(400, "https://example.com/hub", source: DiscoverySource.Seed);
        _a = await _database.InsertUrlAsync(400, "https://example.com/a");
        _b = await _database.InsertUrlAsync(400, "https://example.com/b");
        _orphan = await _database.InsertUrlAsync(400, "https://example.com/orphan");

        await _database.InsertEdgeAsync(400, _hub, _a, "https://example.com/a", DomZone.MainContent, "content region", weightedEquity: 0.25);
        await _database.InsertEdgeAsync(400, _hub, _b, "https://example.com/b", DomZone.Navigation, "landmark nav");
        await _database.InsertEdgeAsync(400, _a, _b, "https://example.com/b", DomZone.Footer, "landmark footer", LinkRelationFlags.Nofollow);
        await _database.InsertEdgeAsync(400, _hub, null, "https://external.example.org/", isInternal: false);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _database.DisposeAsync();

    private async Task SeedEquityAsync()
    {
        await _database.InsertMetricAsync(400, MetricKeys.LinkEquityWeighted, $"{{\"urlId\":{_a},\"percentile\":1.0}}", 0.5);
        await _database.InsertMetricAsync(400, MetricKeys.LinkEquityWeighted, $"{{\"urlId\":{_b},\"percentile\":0.66}}", 0.3);
        await _database.InsertMetricAsync(400, MetricKeys.LinkEquityWeighted, $"{{\"urlId\":{_hub},\"percentile\":0.33}}", 0.15);
        await _database.InsertMetricAsync(400, MetricKeys.LinkEquityWeighted, $"{{\"urlId\":{_orphan},\"percentile\":0.0}}", 0.05);
        await _database.InsertMetricAsync(400, LinkGraphMetricKeys.LinkEquityUnweighted, $"{{\"urlId\":{_a}}}", 0.33);
    }

    [Fact]
    public async Task SnapshotReturnsTopNodesByWeightedEquityWithEdgesBetweenThem()
    {
        await SeedEquityAsync();

        var snapshot = await _readModel.GetSnapshotAsync(400, new LinkGraphQuery(NodeLimit: 2));

        Assert.Equal([_a, _b], snapshot.Nodes.Select(n => n.UrlId).ToArray());
        Assert.Single(snapshot.Edges);
        Assert.Equal(_a, snapshot.Edges[0].SourceUrlId);
        Assert.Equal(_b, snapshot.Edges[0].TargetUrlId);
        Assert.Equal(4, snapshot.TotalNodeCount);
        Assert.Equal(4, snapshot.TotalEdgeCount);
        Assert.Equal(4, snapshot.Aggregate.UrlCount);
    }

    [Fact]
    public async Task SnapshotWithoutEquityFallsBackToIdOrder()
    {
        var snapshot = await _readModel.GetSnapshotAsync(400, new LinkGraphQuery(NodeLimit: 2));

        Assert.Equal([_hub, _a], snapshot.Nodes.Select(n => n.UrlId).ToArray());
    }

    [Fact]
    public async Task ZoneFilterRestrictsSnapshotEdges()
    {
        var snapshot = await _readModel.GetSnapshotAsync(400, new LinkGraphQuery(NodeLimit: 10, ZoneFilter: DomZone.Navigation));

        Assert.Single(snapshot.Edges);
        Assert.Equal(DomZone.Navigation, snapshot.Edges[0].Zone);
        Assert.Equal("landmark nav", snapshot.Edges[0].ZoneEvidence);
    }

    [Fact]
    public async Task ExternalEdgesAreIncludedOnlyWhenRequested()
    {
        var without = await _readModel.GetSnapshotAsync(400, new LinkGraphQuery(NodeLimit: 10));
        var with = await _readModel.GetSnapshotAsync(400, new LinkGraphQuery(NodeLimit: 10, IncludeExternalEdges: true));

        Assert.Equal(3, without.Edges.Count);
        Assert.Equal(4, with.Edges.Count);
        var external = with.Edges.Single(e => !e.IsInternal);
        Assert.Null(external.TargetUrlId);
        Assert.Equal("https://external.example.org/", external.CanonicalTargetUrl);
    }

    [Fact]
    public async Task StreamNodesCarriesDegreesEquityOrphanAndSeedMarkers()
    {
        await SeedEquityAsync();

        var nodes = new List<LinkGraphNode>();
        await foreach (var node in _readModel.StreamNodesAsync(400))
        {
            nodes.Add(node);
        }

        Assert.Equal(4, nodes.Count);
        var a = nodes.Single(n => n.UrlId == _a);
        Assert.Equal(1, a.InDegree);
        Assert.Equal(1, a.OutDegree);
        Assert.Equal(0.5, a.WeightedEquity, 10);
        Assert.Equal(0.33, a.UnweightedEquity, 10);
        Assert.False(a.IsOrphan);
        Assert.False(a.IsSeed);

        var hub = nodes.Single(n => n.UrlId == _hub);
        Assert.True(hub.IsSeed);
        Assert.True(hub.IsOrphan); // no in-links, but a seed entry point
        Assert.Equal(2, hub.OutDegree); // external edge is not an internal out-link

        var orphan = nodes.Single(n => n.UrlId == _orphan);
        Assert.True(orphan.IsOrphan);
        Assert.False(orphan.IsSeed);
        Assert.Equal(0, orphan.InDegree);
    }

    [Fact]
    public async Task StreamEdgesCarriesAllAdditiveFields()
    {
        var edges = new List<LinkGraphEdge>();
        await foreach (var edge in _readModel.StreamEdgesAsync(400))
        {
            edges.Add(edge);
        }

        Assert.Equal(4, edges.Count);
        var first = edges[0];
        Assert.Equal(_hub, first.SourceUrlId);
        Assert.Equal(_a, first.TargetUrlId);
        Assert.Equal(DomZone.MainContent, first.Zone);
        Assert.Equal("content region", first.ZoneEvidence);
        Assert.Equal(0.25, first.WeightedEquity);
        Assert.True(first.IsInternal);

        var nofollow = edges.Single(e => e.RelationFlags == LinkRelationFlags.Nofollow);
        Assert.Equal(DomZone.Footer, nofollow.Zone);
    }

    [Fact]
    public async Task InvalidNodeLimitIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _readModel.GetSnapshotAsync(400, new LinkGraphQuery(NodeLimit: 0)));
    }

    [Fact]
    public async Task EmptyAuditProducesEmptySnapshotAndFeeds()
    {
        await _database.SeedAuditAsync(401);

        var snapshot = await _readModel.GetSnapshotAsync(401);

        Assert.Empty(snapshot.Nodes);
        Assert.Empty(snapshot.Edges);
        Assert.Equal(0, snapshot.TotalNodeCount);

        var nodeCount = 0;
        await foreach (var _ in _readModel.StreamNodesAsync(401))
        {
            nodeCount++;
        }

        Assert.Equal(0, nodeCount);
    }
}
