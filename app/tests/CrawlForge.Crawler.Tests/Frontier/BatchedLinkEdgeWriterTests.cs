using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Frontier;
using CrawlForge.Crawler.Tests.Frontier.Support;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Crawler.Tests.Frontier;

/// <summary>
/// FS-CRAWL-001 §5.8 storage-during-crawl: bounded-buffer transactional batching
/// of link_edges over the real persistence stack, internal/external classification,
/// anchor text, and relation flags.
/// </summary>
public class BatchedLinkEdgeWriterTests : IAsyncLifetime
{
    private FrontierTestDatabase _database = null!;
    private long _sourceId;
    private long _targetId;
    private const long AuditId = 1;
    private const string SourceUrl = "https://example.com/source";
    private const string TargetUrl = "https://example.com/target";

    public async Task InitializeAsync()
    {
        _database = await FrontierTestDatabase.CreateAsync();
        await _database.SeedAuditAsync(AuditId);
        _sourceId = await _database.InsertUrlAsync(AuditId, SourceUrl);
        _targetId = await _database.InsertUrlAsync(AuditId, TargetUrl);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private long? Resolve(string canonicalUrl) => canonicalUrl switch
    {
        SourceUrl => _sourceId,
        TargetUrl => _targetId,
        _ => null
    };

    private async Task<IReadOnlyList<LinkEdgeDto>> ReadEdgesAsync()
    {
        var repository = new SqlRepository<LinkEdgeDto>(_database.Provider);
        var page = await repository.ListAsync(new ListQuery(Limit: 500));
        return page.Items;
    }

    [Fact]
    public async Task InternalEdgePersistsWithResolvedIdentifiersAnchorAndFlags()
    {
        await using (var writer = new BatchedLinkEdgeWriter(_database.Provider, AuditId, Resolve))
        {
            await writer.EmitAsync(new DiscoveredLink(
                SourceUrl, TargetUrl, "Read more", LinkRelationFlags.Nofollow | LinkRelationFlags.Noreferrer, IsInternal: true));
            await writer.FlushAsync();
        }

        var edge = Assert.Single(await ReadEdgesAsync());
        Assert.Equal(AuditId, edge.AuditId);
        Assert.Equal(_sourceId, edge.SourceUrlId);
        Assert.Equal(TargetUrl, edge.CanonicalTargetUrl);
        Assert.Equal(_targetId, edge.TargetUrlId);
        Assert.Equal("Read more", edge.AnchorText);
        Assert.Equal(LinkRelationFlags.Nofollow | LinkRelationFlags.Noreferrer, edge.RelationFlags);
        Assert.True(edge.IsInternal);
    }

    [Fact]
    public async Task ExternalEdgeCarriesNullTargetReference()
    {
        await using (var writer = new BatchedLinkEdgeWriter(_database.Provider, AuditId, Resolve))
        {
            await writer.EmitAsync(new DiscoveredLink(
                SourceUrl, "https://external.example.org/page", null, LinkRelationFlags.None, IsInternal: false));
        }

        var edge = Assert.Single(await ReadEdgesAsync());
        Assert.Null(edge.TargetUrlId);
        Assert.False(edge.IsInternal);
        Assert.Equal("https://external.example.org/page", edge.CanonicalTargetUrl);
    }

    [Fact]
    public async Task BatchThresholdTriggersAutomaticTransactionalFlush()
    {
        await using var writer = new BatchedLinkEdgeWriter(_database.Provider, AuditId, Resolve, batchSize: 3);
        for (var i = 0; i < 3; i++)
        {
            await writer.EmitAsync(new DiscoveredLink(SourceUrl, TargetUrl, $"link-{i}", LinkRelationFlags.None, true));
        }

        Assert.Equal(3, writer.EdgesWritten);
        Assert.Equal(3, (await ReadEdgesAsync()).Count);
    }

    [Fact]
    public async Task BufferIsBoundedAndRemainderFlushesOnDispose()
    {
        var writer = new BatchedLinkEdgeWriter(_database.Provider, AuditId, Resolve, batchSize: 500);
        await writer.EmitAsync(new DiscoveredLink(SourceUrl, TargetUrl, null, LinkRelationFlags.None, true));
        await writer.EmitAsync(new DiscoveredLink(SourceUrl, TargetUrl, null, LinkRelationFlags.None, true));

        // Below the threshold: nothing persisted yet, buffer holds at most batchSize rows.
        Assert.Equal(0, writer.EdgesWritten);
        Assert.Empty(await ReadEdgesAsync());

        await writer.DisposeAsync();
        Assert.Equal(2, writer.EdgesWritten);
        Assert.Equal(2, (await ReadEdgesAsync()).Count);
    }

    [Fact]
    public async Task UnknownSourceUrlIsRejected()
    {
        await using var writer = new BatchedLinkEdgeWriter(_database.Provider, AuditId, Resolve);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.EmitAsync(new DiscoveredLink("https://example.com/unknown", TargetUrl, null, LinkRelationFlags.None, true)).AsTask());
    }

    [Fact]
    public async Task InternalTargetWithoutUrlRecordKeepsNullReference()
    {
        await using (var writer = new BatchedLinkEdgeWriter(_database.Provider, AuditId, Resolve))
        {
            // Internal but not yet crawled: resolver returns null; edge still records the canonical target.
            await writer.EmitAsync(new DiscoveredLink(SourceUrl, "https://example.com/not-yet-crawled", null, LinkRelationFlags.None, true));
        }

        var edge = Assert.Single(await ReadEdgesAsync());
        Assert.True(edge.IsInternal);
        Assert.Null(edge.TargetUrlId);
        Assert.Equal("https://example.com/not-yet-crawled", edge.CanonicalTargetUrl);
    }
}
