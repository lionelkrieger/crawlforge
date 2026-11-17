using System.Data.Common;
using CrawlForge.Audit.LinkGraph;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Host.Execution;

/// <summary>
/// D15 live wiring (criterion #48 zone/equity facet): completes the link graph
/// after the crawl phase. Discovery records edges with anchor text and DOM-zone
/// classification, but target URL records may not exist yet at discovery time;
/// this pass (1) resolves <c>link_edges.target_url_id</c> against the persisted
/// URL dataset, (2) runs the weighted PageRank equity computation and persists
/// its metric rows, and (3) stamps each participating internal edge with its
/// weighted equity contribution (source equity × zone weight ÷ source
/// out-weight sum). All passes stream rows with compact identifier-keyed state
/// and batch writes transactionally — bounded memory at 500k+ URLs, the same
/// pattern as the accepted LinkGraph services.
/// </summary>
public sealed class LinkGraphEnrichmentPass
{
    private const int BatchSize = 500;

    private readonly IDbProvider _provider;
    private readonly WeightedLinkEquityCalculator _equity;

    /// <summary>Creates the pass over the provider and equity calculator.</summary>
    /// <param name="provider">The configured database provider.</param>
    /// <param name="equity">The weighted link-equity calculator.</param>
    public LinkGraphEnrichmentPass(IDbProvider provider, WeightedLinkEquityCalculator equity)
    {
        _provider = provider;
        _equity = equity;
    }

    /// <summary>Resolves edge targets, computes equity, and stamps edge contributions.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the graph is enriched.</returns>
    public async Task RunAsync(long auditId, CancellationToken cancellationToken = default)
    {
        await ResolveTargetsAsync(auditId, cancellationToken).ConfigureAwait(false);

        var result = await _equity.ComputeFullAsync(
            auditId, WeightedLinkEquityCalculator.DefaultZoneWeights, cancellationToken).ConfigureAwait(false);
        await _equity.PersistAsync(auditId, result, cancellationToken).ConfigureAwait(false);

        await StampEdgeEquityAsync(auditId, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves null target references on internal edges by canonical-URL match
    /// against the audit's persisted URL records (streamed, batched updates).
    /// </summary>
    private async Task ResolveTargetsAsync(long auditId, CancellationToken cancellationToken)
    {
        var targetByCanonical = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, canonical_url FROM url_records WHERE audit_id = @audit ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                targetByCanonical[reader.GetString(1)] = reader.GetInt64(0);
            }
        }

        var pending = new List<(long EdgeId, long TargetId)>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id, canonical_target_url FROM link_edges " +
                "WHERE audit_id = @audit AND is_internal = 1 AND target_url_id IS NULL ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (targetByCanonical.TryGetValue(reader.GetString(1), out var targetId))
                {
                    pending.Add((reader.GetInt64(0), targetId));
                }
            }
        }

        for (var offset = 0; offset < pending.Count; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = pending.Skip(offset).Take(BatchSize);
            await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (edgeId, targetId) in batch)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE link_edges SET target_url_id = @target WHERE id = @id";
                AddParameter(command, "@target", targetId);
                AddParameter(command, "@id", edgeId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stamps each internal, resolved, non-self-loop edge with the weighted
    /// equity it carries: source equity × zone weight ÷ source out-weight sum.
    /// Two streaming passes keep state compact (per-URL sums, then per-edge
    /// writes in transactional batches).
    /// </summary>
    private async Task StampEdgeEquityAsync(
        long auditId, LinkEquityResult result, CancellationToken cancellationToken)
    {
        var equityByUrl = result.Weighted.ToDictionary(score => score.UrlId, score => score.Equity);
        var outWeightSum = new Dictionary<long, double>();

        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT source_url_id, target_url_id, dom_zone FROM link_edges " +
                "WHERE audit_id = @audit AND is_internal = 1 AND target_url_id IS NOT NULL ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var source = reader.GetInt64(0);
                var target = reader.GetInt64(1);
                if (source == target)
                {
                    continue;
                }

                var zone = reader.IsDBNull(2) ? DomZone.MainContent : (DomZone)reader.GetInt64(2);
                var weight = WeightedLinkEquityCalculator.DefaultZoneWeights[zone];
                outWeightSum[source] = outWeightSum.GetValueOrDefault(source) + weight;
            }
        }

        var pending = new List<(long EdgeId, double Equity)>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id, source_url_id, target_url_id, dom_zone FROM link_edges " +
                "WHERE audit_id = @audit AND is_internal = 1 AND target_url_id IS NOT NULL ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var source = reader.GetInt64(1);
                var target = reader.GetInt64(2);
                if (source == target ||
                    !equityByUrl.TryGetValue(source, out var sourceEquity) ||
                    !outWeightSum.TryGetValue(source, out var sum) || sum <= 0)
                {
                    continue;
                }

                var zone = reader.IsDBNull(3) ? DomZone.MainContent : (DomZone)reader.GetInt64(3);
                var weight = WeightedLinkEquityCalculator.DefaultZoneWeights[zone];
                pending.Add((reader.GetInt64(0), sourceEquity * weight / sum));
            }
        }

        for (var offset = 0; offset < pending.Count; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = pending.Skip(offset).Take(BatchSize);
            await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (edgeId, equity) in batch)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE link_edges SET weighted_equity = @equity WHERE id = @id";
                AddParameter(command, "@equity", equity);
                AddParameter(command, "@id", edgeId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
