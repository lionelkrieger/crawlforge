using System.Data.Common;
using CrawlForge.Audit.LinkGraph;
using CrawlForge.Audit.Tests.LinkGraph.Support;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.LinkGraph;

/// <summary>
/// Shared synthetic scale graph (seeded once for the whole collection):
/// 500,000 URLs, a full in-content chain, navigation hub shortcuts every 5th URL,
/// and 10,000 external edges. Local-only, no network.
/// </summary>
public sealed class ScaleGraphFixture : IAsyncLifetime
{
    /// <summary>URL count of the synthetic graph.</summary>
    public const int UrlCount = 500_000;

    /// <summary>Hub shortcut stride.</summary>
    public const int HubStride = 5;

    /// <summary>External edge count.</summary>
    public const int ExternalCount = 10_000;

    /// <summary>The migrated, seeded test database.</summary>
    public LinkGraphTestDatabase Database { get; private set; } = null!;

    /// <summary>URL identifiers in insertion order.</summary>
    public IReadOnlyList<long> UrlIds { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        Database = await LinkGraphTestDatabase.CreateAsync();
        await Database.SeedAuditAsync(900);

        var urlIds = new List<long>(UrlCount);
        await using var connection = await Database.Provider.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // URLs: one transaction, reused command/parameters (bounded memory).
        await using (var urlCommand = connection.CreateCommand())
        {
            urlCommand.Transaction = transaction;
            urlCommand.CommandText =
                "INSERT INTO url_records (audit_id, canonical_url, host, path, depth, discovery_source, status_class, robots_state, indexability_flags, is_indexable) " +
                "VALUES (900, @url, 'example.com', @path, @depth, @source, 0, 0, 0, 0)";
            var urlParameter = AddParameter(urlCommand, "@url", string.Empty);
            var pathParameter = AddParameter(urlCommand, "@path", string.Empty);
            var depthParameter = AddParameter(urlCommand, "@depth", 0);
            var sourceParameter = AddParameter(urlCommand, "@source", 0L);
            for (var i = 0; i < UrlCount; i++)
            {
                urlParameter.Value = $"https://example.com/p{i}";
                pathParameter.Value = $"/p{i}";
                depthParameter.Value = Math.Min(i, 100);
                sourceParameter.Value = i == 0 ? (long)DiscoverySource.Seed : (long)DiscoverySource.Link;
                await urlCommand.ExecuteNonQueryAsync();
            }
        }

        // Capture identifiers in insertion order.
        await using (var idCommand = connection.CreateCommand())
        {
            idCommand.Transaction = transaction;
            idCommand.CommandText = "SELECT id FROM url_records WHERE audit_id = 900 ORDER BY id";
            await using var reader = await idCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                urlIds.Add(reader.GetInt64(0));
            }
        }

        // Edges: chain (in-content) + hub shortcuts (navigation) + externals.
        await using (var edgeCommand = connection.CreateCommand())
        {
            edgeCommand.Transaction = transaction;
            edgeCommand.CommandText =
                "INSERT INTO link_edges (audit_id, source_url_id, canonical_target_url, target_url_id, anchor_text, relation_flags, is_internal, dom_zone) " +
                "VALUES (900, @source, @target, @targetId, 'a', 0, @internal, @zone)";
            var sourceParameter = AddParameter(edgeCommand, "@source", 0L);
            var targetParameter = AddParameter(edgeCommand, "@target", string.Empty);
            var targetIdParameter = AddParameter(edgeCommand, "@targetId", DBNull.Value);
            var internalParameter = AddParameter(edgeCommand, "@internal", 1L);
            var zoneParameter = AddParameter(edgeCommand, "@zone", DBNull.Value);

            for (var i = 1; i < UrlCount; i++)
            {
                sourceParameter.Value = urlIds[i - 1];
                targetParameter.Value = $"https://example.com/p{i}";
                targetIdParameter.Value = urlIds[i];
                internalParameter.Value = 1L;
                zoneParameter.Value = (long)DomZone.MainContent;
                await edgeCommand.ExecuteNonQueryAsync();
            }

            for (var i = HubStride; i < UrlCount; i += HubStride)
            {
                sourceParameter.Value = urlIds[0];
                targetParameter.Value = $"https://example.com/p{i}";
                targetIdParameter.Value = urlIds[i];
                internalParameter.Value = 1L;
                zoneParameter.Value = (long)DomZone.Navigation;
                await edgeCommand.ExecuteNonQueryAsync();
            }

            for (var i = 0; i < ExternalCount; i++)
            {
                sourceParameter.Value = urlIds[i * 50];
                targetParameter.Value = $"https://external.example.org/x{i}";
                targetIdParameter.Value = DBNull.Value;
                internalParameter.Value = 0L;
                zoneParameter.Value = DBNull.Value;
                await edgeCommand.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
        UrlIds = urlIds;
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await Database.DisposeAsync();

    private static DbParameter AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return parameter;
    }
}

/// <summary>Collection definition sharing one seeded scale graph across tests.</summary>
[CollectionDefinition(Name)]
public sealed class LinkGraphScaleCollection : ICollectionFixture<ScaleGraphFixture>
{
    /// <summary>Collection name.</summary>
    public const string Name = "LinkGraphScale";
}

/// <summary>
/// Scale proof (WP-03-04): the synthetic 500,000-URL / ~610,000-edge graph is
/// analyzed end-to-end — aggregates, orphan streaming, node streaming, and
/// weighted equity — with exact expectations and bounded-memory assertions.
/// All analyzer state is compact identifier-keyed structures; no full-row
/// materialization.
/// </summary>
[Collection(LinkGraphScaleCollection.Name)]
public sealed class LinkGraphScaleTests
{
    private const long MemoryBoundBytes = 512L * 1024 * 1024;

    private readonly ScaleGraphFixture _fixture;

    /// <summary>Creates the scale test over the shared fixture.</summary>
    /// <param name="fixture">The seeded scale graph.</param>
    public LinkGraphScaleTests(ScaleGraphFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AggregatesAt500kScaleAreExactAndMemoryBounded()
    {
        var analyzer = new LinkGraphAnalyzer(_fixture.Database.Provider);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        var aggregate = await analyzer.ComputeAggregateAsync(900);

        var after = GC.GetTotalMemory(forceFullCollection: false);
        Assert.True(after - baseline < MemoryBoundBytes,
            $"Aggregate computation retained {after - baseline:N0} bytes; expected bounded compact state under {MemoryBoundBytes:N0}.");

        const int chainEdges = ScaleGraphFixture.UrlCount - 1;
        const int hubEdges = (ScaleGraphFixture.UrlCount - 1) / ScaleGraphFixture.HubStride;
        Assert.Equal(ScaleGraphFixture.UrlCount, aggregate.UrlCount);
        Assert.Equal(chainEdges + hubEdges, aggregate.InternalEdgeCount);
        Assert.Equal(ScaleGraphFixture.ExternalCount, aggregate.ExternalEdgeCount);
        Assert.Equal(1, aggregate.OrphanCount); // only the seed lacks in-links
        Assert.Equal(1.0 / ScaleGraphFixture.UrlCount, aggregate.OrphanRate, 12);
        Assert.Equal(1, aggregate.SeedCount);
        Assert.Equal(2, aggregate.MaxInDegree); // chain + hub overlap
        Assert.Equal((double)(chainEdges + hubEdges) / ScaleGraphFixture.UrlCount, aggregate.AverageInDegree, 12);
        Assert.Equal(1, aggregate.ConnectedComponentCount); // the chain connects everything
        Assert.Equal(chainEdges, aggregate.ZoneDistribution[DomZone.MainContent]);
        Assert.Equal(hubEdges, aggregate.ZoneDistribution[DomZone.Navigation]);
    }

    [Fact]
    public async Task OrphanStreamingAt500kScaleYieldsOnlyTheSeed()
    {
        var analyzer = new LinkGraphAnalyzer(_fixture.Database.Provider);

        var orphans = new List<OrphanUrlRecord>();
        await foreach (var orphan in analyzer.StreamOrphansAsync(900))
        {
            orphans.Add(orphan);
        }

        Assert.Single(orphans);
        Assert.Equal(_fixture.UrlIds[0], orphans[0].UrlId);
        Assert.True(orphans[0].IsSeed);
    }

    [Fact]
    public async Task NodeStreamingAt500kScaleVisitsEveryUrl()
    {
        var readModel = new LinkGraphReadModelProvider(_fixture.Database.Provider, new LinkGraphAnalyzer(_fixture.Database.Provider));

        long count = 0;
        long lastId = 0;
        await foreach (var node in readModel.StreamNodesAsync(900))
        {
            count++;
            Assert.True(node.UrlId > lastId, "Nodes must stream in id order.");
            lastId = node.UrlId;
        }

        Assert.Equal(ScaleGraphFixture.UrlCount, count);
    }

    [Fact]
    public async Task WeightedEquityAt500kScaleCompletesWithPreservedMass()
    {
        var calculator = new WeightedLinkEquityCalculator(
            _fixture.Database.Provider,
            new WeightedEquityOptions(MaxIterations: 15));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        var result = await calculator.ComputeFullAsync(900, WeightedLinkEquityCalculator.DefaultZoneWeights);

        var after = GC.GetTotalMemory(forceFullCollection: false);
        Assert.True(after - baseline < MemoryBoundBytes,
            $"Equity computation retained {after - baseline:N0} bytes; expected bounded compact state under {MemoryBoundBytes:N0}.");

        Assert.Equal(ScaleGraphFixture.UrlCount, result.Weighted.Count);
        Assert.Equal(ScaleGraphFixture.UrlCount, result.Unweighted.Count);
        // Mass preservation holds at every iteration, converged or not.
        Assert.Equal(1.0, result.Weighted.Sum(s => s.Equity), 6);
        Assert.Equal(1.0, result.Unweighted.Sum(s => s.Equity), 6);
        Assert.InRange(result.Concentration, 0.0, 1.0);
    }
}
