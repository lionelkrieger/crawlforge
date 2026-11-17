using System.Data.Common;
using System.Text.Json;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.NGrams;

/// <summary>
/// N-gram read model (FS-CRAWL-002 §5.7–§5.8) backing the phase-05 REST/MCP
/// surfaces: GET /api/audits/{id}/ngrams (site summaries or per-URL rows),
/// /ngrams/cannibalization and /ngrams/gaps (persisted cluster/gap findings),
/// and the MCP <c>ngram-analysis</c> resource (bounded read-only projection).
/// Explorer reads are constant-cost indexed lookups over the materialized
/// <c>ngram_site_summaries</c> table on both providers.
/// </summary>
public sealed class NGramReadModel
{
    private readonly IDbProvider _provider;

    /// <summary>Creates the read model.</summary>
    /// <param name="provider">Database provider.</param>
    public NGramReadModel(IDbProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <summary>
    /// N-gram explorer list (§5.7): site-summary rows (scope=site) or per-URL
    /// frequency rows (scope=url), with zone/order/term/minDf filters, sorting
    /// (df, tf, count), and pagination.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="query">List query shape.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The paged response.</returns>
    public async Task<NgramListResponse> ListAsync(long auditId, NgramListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, 1000);

        using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(query.Scope, "url", StringComparison.OrdinalIgnoreCase))
        {
            return await ListUrlRowsAsync(connection, auditId, query, offset, limit, cancellationToken).ConfigureAwait(false);
        }

        return await ListSiteRowsAsync(connection, auditId, query, offset, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cannibalization clusters (§5.7) read from persisted findings, with
    /// optional order/zone/minimum-size filters and pagination.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="zone">Optional zone filter.</param>
    /// <param name="order">Optional order filter.</param>
    /// <param name="minClusterSize">Minimum member count (default 2).</param>
    /// <param name="offset">Zero-based page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching clusters.</returns>
    public async Task<IReadOnlyList<NgramCannibalizationCluster>> ListClustersAsync(
        long auditId,
        TextZone? zone = null,
        int? order = null,
        int minClusterSize = 2,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var clusters = new List<NgramCannibalizationCluster>();
        foreach (var evidence in await ReadFindingEvidenceAsync(auditId, FindingRuleKeys.ContentKeywordCannibalization, cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(evidence);
            var root = document.RootElement;
            var members = root.GetProperty("members").EnumerateArray()
                .Select(m => new NgramClusterMember(
                    m.GetProperty("urlId").GetInt64(),
                    m.GetProperty("url").GetString() ?? string.Empty,
                    m.GetProperty("tf").GetDouble(),
                    m.TryGetProperty("canonicalTarget", out var target) && target.ValueKind != JsonValueKind.Null ? target.GetString() : null))
                .ToList();
            var clusterZone = ParseZone(root.GetProperty("zone").GetString() ?? "title");
            var clusterOrder = root.GetProperty("order").GetInt32();
            if (members.Count < minClusterSize || (zone is not null && clusterZone != zone) || (order is not null && clusterOrder != order))
            {
                continue;
            }

            clusters.Add(new NgramCannibalizationCluster(
                clusterZone,
                clusterOrder,
                root.GetProperty("term").GetString() ?? string.Empty,
                members,
                root.GetProperty("siteDf").GetInt64(),
                root.GetProperty("sameCanonicalTarget").GetBoolean()));
        }

        return clusters.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 1000)).ToList();
    }

    /// <summary>
    /// Topical gaps (§5.7) read from persisted findings, with a minimum anchor
    /// DF override and pagination.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="query">Gaps query shape.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching gaps.</returns>
    public async Task<IReadOnlyList<NgramTopicalGap>> ListGapsAsync(long auditId, NgramGapsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var gaps = new List<NgramTopicalGap>();
        foreach (var evidence in await ReadFindingEvidenceAsync(auditId, FindingRuleKeys.ContentTopicalGap, cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(evidence);
            var root = document.RootElement;
            var anchorDf = root.GetProperty("anchorDf").GetInt64();
            if (query.MinAnchorDf is not null && anchorDf < query.MinAnchorDf)
            {
                continue;
            }

            var samples = root.TryGetProperty("sampleSourceUrls", out var samplesElement)
                ? samplesElement.EnumerateArray().Select(s => s.GetString() ?? string.Empty).ToList()
                : [];
            gaps.Add(new NgramTopicalGap(
                root.GetProperty("term").GetString() ?? string.Empty,
                root.GetProperty("order").GetInt32(),
                anchorDf,
                root.GetProperty("threshold").GetInt32(),
                samples));
        }

        return gaps.Skip(Math.Max(0, query.Offset)).Take(Math.Clamp(query.Limit, 1, 1000)).ToList();
    }

    /// <summary>
    /// MCP <c>ngram-analysis</c> resource (§5.8): a bounded, read-only
    /// projection — top site terms, optional per-URL rows, and top clusters.
    /// Bound counters live in audit diagnostics; the resource reports zeros.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="zone">Optional zone filter.</param>
    /// <param name="order">Optional order filter.</param>
    /// <param name="urlId">Optional URL filter for per-URL rows.</param>
    /// <param name="minDf">Optional minimum DF for top terms.</param>
    /// <param name="limit">Maximum rows per section (default 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resource payload.</returns>
    public async Task<NgramAnalysisResource> GetAnalysisResourceAsync(
        long auditId,
        TextZone? zone = null,
        int? order = null,
        long? urlId = null,
        long? minDf = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 1000);
        var topTerms = await ListAsync(auditId, new NgramListQuery(Zone: zone, Order: order, MinDf: minDf, Limit: boundedLimit), cancellationToken).ConfigureAwait(false);
        var urlRows = urlId is null
            ? []
            : (await ListAsync(auditId, new NgramListQuery(Scope: "url", UrlId: urlId, Zone: zone, Order: order, Limit: boundedLimit), cancellationToken).ConfigureAwait(false)).UrlItems;
        var clusters = await ListClustersAsync(auditId, zone, order, limit: Math.Min(boundedLimit, 20), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new NgramAnalysisResource(auditId, topTerms.SiteItems, urlRows, clusters, new NGramBoundsCounters());
    }

    private static async Task<NgramListResponse> ListSiteRowsAsync(
        DbConnection connection, long auditId, NgramListQuery query, int offset, int limit, CancellationToken cancellationToken)
    {
        var where = "WHERE `audit_id` = @auditId";
        if (query.Zone is not null)
        {
            where += " AND `zone` = @zone";
        }

        if (query.Order is not null)
        {
            where += " AND `ngram_order` = @order";
        }

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            where += " AND `term` LIKE @term";
        }

        if (query.MinDf is not null)
        {
            where += " AND `document_frequency` >= @minDf";
        }

        var sortColumn = query.SortBy.ToLowerInvariant() switch
        {
            "tf" => "mean_term_frequency",
            "count" => "total_frequency",
            _ => "document_frequency",
        };
        var direction = query.Descending ? "DESC" : "ASC";

        long total;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM `ngram_site_summaries` {where}";
            BindFilters(countCommand, auditId, query);
            total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        var items = new List<NgramSiteSummaryDto>();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT `id`, `zone`, `ngram_order`, `term`, `document_frequency`, `total_frequency`, `mean_term_frequency`, `primary_term_count` " +
            $"FROM `ngram_site_summaries` {where} ORDER BY `{sortColumn}` {direction}, `term` ASC LIMIT @limit OFFSET @offset";
        BindFilters(command, auditId, query);
        AddParameter(command, "@limit", limit);
        AddParameter(command, "@offset", offset);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new NgramSiteSummaryDto(
                reader.GetInt64(0), auditId, (TextZone)reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetDouble(6), reader.GetInt64(7)));
        }

        return new NgramListResponse(items, [], total, offset, limit);
    }

    private static async Task<NgramListResponse> ListUrlRowsAsync(
        DbConnection connection, long auditId, NgramListQuery query, int offset, int limit, CancellationToken cancellationToken)
    {
        var where = "WHERE `audit_id` = @auditId";
        if (query.UrlId is not null)
        {
            where += " AND `url_id` = @urlId";
        }

        if (query.Zone is not null)
        {
            where += " AND `zone` = @zone";
        }

        if (query.Order is not null)
        {
            where += " AND `ngram_order` = @order";
        }

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            where += " AND `term` LIKE @term";
        }

        var sortColumn = query.SortBy.ToLowerInvariant() switch
        {
            "tf" => "term_frequency",
            _ => "term_count",
        };
        var direction = query.Descending ? "DESC" : "ASC";

        long total;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM `ngram_frequencies` {where}";
            BindFilters(countCommand, auditId, query);
            total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        var items = new List<NgramFrequencyDto>();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT `id`, `url_id`, `zone`, `ngram_order`, `term`, `term_count`, `term_frequency` " +
            $"FROM `ngram_frequencies` {where} ORDER BY `{sortColumn}` {direction}, `term` ASC LIMIT @limit OFFSET @offset";
        BindFilters(command, auditId, query);
        AddParameter(command, "@limit", limit);
        AddParameter(command, "@offset", offset);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new NgramFrequencyDto(
                reader.GetInt64(0), auditId, reader.GetInt64(1), (TextZone)reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4), reader.GetInt64(5), reader.GetDouble(6)));
        }

        return new NgramListResponse([], items, total, offset, limit);
    }

    private async Task<List<string>> ReadFindingEvidenceAsync(long auditId, string ruleKey, CancellationToken cancellationToken)
    {
        var evidence = new List<string>();
        using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `evidence_json` FROM `findings` WHERE `audit_id` = @auditId AND `rule_key` = @ruleKey ORDER BY `id`";
        AddParameter(command, "@auditId", auditId);
        AddParameter(command, "@ruleKey", ruleKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                evidence.Add(reader.GetString(0));
            }
        }

        return evidence;
    }

    private static void BindFilters(DbCommand command, long auditId, NgramListQuery query)
    {
        AddParameter(command, "@auditId", auditId);
        if (query.Zone is not null)
        {
            AddParameter(command, "@zone", (int)query.Zone);
        }

        if (query.Order is not null)
        {
            AddParameter(command, "@order", query.Order);
        }

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            AddParameter(command, "@term", "%" + query.Query + "%");
        }

        if (query.MinDf is not null)
        {
            AddParameter(command, "@minDf", query.MinDf);
        }

        if (query.UrlId is not null)
        {
            AddParameter(command, "@urlId", query.UrlId);
        }
    }

    private static TextZone ParseZone(string zone) => zone switch
    {
        "title" => TextZone.Title,
        "headings" => TextZone.Headings,
        "body" => TextZone.Body,
        "inbound-anchor" => TextZone.AnchorText,
        _ => TextZone.Body,
    };

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
