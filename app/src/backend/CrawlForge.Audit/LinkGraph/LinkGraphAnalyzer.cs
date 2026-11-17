using System.Data.Common;
using System.Runtime.CompilerServices;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.LinkGraph;

/// <summary>
/// Streaming link-graph analyzer (schema catalog: link_edges "supports orphan
/// and graph analysis"). Two bounded passes over the persisted graph — edges
/// then URLs — with compact identifier-keyed state only (no row materialization),
/// satisfying the 500k+ URL scale invariant. Orphan definition: a URL with no
/// internal in-links from another URL (self-loops excluded); seeds are marked so
/// consumers can separate entry points from true orphans. Connected components
/// use iterative union-find (stack-safe at scale).
/// </summary>
public sealed class LinkGraphAnalyzer : ILinkGraphAnalyzer
{
    private readonly IDbProvider _provider;

    /// <summary>Creates the analyzer over the configured provider.</summary>
    /// <param name="provider">The configured database provider.</param>
    public LinkGraphAnalyzer(IDbProvider provider)
    {
        _provider = provider;
    }

    /// <inheritdoc />
    public async Task<LinkGraphAggregate> ComputeAggregateAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var inDegree = new Dictionary<long, long>();
        var outDegree = new Dictionary<long, long>();
        var zoneDistribution = new Dictionary<DomZone, long>();
        var components = new UnionFind();
        long internalEdges = 0;
        long externalEdges = 0;

        // Pass 1: stream edges — degrees, zone distribution, component unions.
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT source_url_id, target_url_id, is_internal, dom_zone FROM link_edges " +
                "WHERE audit_id = @audit ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var source = reader.GetInt64(0);
                var target = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                var isInternal = reader.GetInt64(2) != 0;
                DomZone? zone = reader.IsDBNull(3) ? null : (DomZone)reader.GetInt64(3);

                if (zone is { } z)
                {
                    zoneDistribution[z] = zoneDistribution.GetValueOrDefault(z) + 1;
                }

                if (!isInternal)
                {
                    externalEdges++;
                    continue;
                }

                internalEdges++;
                outDegree[source] = outDegree.GetValueOrDefault(source) + 1;
                components.Add(source);
                if (target is { } t && t != source)
                {
                    inDegree[t] = inDegree.GetValueOrDefault(t) + 1;
                    components.Union(source, t);
                }
            }
        }

        // Pass 2: stream URLs — counts, orphans, isolated components.
        long urlCount = 0;
        long seedCount = 0;
        long orphanCount = 0;
        long maxInDegree = 0;
        long inDegreeSum = 0;
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id, discovery_source FROM url_records WHERE audit_id = @audit ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                var source = (DiscoverySource)reader.GetInt64(1);
                urlCount++;
                if (source is DiscoverySource.Seed or DiscoverySource.List)
                {
                    seedCount++;
                }

                var degree = inDegree.GetValueOrDefault(id);
                if (degree == 0)
                {
                    orphanCount++;
                }

                inDegreeSum += degree;
                maxInDegree = Math.Max(maxInDegree, degree);
                components.Add(id);
            }
        }

        return new LinkGraphAggregate(
            urlCount,
            internalEdges,
            externalEdges,
            orphanCount,
            urlCount == 0 ? 0 : (double)orphanCount / urlCount,
            seedCount,
            urlCount == 0 ? 0 : (double)inDegreeSum / urlCount,
            maxInDegree,
            components.CountRoots(),
            zoneDistribution);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<OrphanUrlRecord> StreamOrphansAsync(
        long auditId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Pass 1: compact set of internally-linked targets (self-loops excluded).
        var linkedTargets = new HashSet<long>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT source_url_id, target_url_id FROM link_edges " +
                "WHERE audit_id = @audit AND is_internal = 1 AND target_url_id IS NOT NULL ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var source = reader.GetInt64(0);
                var target = reader.GetInt64(1);
                if (target != source)
                {
                    linkedTargets.Add(target);
                }
            }
        }

        // Pass 2: stream URLs, yielding those without internal in-links.
        await using var urlConnection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var urlCommand = urlConnection.CreateCommand();
        urlCommand.CommandText =
            "SELECT id, canonical_url, host, depth, discovery_source FROM url_records " +
            "WHERE audit_id = @audit ORDER BY id";
        AddParameter(urlCommand, "@audit", auditId);
        await using var urlReader = await urlCommand.ExecuteReaderAsync(cancellationToken);
        while (await urlReader.ReadAsync(cancellationToken))
        {
            var id = urlReader.GetInt64(0);
            if (linkedTargets.Contains(id))
            {
                continue;
            }

            var discoverySource = (DiscoverySource)urlReader.GetInt64(4);
            yield return new OrphanUrlRecord(
                id,
                urlReader.GetString(1),
                urlReader.GetString(2),
                urlReader.GetInt32(3),
                discoverySource,
                discoverySource is DiscoverySource.Seed or DiscoverySource.List);
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Iterative union-find over URL identifiers: path compression without
    /// recursion (stack-safe at 500k+ nodes), union by size. Memory is compact
    /// identifier-keyed dictionaries only.
    /// </summary>
    private sealed class UnionFind
    {
        private readonly Dictionary<long, long> _parent = [];
        private readonly Dictionary<long, int> _size = [];

        public void Add(long node)
        {
            if (!_parent.ContainsKey(node))
            {
                _parent[node] = node;
                _size[node] = 1;
            }
        }

        public void Union(long a, long b)
        {
            // Endpoints may appear first as targets; ensure both exist.
            Add(a);
            Add(b);
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA == rootB)
            {
                return;
            }

            if (_size[rootA] < _size[rootB])
            {
                (rootA, rootB) = (rootB, rootA);
            }

            _parent[rootB] = rootA;
            _size[rootA] += _size[rootB];
        }

        public long CountRoots()
        {
            long roots = 0;
            foreach (var (node, parent) in _parent)
            {
                if (node == parent)
                {
                    roots++;
                }
            }

            return roots;
        }

        private long Find(long node)
        {
            var root = node;
            while (_parent[root] != root)
            {
                root = _parent[root];
            }

            // Iterative path compression.
            var current = node;
            while (_parent[current] != root)
            {
                var next = _parent[current];
                _parent[current] = root;
                current = next;
            }

            return root;
        }
    }
}
