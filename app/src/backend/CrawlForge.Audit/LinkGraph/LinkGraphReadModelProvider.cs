using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.LinkGraph;

/// <summary>
/// Graph read-model provider for the phase-06 Sigma.js/Graphology surfaces.
/// Interactive snapshots are bounded windows (top nodes by weighted equity plus
/// the edges between them); full-graph consumers use the streaming feeds. All
/// passes stream rows with compact identifier-keyed state only — no full-row
/// materialization — preserving the 500k+ URL scale invariant.
/// </summary>
public sealed class LinkGraphReadModelProvider : ILinkGraphReadModel
{
    /// <summary>Hard cap on edges returned in one snapshot window (UI sanity bound).</summary>
    public const int MaxSnapshotEdges = 50_000;

    private readonly IDbProvider _provider;
    private readonly ILinkGraphAnalyzer _analyzer;

    /// <summary>Creates the read-model provider.</summary>
    /// <param name="provider">The configured database provider.</param>
    /// <param name="analyzer">The streaming graph analyzer for aggregates.</param>
    public LinkGraphReadModelProvider(IDbProvider provider, ILinkGraphAnalyzer analyzer)
    {
        _provider = provider;
        _analyzer = analyzer;
    }

    /// <inheritdoc />
    public async Task<LinkGraphSnapshot> GetSnapshotAsync(long auditId, LinkGraphQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new LinkGraphQuery();
        if (query.NodeLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "The node limit must be positive.");
        }

        var aggregate = await _analyzer.ComputeAggregateAsync(auditId, cancellationToken);
        var (weighted, unweighted) = await LoadEquityMapsAsync(auditId, cancellationToken);

        // Select the window: top nodes by weighted equity, id order as tie-break
        // (and as the ordering when equity has not been computed yet).
        var candidates = new List<WindowCandidate>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM url_records WHERE audit_id = @audit ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                candidates.Add(new WindowCandidate(id, weighted.GetValueOrDefault(id)));
            }
        }

        candidates.Sort(static (a, b) =>
        {
            var byEquity = b.Equity.CompareTo(a.Equity);
            return byEquity != 0 ? byEquity : a.UrlId.CompareTo(b.UrlId);
        });
        var selected = new HashSet<long>();
        foreach (var candidate in candidates.Take(query.NodeLimit))
        {
            selected.Add(candidate.UrlId);
        }

        // One edge pass: whole-graph degrees plus the bounded window edge list.
        var inDegree = new Dictionary<long, long>();
        var outDegree = new Dictionary<long, long>();
        var windowEdges = new List<LinkGraphEdge>();
        await foreach (var edge in StreamEdgesCoreAsync(auditId, cancellationToken))
        {
            if (edge.IsInternal)
            {
                outDegree[edge.SourceUrlId] = outDegree.GetValueOrDefault(edge.SourceUrlId) + 1;
                if (edge.TargetUrlId is { } target && target != edge.SourceUrlId)
                {
                    inDegree[target] = inDegree.GetValueOrDefault(target) + 1;
                }
            }

            if (windowEdges.Count >= MaxSnapshotEdges)
            {
                continue;
            }

            if (query.ZoneFilter is { } zone && edge.Zone != zone)
            {
                continue;
            }

            var sourceInWindow = selected.Contains(edge.SourceUrlId);
            var targetInWindow = edge.TargetUrlId is { } t && selected.Contains(t);
            if ((edge.IsInternal && sourceInWindow && targetInWindow) ||
                (!edge.IsInternal && query.IncludeExternalEdges && sourceInWindow))
            {
                windowEdges.Add(edge);
            }
        }

        // Materialize the window nodes in selection order.
        var nodesById = new Dictionary<long, LinkGraphNode>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id, canonical_url, host, depth, discovery_source FROM url_records " +
                "WHERE audit_id = @audit ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                if (!selected.Contains(id))
                {
                    continue;
                }

                nodesById[id] = BuildNode(reader, id, inDegree, outDegree, weighted, unweighted);
            }
        }

        var orderedNodes = candidates.Take(query.NodeLimit)
            .Where(c => nodesById.ContainsKey(c.UrlId))
            .Select(c => nodesById[c.UrlId])
            .ToList();

        return new LinkGraphSnapshot(aggregate, orderedNodes, windowEdges, aggregate.UrlCount, aggregate.InternalEdgeCount + aggregate.ExternalEdgeCount);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LinkGraphNode> StreamNodesAsync(long auditId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (weighted, unweighted) = await LoadEquityMapsAsync(auditId, cancellationToken);
        var inDegree = new Dictionary<long, long>();
        var outDegree = new Dictionary<long, long>();

        await foreach (var edge in StreamEdgesCoreAsync(auditId, cancellationToken))
        {
            if (!edge.IsInternal)
            {
                continue;
            }

            outDegree[edge.SourceUrlId] = outDegree.GetValueOrDefault(edge.SourceUrlId) + 1;
            if (edge.TargetUrlId is { } target && target != edge.SourceUrlId)
            {
                inDegree[target] = inDegree.GetValueOrDefault(target) + 1;
            }
        }

        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, canonical_url, host, depth, discovery_source FROM url_records " +
            "WHERE audit_id = @audit ORDER BY id";
        AddParameter(command, "@audit", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return BuildNode(reader, reader.GetInt64(0), inDegree, outDegree, weighted, unweighted);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<LinkGraphEdge> StreamEdgesAsync(long auditId, CancellationToken cancellationToken = default) =>
        StreamEdgesCoreAsync(auditId, cancellationToken);

    private async IAsyncEnumerable<LinkGraphEdge> StreamEdgesCoreAsync(long auditId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, source_url_id, canonical_target_url, target_url_id, anchor_text, relation_flags, is_internal, dom_zone, zone_evidence, weighted_equity " +
            "FROM link_edges WHERE audit_id = @audit ORDER BY id";
        AddParameter(command, "@audit", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new LinkGraphEdge(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetString(2),
                reader.IsDBNull(7) ? null : (DomZone)reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                (LinkRelationFlags)reader.GetInt64(5),
                reader.GetInt64(6) != 0,
                reader.IsDBNull(9) ? null : reader.GetDouble(9));
        }
    }

    private async Task<(Dictionary<long, double> Weighted, Dictionary<long, double> Unweighted)> LoadEquityMapsAsync(long auditId, CancellationToken cancellationToken)
    {
        var weighted = new Dictionary<long, double>();
        var unweighted = new Dictionary<long, double>();
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT metric_key, dimensions_json, metric_value FROM metric_summaries " +
            "WHERE audit_id = @audit AND metric_key IN (@weighted, @unweighted)";
        AddParameter(command, "@audit", auditId);
        AddParameter(command, "@weighted", MetricKeys.LinkEquityWeighted);
        AddParameter(command, "@unweighted", LinkGraphMetricKeys.LinkEquityUnweighted);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var dimensions = reader.GetString(1);
            var value = reader.GetDouble(2);
            using var document = JsonDocument.Parse(dimensions);
            if (!document.RootElement.TryGetProperty("urlId", out var urlIdElement))
            {
                continue;
            }

            var map = key == MetricKeys.LinkEquityWeighted ? weighted : unweighted;
            map[urlIdElement.GetInt64()] = value;
        }

        return (weighted, unweighted);
    }

    private static LinkGraphNode BuildNode(
        DbDataReader reader,
        long id,
        Dictionary<long, long> inDegree,
        Dictionary<long, long> outDegree,
        Dictionary<long, double> weighted,
        Dictionary<long, double> unweighted)
    {
        var discoverySource = (DiscoverySource)reader.GetInt64(4);
        var inLinks = inDegree.GetValueOrDefault(id);
        return new LinkGraphNode(
            id,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            weighted.GetValueOrDefault(id),
            unweighted.GetValueOrDefault(id),
            inLinks,
            outDegree.GetValueOrDefault(id),
            inLinks == 0,
            discoverySource is DiscoverySource.Seed or DiscoverySource.List);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record WindowCandidate(long UrlId, double Equity);
}
