using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Data.Repositories;

/// <summary>
/// Aggregation-into-DB service (FS-CRAWL-001 §8.1): computes summary metrics into
/// the precomputed <c>metric_summaries</c> read model and persists per-category and
/// overall audit scores, so dashboard reads are constant-cost lookups keyed by
/// (audit, metric, dimensions) rather than full scans over raw crawl data.
/// </summary>
public interface IAuditAggregationService
{
    /// <summary>
    /// Recomputes all aggregates and scores for one audit inside a single transaction.
    /// Existing aggregate rows for the audit are replaced atomically.
    /// </summary>
    /// <param name="auditId">The audit to aggregate.</param>
    /// <param name="disabledCategories">Categories disabled by local toggles; they contribute no score. Null means all enabled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Write counters for the recomputation.</returns>
    Task<AggregationResult> RecomputeAsync(long auditId, IReadOnlySet<AuditCategory>? disabledCategories = null, CancellationToken cancellationToken = default);
}

/// <summary>Counters returned by an aggregation recomputation.</summary>
/// <param name="MetricRowsWritten">Number of metric_summaries rows written.</param>
/// <param name="ScoreRowsWritten">Number of audit_scores rows written.</param>
public sealed record AggregationResult(int MetricRowsWritten, int ScoreRowsWritten);

/// <summary>
/// Provider-agnostic implementation of <see cref="IAuditAggregationService"/>.
/// Counting and distribution work is pushed into indexed GROUP BY queries; the few
/// payload scans (timing JSON, word counts) and the link-equity concentration pass
/// stream rows one at a time with bounded accumulators (precedence rule 7).
///
/// Baseline metric keys: url-count, finding-count, crawl-health, distribution,
/// link-graph, link-equity. Dimension payloads identify the breakdown. Additive
/// metric keys live in <see cref="Contracts.Constants.MetricKeys"/>.
///
/// Scoring model (deterministic baseline; the phase-03 scoring engine owns the
/// final formula): penalty = (3·errors + warnings) per indexable URL;
/// score = 100 · max(0, 1 − penalty/3), clamped to [0,100]. Suppressed findings
/// are excluded; disabled categories contribute no score; the overall score uses
/// all non-suppressed findings across enabled categories.
/// </summary>
public sealed class AuditAggregationService : IAuditAggregationService
{
    private const string MetricUrlCount = "url-count";
    private const string MetricFindingCount = "finding-count";
    private const string MetricCrawlHealth = "crawl-health";
    private const string MetricDistribution = "distribution";
    private const string MetricLinkGraph = "link-graph";
    private const string MetricLinkEquity = "link-equity";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IDbProvider _provider;
    private readonly SqlRepository<MetricSummaryDto> _metrics;
    private readonly SqlRepository<AuditScoreDto> _scores;

    /// <summary>Creates the aggregation service over the configured provider.</summary>
    /// <param name="provider">The configured database provider.</param>
    public AuditAggregationService(IDbProvider provider)
    {
        _provider = provider;
        _metrics = new SqlRepository<MetricSummaryDto>(provider);
        _scores = new SqlRepository<AuditScoreDto>(provider);
    }

    /// <inheritdoc />
    public async Task<AggregationResult> RecomputeAsync(long auditId, IReadOnlySet<AuditCategory>? disabledCategories = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await DeleteExistingAsync(connection, transaction, "metric_summaries", auditId, cancellationToken);
        await DeleteExistingAsync(connection, transaction, "audit_scores", auditId, cancellationToken);

        var metricRows = 0;
        var scoreRows = 0;

        async Task WriteMetricAsync(string key, string dimensionsJson, double value)
        {
            await _metrics.CreateAsync(new MetricSummaryDto(0, auditId, key, dimensionsJson, value), connection, transaction, cancellationToken);
            metricRows++;
        }

        // --- URL counts by status class, content type, depth, indexability ---
        var statusCounts = new Dictionary<long, long>();
        await foreach (var (group, count) in GroupCountAsync(connection, transaction,
            "SELECT status_class, COUNT(*) FROM url_records WHERE audit_id = @audit GROUP BY status_class", auditId, cancellationToken))
        {
            statusCounts[group] = count;
            await WriteMetricAsync(MetricUrlCount, Dim(("dimension", "status-class"), ("value", ((CrawlStatusClass)group).ToString())), count);
        }

        await foreach (var (group, count) in GroupCountTextAsync(connection, transaction,
            "SELECT COALESCE(content_type, 'unknown'), COUNT(*) FROM url_records WHERE audit_id = @audit GROUP BY COALESCE(content_type, 'unknown')", auditId, cancellationToken))
        {
            await WriteMetricAsync(MetricUrlCount, Dim(("dimension", "content-type"), ("value", group)), count);
        }

        await foreach (var (group, count) in GroupCountAsync(connection, transaction,
            "SELECT depth, COUNT(*) FROM url_records WHERE audit_id = @audit GROUP BY depth", auditId, cancellationToken))
        {
            await WriteMetricAsync(MetricUrlCount, Dim(("dimension", "depth"), ("value", group.ToString(CultureInfo.InvariantCulture))), count);
        }

        long indexableCount = 0;
        await foreach (var (group, count) in GroupCountAsync(connection, transaction,
            "SELECT is_indexable, COUNT(*) FROM url_records WHERE audit_id = @audit GROUP BY is_indexable", auditId, cancellationToken))
        {
            if (group == 1)
            {
                indexableCount = count;
            }

            await WriteMetricAsync(MetricUrlCount, Dim(("dimension", "indexability"), ("value", group == 1 ? "indexable" : "non-indexable")), count);
        }

        // --- Finding counts by severity and category (suppressed excluded) ---
        var severityCounts = new Dictionary<FindingSeverity, long>();
        var categorySeverity = new Dictionary<(AuditCategory Category, FindingSeverity Severity), long>();
        await foreach (var row in QueryRowsAsync(connection, transaction,
            "SELECT category, severity, COUNT(*) FROM findings WHERE audit_id = @audit AND is_suppressed = 0 GROUP BY category, severity", auditId, cancellationToken))
        {
            var category = (AuditCategory)row.GetInt64(0);
            var severity = (FindingSeverity)row.GetInt64(1);
            var count = row.GetInt64(2);
            severityCounts[severity] = severityCounts.GetValueOrDefault(severity) + count;
            categorySeverity[(category, severity)] = count;
            await WriteMetricAsync(MetricFindingCount, Dim(("dimension", "severity-category"), ("category", category.ToString()), ("severity", severity.ToString())), count);
        }

        foreach (var (severity, count) in severityCounts)
        {
            await WriteMetricAsync(MetricFindingCount, Dim(("dimension", "severity"), ("value", severity.ToString())), count);
        }

        foreach (var category in Enum.GetValues<AuditCategory>())
        {
            var count = categorySeverity.Where(kv => kv.Key.Category == category).Sum(kv => kv.Value);
            await WriteMetricAsync(MetricFindingCount, Dim(("dimension", "category"), ("value", category.ToString())), count);
        }

        // --- Crawl health: errors, redirects, blocked, skipped ---
        long SumStatus(params CrawlStatusClass[] classes) => classes.Sum(c => statusCounts.GetValueOrDefault((long)c));
        await WriteMetricAsync(MetricCrawlHealth, Dim(("measure", "errors")), SumStatus(CrawlStatusClass.ClientError, CrawlStatusClass.ServerError, CrawlStatusClass.FetchError));
        await WriteMetricAsync(MetricCrawlHealth, Dim(("measure", "redirects")), SumStatus(CrawlStatusClass.Redirect));
        await WriteMetricAsync(MetricCrawlHealth, Dim(("measure", "blocked")), SumStatus(CrawlStatusClass.Blocked));
        await WriteMetricAsync(MetricCrawlHealth, Dim(("measure", "skipped")), SumStatus(CrawlStatusClass.NotCrawled));

        // --- Distributions: page weight (SQL), response time and word count (streaming) ---
        await WritePageWeightDistributionAsync(connection, transaction, auditId, WriteMetricAsync, cancellationToken);
        await WriteResponseTimeDistributionAsync(connection, transaction, auditId, WriteMetricAsync, cancellationToken);
        await WriteWordCountDistributionAsync(connection, transaction, auditId, WriteMetricAsync, cancellationToken);

        // --- Link-graph statistics ---
        var totalInternalEdges = await ScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM link_edges WHERE audit_id = @audit AND is_internal = 1", auditId, cancellationToken);
        await WriteMetricAsync(MetricLinkGraph, Dim(("measure", "internal-edges")), totalInternalEdges);

        var orphanCount = await ScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM url_records u WHERE u.audit_id = @audit AND u.discovery_source <> 0 " +
            "AND NOT EXISTS (SELECT 1 FROM link_edges e WHERE e.audit_id = @audit AND e.is_internal = 1 AND e.target_url_id = u.id)",
            auditId, cancellationToken);
        await WriteMetricAsync(MetricLinkGraph, Dim(("measure", "orphan-pages")), orphanCount);

        var maxOutDegree = await ScalarAsync(connection, transaction,
            "SELECT COALESCE(MAX(c), 0) FROM (SELECT COUNT(*) AS c FROM link_edges WHERE audit_id = @audit AND is_internal = 1 GROUP BY source_url_id)",
            auditId, cancellationToken);
        await WriteMetricAsync(MetricLinkGraph, Dim(("measure", "max-out-degree")), maxOutDegree);

        var hubCount = await ScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM (SELECT source_url_id, COUNT(*) AS c FROM link_edges WHERE audit_id = @audit AND is_internal = 1 GROUP BY source_url_id HAVING COUNT(*) >= 10)",
            auditId, cancellationToken);
        await WriteMetricAsync(MetricLinkGraph, Dim(("measure", "hub-pages")), hubCount);

        var reachableCount = await ScalarAsync(connection, transaction,
            "WITH RECURSIVE reachable(id) AS (" +
            "SELECT id FROM url_records WHERE audit_id = @audit AND discovery_source = 0 " +
            "UNION " +
            "SELECT e.target_url_id FROM link_edges e JOIN reachable r ON e.source_url_id = r.id " +
            "WHERE e.audit_id = @audit AND e.is_internal = 1 AND e.target_url_id IS NOT NULL) " +
            "SELECT COUNT(*) FROM reachable",
            auditId, cancellationToken);
        await WriteMetricAsync(MetricLinkGraph, Dim(("measure", "reachable-pages")), reachableCount);

        var totalUrls = await ScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM url_records WHERE audit_id = @audit", auditId, cancellationToken);
        await WriteMetricAsync(MetricLinkGraph, Dim(("measure", "reachability-rate")), totalUrls == 0 ? 0 : (double)reachableCount / totalUrls);

        // --- Link-equity distribution: inlink counts, percentile concentration ---
        var inlinkCounts = new List<long>();
        long totalInlinks = 0;
        await foreach (var row in QueryRowsAsync(connection, transaction,
            "SELECT target_url_id, COUNT(*) FROM link_edges WHERE audit_id = @audit AND is_internal = 1 AND target_url_id IS NOT NULL GROUP BY target_url_id",
            auditId, cancellationToken))
        {
            var count = row.GetInt64(1);
            inlinkCounts.Add(count);
            totalInlinks += count;
        }

        inlinkCounts.Sort();
        inlinkCounts.Reverse();
        var topDecileCount = Math.Max(1, inlinkCounts.Count / 10);
        long topDecileInlinks = 0;
        for (var i = 0; i < topDecileCount && i < inlinkCounts.Count; i++)
        {
            topDecileInlinks += inlinkCounts[i];
        }

        await WriteMetricAsync(MetricLinkEquity, Dim(("measure", "inlink-total")), totalInlinks);
        await WriteMetricAsync(MetricLinkEquity, Dim(("measure", "max-inlinks")), inlinkCounts.Count == 0 ? 0 : inlinkCounts[0]);
        await WriteMetricAsync(MetricLinkEquity, Dim(("measure", "top-decile-share")), totalInlinks == 0 ? 0 : (double)topDecileInlinks / totalInlinks);

        var weightedEquity = await ScalarAsync(connection, transaction,
            "SELECT COALESCE(SUM(weighted_equity), 0) FROM link_edges WHERE audit_id = @audit", auditId, cancellationToken);
        await WriteMetricAsync(Contracts.Constants.MetricKeys.LinkEquityWeighted, Dim(("measure", "total")), weightedEquity);

        // --- Audit scores: per enabled category + overall ---
        var coverage = Math.Max(1, indexableCount);
        foreach (var category in Enum.GetValues<AuditCategory>())
        {
            if (disabledCategories is not null && disabledCategories.Contains(category))
            {
                continue;
            }

            var errors = categorySeverity.GetValueOrDefault((category, FindingSeverity.Error));
            var warnings = categorySeverity.GetValueOrDefault((category, FindingSeverity.Warning));
            var notices = categorySeverity.GetValueOrDefault((category, FindingSeverity.Notice));
            var passes = categorySeverity.GetValueOrDefault((category, FindingSeverity.Pass));
            var score = ComputeScore(errors, warnings, coverage);
            await _scores.CreateAsync(new AuditScoreDto(0, auditId, category, score, (int)errors, (int)warnings, (int)notices, (int)passes), connection, transaction, cancellationToken);
            scoreRows++;
        }

        var totalErrors = severityCounts.GetValueOrDefault(FindingSeverity.Error);
        var totalWarnings = severityCounts.GetValueOrDefault(FindingSeverity.Warning);
        var totalNotices = severityCounts.GetValueOrDefault(FindingSeverity.Notice);
        var totalPasses = severityCounts.GetValueOrDefault(FindingSeverity.Pass);
        await _scores.CreateAsync(
            new AuditScoreDto(0, auditId, null, ComputeScore(totalErrors, totalWarnings, coverage), (int)totalErrors, (int)totalWarnings, (int)totalNotices, (int)totalPasses),
            connection, transaction, cancellationToken);
        scoreRows++;

        await transaction.CommitAsync(cancellationToken);
        return new AggregationResult(metricRows, scoreRows);
    }

    private static double ComputeScore(long errors, long warnings, long coverage)
    {
        var penalty = (3.0 * errors + warnings) / coverage;
        return Math.Clamp(100.0 * (1.0 - penalty / 3.0), 0.0, 100.0);
    }

    private static string Dim(params (string Key, string Value)[] pairs) =>
        JsonSerializer.Serialize(pairs.ToDictionary(p => p.Key, p => p.Value), JsonOptions);

    private static async Task DeleteExistingAsync(DbConnection connection, DbTransaction transaction, string table, long auditId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE audit_id = @audit";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@audit";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async IAsyncEnumerable<(long Group, long Count)> GroupCountAsync(
        DbConnection connection, DbTransaction transaction, string sql, long auditId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@audit";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return (reader.GetInt64(0), reader.GetInt64(1));
        }
    }

    private static async IAsyncEnumerable<(string Group, long Count)> GroupCountTextAsync(
        DbConnection connection, DbTransaction transaction, string sql, long auditId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@audit";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return (reader.GetString(0), reader.GetInt64(1));
        }
    }

    private static async IAsyncEnumerable<DbDataReader> QueryRowsAsync(
        DbConnection connection, DbTransaction transaction, string sql, long auditId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@audit";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return reader;
        }
    }

    private static async Task<long> ScalarAsync(DbConnection connection, DbTransaction transaction, string sql, long auditId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@audit";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task WritePageWeightDistributionAsync(
        DbConnection connection, DbTransaction transaction, long auditId,
        Func<string, string, double, Task> writeMetric, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COALESCE(AVG(size_bytes), 0), COALESCE(MAX(size_bytes), 0), COALESCE(MIN(size_bytes), 0), " +
            "SUM(CASE WHEN size_bytes < 10240 THEN 1 ELSE 0 END), " +
            "SUM(CASE WHEN size_bytes >= 10240 AND size_bytes < 102400 THEN 1 ELSE 0 END), " +
            "SUM(CASE WHEN size_bytes >= 102400 AND size_bytes < 1048576 THEN 1 ELSE 0 END), " +
            "SUM(CASE WHEN size_bytes >= 1048576 THEN 1 ELSE 0 END) " +
            "FROM url_records WHERE audit_id = @audit AND size_bytes IS NOT NULL";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@audit";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        await writeMetric(MetricDistribution, Dim(("measure", "page-weight"), ("statistic", "mean")), reader.GetDouble(0));
        await writeMetric(MetricDistribution, Dim(("measure", "page-weight"), ("statistic", "max")), reader.GetInt64(1));
        await writeMetric(MetricDistribution, Dim(("measure", "page-weight"), ("statistic", "min")), reader.GetInt64(2));
        string[] buckets = ["under-10kb", "10kb-100kb", "100kb-1mb", "over-1mb"];
        for (var i = 0; i < buckets.Length; i++)
        {
            await writeMetric(MetricDistribution, Dim(("measure", "page-weight"), ("statistic", "bucket"), ("bucket", buckets[i])), reader.GetInt64(3 + i));
        }
    }

    private static async Task WriteResponseTimeDistributionAsync(
        DbConnection connection, DbTransaction transaction, long auditId,
        Func<string, string, double, Task> writeMetric, CancellationToken cancellationToken)
    {
        // Timing payloads carry a numeric "totalMs" property when the fetcher
        // recorded one; the scan streams rows with bounded accumulators only.
        long count = 0;
        double sum = 0;
        double max = 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT timing_json FROM url_records WHERE audit_id = @audit AND timing_json IS NOT NULL";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@audit";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(reader.GetString(0));
            if (document.RootElement.TryGetProperty("totalMs", out var totalMs) && totalMs.TryGetDouble(out var value))
            {
                count++;
                sum += value;
                max = Math.Max(max, value);
            }
        }

        await writeMetric(MetricDistribution, Dim(("measure", "response-time"), ("statistic", "mean")), count == 0 ? 0 : sum / count);
        await writeMetric(MetricDistribution, Dim(("measure", "response-time"), ("statistic", "max")), max);
        await writeMetric(MetricDistribution, Dim(("measure", "response-time"), ("statistic", "samples")), count);
    }

    private static async Task WriteWordCountDistributionAsync(
        DbConnection connection, DbTransaction transaction, long auditId,
        Func<string, string, double, Task> writeMetric, CancellationToken cancellationToken)
    {
        // Word counts arrive as url_elements rows in the "word-count" group with a
        // numeric "count" payload property (produced by the content analyzer).
        long count = 0;
        double sum = 0;
        double max = 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT ue.payload_json FROM url_elements ue JOIN url_records u ON ue.url_id = u.id " +
            "WHERE u.audit_id = @audit AND ue.element_group = 'word-count'";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@audit";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(reader.GetString(0));
            if (document.RootElement.TryGetProperty("count", out var wordCount) && wordCount.TryGetDouble(out var value))
            {
                count++;
                sum += value;
                max = Math.Max(max, value);
            }
        }

        await writeMetric(MetricDistribution, Dim(("measure", "word-count"), ("statistic", "mean")), count == 0 ? 0 : sum / count);
        await writeMetric(MetricDistribution, Dim(("measure", "word-count"), ("statistic", "max")), max);
        await writeMetric(MetricDistribution, Dim(("measure", "word-count"), ("statistic", "samples")), count);
    }
}
