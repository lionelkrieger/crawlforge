using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// Site-wide WAF block-rate diagnostic verdict (FS-CRAWL-002 §2.6): the share
/// of WAF-blocked URLs among all crawled URLs of an audit. Below the minimum
/// sample the diagnostic never reports; at or above 1% it reports a Warning,
/// at or above 5% an Error.
/// </summary>
/// <param name="AuditId">Owning audit.</param>
/// <param name="TotalUrls">Total crawled URL records of the audit.</param>
/// <param name="WafBlockedUrls">URLs classified blocked with the waf-challenge sub-classification.</param>
/// <param name="BlockRate">Blocked share in [0, 1]; zero when no URLs exist.</param>
/// <param name="Severity">Finding severity, or null when below the warning threshold.</param>
/// <param name="InsufficientSample">Whether the URL sample is below the diagnostic minimum.</param>
public sealed record WafBlockRateReport(
    long AuditId,
    int TotalUrls,
    int WafBlockedUrls,
    double BlockRate,
    FindingSeverity? Severity,
    bool InsufficientSample);

/// <summary>
/// Computes and reports the site-wide <c>waf-block-rate</c> diagnostic
/// (1% Warning / 5% Error, minimum 20 URLs) and persists the
/// <c>waf-blocks</c> / <c>waf-block-rate</c> metric summaries. Reporting only
/// (exclusion E3); all inputs are local audit facts (exclusion E4).
/// </summary>
public sealed class WafBlockRateDiagnostic
{
    private readonly IDbProvider _provider;
    private readonly WafDetectionPolicy _policy;

    /// <summary>Creates the diagnostic over a database provider.</summary>
    /// <param name="provider">Database provider holding the audit schema.</param>
    /// <param name="policy">Detection policy carrying the thresholds; null uses the defaults.</param>
    public WafBlockRateDiagnostic(IDbProvider provider, WafDetectionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _policy = policy ?? WafDetectionPolicy.Default;
    }

    /// <summary>Computes the block-rate verdict without persisting anything.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The block-rate report.</returns>
    public async Task<WafBlockRateReport> ComputeAsync(long auditId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var total = await CountAsync(connection, "SELECT COUNT(*) FROM url_records WHERE audit_id = @audit;", auditId, cancellationToken).ConfigureAwait(false);
        var blocked = await CountAsync(
            connection,
            "SELECT COUNT(*) FROM url_records WHERE audit_id = @audit AND status_class = @status AND block_reason = @reason;",
            auditId,
            cancellationToken,
            ("@status", (int)CrawlStatusClass.Blocked),
            ("@reason", WafDetectionRecorder.WafChallengeBlockReason)).ConfigureAwait(false);

        var rate = total == 0 ? 0.0 : (double)blocked / total;
        var insufficient = total < _policy.MinimumUrlsForBlockRate;
        FindingSeverity? severity = null;
        if (!insufficient)
        {
            if (rate >= _policy.BlockRateErrorThreshold)
            {
                severity = FindingSeverity.Error;
            }
            else if (rate >= _policy.BlockRateWarningThreshold)
            {
                severity = FindingSeverity.Warning;
            }
        }

        return new WafBlockRateReport(auditId, total, blocked, rate, severity, insufficient);
    }

    /// <summary>
    /// Computes the verdict and, when it reaches a threshold, persists the
    /// site-wide finding (idempotent) plus the waf-blocks and waf-block-rate
    /// metric summaries.
    /// </summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The block-rate report.</returns>
    public async Task<WafBlockRateReport> ReportAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var report = await ComputeAsync(auditId, cancellationToken).ConfigureAwait(false);

        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await UpsertMetricAsync(connection, transaction, auditId, MetricKeys.WafBlocks, report.WafBlockedUrls, cancellationToken).ConfigureAwait(false);
        await UpsertMetricAsync(connection, transaction, auditId, MetricKeys.WafBlockRate, report.BlockRate, cancellationToken).ConfigureAwait(false);

        if (report.Severity is { } severity)
        {
            var evidence = JsonSerializer.Serialize(new
            {
                totalUrls = report.TotalUrls,
                wafBlockedUrls = report.WafBlockedUrls,
                blockRate = report.BlockRate,
                warningThreshold = _policy.BlockRateWarningThreshold,
                errorThreshold = _policy.BlockRateErrorThreshold,
                minimumUrls = _policy.MinimumUrlsForBlockRate,
                severity = severity.ToString(),
            });

            await using var probe = connection.CreateCommand();
            probe.Transaction = transaction;
            probe.CommandText = "SELECT COUNT(*) FROM findings WHERE audit_id = @audit AND rule_key = @rule AND url_id IS NULL;";
            probe.Parameters.Add(CreateParameter(probe, "@audit", auditId));
            probe.Parameters.Add(CreateParameter(probe, "@rule", FindingRuleKeys.WafBlockRate));
            var existing = Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (existing == 0)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO findings (audit_id, rule_key, category, severity, url_id, evidence_json, recommendation_key, is_suppressed) " +
                    "VALUES (@audit, @rule, @category, @severity, NULL, @evidence, NULL, 0);";
                insert.Parameters.Add(CreateParameter(insert, "@audit", auditId));
                insert.Parameters.Add(CreateParameter(insert, "@rule", FindingRuleKeys.WafBlockRate));
                insert.Parameters.Add(CreateParameter(insert, "@category", (int)AuditCategory.Crawl));
                insert.Parameters.Add(CreateParameter(insert, "@severity", (int)severity));
                insert.Parameters.Add(CreateParameter(insert, "@evidence", evidence));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return report;
    }

    private static async Task<int> CountAsync(
        DbConnection connection,
        string sql,
        long auditId,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] extraParameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(CreateParameter(command, "@audit", auditId));
        foreach (var (parameterName, parameterValue) in extraParameters)
        {
            command.Parameters.Add(CreateParameter(command, parameterName, parameterValue));
        }

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task UpsertMetricAsync(
        DbConnection connection,
        DbTransaction transaction,
        long auditId,
        string metricKey,
        double value,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM metric_summaries WHERE audit_id = @audit AND metric_key = @key;";
            delete.Parameters.Add(CreateParameter(delete, "@audit", auditId));
            delete.Parameters.Add(CreateParameter(delete, "@key", metricKey));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO metric_summaries (audit_id, metric_key, dimensions_json, metric_value) VALUES (@audit, @key, '{}', @value);";
        insert.Parameters.Add(CreateParameter(insert, "@audit", auditId));
        insert.Parameters.Add(CreateParameter(insert, "@key", metricKey));
        insert.Parameters.Add(CreateParameter(insert, "@value", value));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbParameter CreateParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }
}
