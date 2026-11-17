using System.Data.Common;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.Scoring;

/// <summary>
/// Cross-audit comparison read model (FS-CRAWL-001 §8.2): new/resolved
/// findings, metric deltas, and score deltas between two audits of the same
/// project. Fully offline — reads stored findings, URL records, and
/// precomputed aggregates only; no URL is fetched and no content artifact is
/// required. Finding identity across audits is (rule key, canonical URL)
/// because URL surrogate keys differ between audits; the canonical URL is
/// resolved through a <c>url_records</c> join. Suppressed findings are
/// excluded from the default comparison, consistent with §7.5 default counts
/// and scoring.
/// </summary>
public sealed class AuditComparisonReader
{
    private readonly IDbProvider _provider;

    /// <summary>Creates the reader over the configured database provider.</summary>
    /// <param name="provider">Database provider (SQLite or MySQL).</param>
    public AuditComparisonReader(IDbProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Compares a target audit against a base audit.
    /// </summary>
    /// <param name="baseAuditId">Earlier (base) audit reference.</param>
    /// <param name="targetAuditId">Later (target) audit reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The comparison read model.</returns>
    public async Task<AuditComparisonResult> CompareAsync(
        long baseAuditId,
        long targetAuditId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);

        var baseFindings = await ReadFindingsAsync(connection, baseAuditId, cancellationToken);
        var targetFindings = await ReadFindingsAsync(connection, targetAuditId, cancellationToken);

        var baseKeys = new HashSet<(string RuleKey, string Url)>(baseFindings.Select(Identity));
        var targetKeys = new HashSet<(string RuleKey, string Url)>(targetFindings.Select(Identity));

        var newFindings = targetFindings
            .Where(f => !baseKeys.Contains(Identity(f)))
            .GroupBy(Identity)
            .Select(g => g.First())
            .OrderBy(f => f.RuleKey, StringComparer.Ordinal)
            .ThenBy(f => f.CanonicalUrl, StringComparer.Ordinal)
            .ToArray();
        var resolvedFindings = baseFindings
            .Where(f => !targetKeys.Contains(Identity(f)))
            .GroupBy(Identity)
            .Select(g => g.First())
            .OrderBy(f => f.RuleKey, StringComparer.Ordinal)
            .ThenBy(f => f.CanonicalUrl, StringComparer.Ordinal)
            .ToArray();

        var metricDeltas = await ReadMetricDeltasAsync(connection, baseAuditId, targetAuditId, cancellationToken);
        var scoreDeltas = await ReadScoreDeltasAsync(connection, baseAuditId, targetAuditId, cancellationToken);

        return new AuditComparisonResult(
            baseAuditId, targetAuditId, newFindings, resolvedFindings, metricDeltas, scoreDeltas);
    }

    private static (string RuleKey, string Url) Identity(FindingComparisonEntry finding) =>
        (finding.RuleKey, finding.CanonicalUrl ?? string.Empty);

    private static async Task<IReadOnlyList<FindingComparisonEntry>> ReadFindingsAsync(
        DbConnection connection,
        long auditId,
        CancellationToken cancellationToken)
    {
        var findings = new List<FindingComparisonEntry>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT f.rule_key, f.category, f.severity, f.url_id, u.canonical_url " +
            "FROM findings f LEFT JOIN url_records u ON u.id = f.url_id " +
            "WHERE f.audit_id = @auditId AND f.is_suppressed = 0";
        AddParameter(command, "@auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            findings.Add(new FindingComparisonEntry(
                RuleKey: reader.GetString(0),
                Category: (AuditCategory)reader.GetInt32(1),
                Severity: (FindingSeverity)reader.GetInt32(2),
                CanonicalUrl: reader.IsDBNull(4) ? null : reader.GetString(4),
                UrlId: reader.IsDBNull(3) ? null : reader.GetInt64(3)));
        }

        return findings;
    }

    private static async Task<IReadOnlyList<MetricDelta>> ReadMetricDeltasAsync(
        DbConnection connection,
        long baseAuditId,
        long targetAuditId,
        CancellationToken cancellationToken)
    {
        var baseMetrics = await ReadMetricsAsync(connection, baseAuditId, cancellationToken);
        var targetMetrics = await ReadMetricsAsync(connection, targetAuditId, cancellationToken);

        var keys = baseMetrics.Keys.Union(targetMetrics.Keys)
            .OrderBy(k => k.MetricKey, StringComparer.Ordinal)
            .ThenBy(k => k.DimensionsJson, StringComparer.Ordinal);
        var deltas = new List<MetricDelta>();
        foreach (var key in keys)
        {
            baseMetrics.TryGetValue(key, out var baseValue);
            targetMetrics.TryGetValue(key, out var targetValue);
            deltas.Add(new MetricDelta(
                key.MetricKey,
                key.DimensionsJson,
                baseMetrics.ContainsKey(key) ? baseValue : null,
                targetMetrics.ContainsKey(key) ? targetValue : null,
                baseMetrics.ContainsKey(key) && targetMetrics.ContainsKey(key)
                    ? Math.Round(targetValue - baseValue, 6)
                    : null));
        }

        return deltas;
    }

    private static async Task<Dictionary<(string MetricKey, string DimensionsJson), double>> ReadMetricsAsync(
        DbConnection connection,
        long auditId,
        CancellationToken cancellationToken)
    {
        var metrics = new Dictionary<(string, string), double>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT metric_key, dimensions_json, metric_value FROM metric_summaries WHERE audit_id = @auditId";
        AddParameter(command, "@auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            metrics[(reader.GetString(0), reader.GetString(1))] = reader.GetDouble(2);
        }

        return metrics;
    }

    private static async Task<IReadOnlyList<ScoreDelta>> ReadScoreDeltasAsync(
        DbConnection connection,
        long baseAuditId,
        long targetAuditId,
        CancellationToken cancellationToken)
    {
        // Overall (null category) is keyed by the OverallKey sentinel because
        // nullable enums cannot serve as dictionary keys (notnull constraint).
        var baseScores = await ReadScoresAsync(connection, baseAuditId, cancellationToken);
        var targetScores = await ReadScoresAsync(connection, targetAuditId, cancellationToken);

        var keys = baseScores.Keys.Union(targetScores.Keys)
            .OrderBy(k => k == OverallKey ? 1 : 0)
            .ThenBy(k => k);
        var deltas = new List<ScoreDelta>();
        foreach (var key in keys)
        {
            baseScores.TryGetValue(key, out var baseScore);
            targetScores.TryGetValue(key, out var targetScore);
            deltas.Add(new ScoreDelta(
                key == OverallKey ? null : (AuditCategory)key,
                baseScores.ContainsKey(key) ? baseScore : null,
                targetScores.ContainsKey(key) ? targetScore : null,
                baseScores.ContainsKey(key) && targetScores.ContainsKey(key)
                    ? Math.Round(targetScore - baseScore, 2)
                    : null));
        }

        return deltas;
    }

    private const int OverallKey = -1;

    private static async Task<Dictionary<int, double>> ReadScoresAsync(
        DbConnection connection,
        long auditId,
        CancellationToken cancellationToken)
    {
        var scores = new Dictionary<int, double>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT category, score FROM audit_scores WHERE audit_id = @auditId";
        AddParameter(command, "@auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            scores[reader.IsDBNull(0) ? OverallKey : reader.GetInt32(0)] = reader.GetDouble(1);
        }

        return scores;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
