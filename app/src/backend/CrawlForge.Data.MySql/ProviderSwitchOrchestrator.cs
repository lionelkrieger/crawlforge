using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Data.MySql;

/// <summary>
/// Provider-switch orchestration contract (FS-CRAWL-001 §2.4, criterion #18).
/// Switching providers is an explicit, user-initiated operation using the archive
/// export/import mechanism — never automatic. The forward switch (embedded →
/// external MySQL) runs, in order:
/// <list type="number">
/// <item>connection test on the target — reachability, credentials, and migration
/// compatibility must all pass before anything else happens;</item>
/// <item>project archive export from the source into a self-contained local bundle;</item>
/// <item>schema migration on the target, with ledger verification that every applied
/// migration belongs to the target dialect;</item>
/// <item>archive import into the target.</item>
/// </list>
/// Report parity (identical report output on either provider) is verified by
/// comparing the precomputed metric and score read models of an audit; the revert
/// path imports an archive back into the embedded provider. The end-to-end exercise
/// (full audit on MySQL, identical reports, revert) runs in Phase 07.
/// </summary>
public sealed class ProviderSwitchOrchestrator
{
    private readonly ProviderSwitchEndpoint _source;
    private readonly ProviderSwitchEndpoint _target;

    /// <summary>Creates an orchestrator between two provider endpoints.</summary>
    /// <param name="source">Source endpoint (embedded provider for the forward switch).</param>
    /// <param name="target">Target endpoint (external MySQL for the forward switch).</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ProviderSwitchOrchestrator(ProviderSwitchEndpoint source, ProviderSwitchEndpoint target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        _source = source;
        _target = target;
    }

    /// <summary>
    /// Switches one project from the source provider to the target provider. When
    /// the target connection test fails, the operation aborts before any export,
    /// migration, or import — the source remains untouched.
    /// </summary>
    /// <param name="projectId">Project to switch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The switch outcome.</returns>
    public async Task<ProviderSwitchResult> SwitchAsync(long projectId, CancellationToken cancellationToken = default)
    {
        // Gate: the target must be reachable, authenticated, and migration-compatible
        // before first use (criterion #18). No side effects on failure.
        DbConnectionTestResult test = await _target.Provider.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!test.IsReachable || !test.AuthenticationOk || !test.MigrationCompatible)
        {
            return ProviderSwitchResult.Failed($"Connection test failed before switching: {test.Message}");
        }

        // Export the project into a self-contained local bundle (the provider-migration
        // vehicle, §2.4). The bundle never leaves the machine.
        using var bundle = new MemoryStream();
        await _source.Archive.ExportAsync(projectId, bundle, cancellationToken).ConfigureAwait(false);
        bundle.Position = 0;

        // Migrate the target schema, then verify the ledger is complete and belongs
        // to the target dialect (the migration ledger is idempotent per provider).
        await _target.Migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SchemaMigrationDto> ledger = await _target.Migrator.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        if (ledger.Count == 0)
        {
            return ProviderSwitchResult.Failed("The target migration ledger is empty after migration.");
        }

        int version = 0;
        foreach (SchemaMigrationDto row in ledger)
        {
            if (!string.Equals(row.ProviderDialect, _target.Provider.Dialect, StringComparison.Ordinal))
            {
                return ProviderSwitchResult.Failed(
                    $"The target migration ledger contains dialect '{row.ProviderDialect}' instead of '{_target.Provider.Dialect}'.");
            }

            if (row.Version > version)
            {
                version = row.Version;
            }
        }

        ProjectArchiveImportResult import = await _target.Archive.ImportAsync(bundle, cancellationToken).ConfigureAwait(false);
        return new ProviderSwitchResult(true, null, version, import);
    }

    /// <summary>
    /// Reverts to the embedded provider by importing a project archive (criterion
    /// #18 revert path). The embedded schema is brought up to date first; no
    /// connection test is needed because the embedded provider is local and offline.
    /// </summary>
    /// <param name="archive">Archive stream owned by the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The revert outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="archive"/> is null.</exception>
    public async Task<ProviderSwitchResult> RevertAsync(Stream archive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);

        await _source.Migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        ProjectArchiveImportResult import = await _source.Archive.ImportAsync(archive, cancellationToken).ConfigureAwait(false);
        int version = await _source.Migrator.GetCurrentVersionAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderSwitchResult(true, null, version, import);
    }

    /// <summary>
    /// Compares two providers' report output for one audit: the precomputed metric
    /// summaries and audit scores must match exactly (identical report output,
    /// criterion #18). Comparison is order-independent; values compare exactly.
    /// </summary>
    /// <param name="sourceMetrics">Source provider metric rows.</param>
    /// <param name="targetMetrics">Target provider metric rows.</param>
    /// <param name="sourceScores">Source provider score rows.</param>
    /// <param name="targetScores">Target provider score rows.</param>
    /// <returns>The parity verdict.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ReportParityResult CompareReports(
        IReadOnlyList<MetricSummaryRow> sourceMetrics,
        IReadOnlyList<MetricSummaryRow> targetMetrics,
        IReadOnlyList<AuditScoreRow> sourceScores,
        IReadOnlyList<AuditScoreRow> targetScores)
    {
        ArgumentNullException.ThrowIfNull(sourceMetrics);
        ArgumentNullException.ThrowIfNull(targetMetrics);
        ArgumentNullException.ThrowIfNull(sourceScores);
        ArgumentNullException.ThrowIfNull(targetScores);

        var differences = new List<string>();

        Dictionary<(string Key, string Dimensions), double> sourceMetricMap = ToMetricMap(sourceMetrics, "source", differences);
        Dictionary<(string Key, string Dimensions), double> targetMetricMap = ToMetricMap(targetMetrics, "target", differences);
        CompareMaps(sourceMetricMap, targetMetricMap, "metric", differences);

        Dictionary<string, (double Score, string Counts)> sourceScoreMap = ToScoreMap(sourceScores, "source", differences);
        Dictionary<string, (double Score, string Counts)> targetScoreMap = ToScoreMap(targetScores, "target", differences);
        CompareScoreMaps(sourceScoreMap, targetScoreMap, differences);

        return new ReportParityResult(
            differences.Count == 0,
            Math.Max(sourceMetrics.Count, targetMetrics.Count),
            Math.Max(sourceScores.Count, targetScores.Count),
            differences);
    }

    /// <summary>
    /// Reads one audit's report rows from an open connection using provider-neutral
    /// SQL (backtick quoting is accepted by both MySQL and SQLite). The caller owns
    /// the connection.
    /// </summary>
    /// <remarks>
    /// DEFECT D10: <c>audit_scores</c> stores the contributing counts as the four
    /// integer columns written by the score recorder (error_count, warning_count,
    /// notice_count, pass_count) — there is no contributing_counts_json column in
    /// the locked schema. The counts payload is composed here from those columns
    /// with a deterministic camelCase shape, identical on both providers. The
    /// nullable integer category maps to its <see cref="AuditCategory"/> name, or
    /// to the overall marker when NULL (the overall score).
    /// </remarks>
    /// <param name="connection">Open connection to either provider.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The audit's metric summaries and scores.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public static async Task<(IReadOnlyList<MetricSummaryRow> Metrics, IReadOnlyList<AuditScoreRow> Scores)> ReadReportRowsAsync(
        DbConnection connection,
        long auditId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var metrics = new List<MetricSummaryRow>();
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT `metric_key`, `dimensions_json`, `metric_value` FROM `metric_summaries` WHERE `audit_id` = @auditId ORDER BY `metric_key`, `dimensions_json`";
            AddAuditParameter(command, auditId);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                metrics.Add(new MetricSummaryRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "{}" : reader.GetString(1),
                    reader.GetDouble(2)));
            }
        }

        var scores = new List<AuditScoreRow>();
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT `category`, `score`, `error_count`, `warning_count`, `notice_count`, `pass_count` "
                + "FROM `audit_scores` WHERE `audit_id` = @auditId ORDER BY `category`";
            AddAuditParameter(command, auditId);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                scores.Add(new AuditScoreRow(
                    reader.IsDBNull(0) ? OverallCategoryKey : ((AuditCategory)reader.GetInt32(0)).ToString(),
                    reader.GetDouble(1),
                    JsonSerializer.Serialize(
                        new ContributingCounts(
                            reader.GetInt32(2),
                            reader.GetInt32(3),
                            reader.GetInt32(4),
                            reader.GetInt32(5)),
                        CountsJsonOptions)));
            }
        }

        return (metrics, scores);
    }

    /// <summary>Category key of the overall score row (NULL category in the schema).</summary>
    private const string OverallCategoryKey = "overall";

    private static readonly JsonSerializerOptions CountsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The four contributing count columns of audit_scores, serialized with a
    /// deterministic camelCase shape so both providers produce identical payloads.
    /// </summary>
    private sealed record ContributingCounts(int ErrorCount, int WarningCount, int NoticeCount, int PassCount);

    private static void AddAuditParameter(DbCommand command, long auditId)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
    }

    private static Dictionary<(string Key, string Dimensions), double> ToMetricMap(
        IReadOnlyList<MetricSummaryRow> rows, string side, List<string> differences)
    {
        var map = new Dictionary<(string, string), double>();
        foreach (MetricSummaryRow row in rows)
        {
            if (!map.TryAdd((row.MetricKey, row.DimensionsJson), row.Value))
            {
                differences.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Duplicate {side} metric row for '{row.MetricKey}' with identical dimensions."));
            }
        }

        return map;
    }

    private static Dictionary<string, (double Score, string Counts)> ToScoreMap(
        IReadOnlyList<AuditScoreRow> rows, string side, List<string> differences)
    {
        var map = new Dictionary<string, (double, string)>(StringComparer.Ordinal);
        foreach (AuditScoreRow row in rows)
        {
            if (!map.TryAdd(row.Category, (row.Score, row.ContributingCountsJson)))
            {
                differences.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Duplicate {side} score row for category '{row.Category}'."));
            }
        }

        return map;
    }

    private static void CompareMaps(
        Dictionary<(string Key, string Dimensions), double> source,
        Dictionary<(string Key, string Dimensions), double> target,
        string label,
        List<string> differences)
    {
        foreach (((string key, string dimensions), double value) in source)
        {
            if (!target.TryGetValue((key, dimensions), out double targetValue))
            {
                differences.Add(string.Create(CultureInfo.InvariantCulture, $"Missing {label} '{key}' on target."));
            }
            else if (value != targetValue)
            {
                differences.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Value mismatch for {label} '{key}': source={value}, target={targetValue}."));
            }
        }

        foreach ((string key, string dimensions) in target.Keys)
        {
            if (!source.ContainsKey((key, dimensions)))
            {
                differences.Add(string.Create(CultureInfo.InvariantCulture, $"Unexpected {label} '{key}' on target."));
            }
        }
    }

    private static void CompareScoreMaps(
        Dictionary<string, (double Score, string Counts)> source,
        Dictionary<string, (double Score, string Counts)> target,
        List<string> differences)
    {
        foreach ((string category, (double score, string counts)) in source)
        {
            if (!target.TryGetValue(category, out (double Score, string Counts) targetRow))
            {
                differences.Add(string.Create(CultureInfo.InvariantCulture, $"Missing score category '{category}' on target."));
            }
            else if (score != targetRow.Score || !string.Equals(counts, targetRow.Counts, StringComparison.Ordinal))
            {
                differences.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Score mismatch for category '{category}': source={score}, target={targetRow.Score}."));
            }
        }

        foreach (string category in target.Keys)
        {
            if (!source.ContainsKey(category))
            {
                differences.Add(string.Create(CultureInfo.InvariantCulture, $"Unexpected score category '{category}' on target."));
            }
        }
    }
}
