using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Persistence and offline reads of migration-matrix results (FS-CRAWL-002
/// §10.3/§10.6, §8.1 aggregate pattern). Per-row verdicts persist as findings
/// under the five additive <c>redirect-matrix.*</c> rule keys in the
/// Indexability &amp; status category; verdict aggregates persist as
/// <c>metric_summaries</c> rows under the <c>redirect-matrix</c> metric key
/// dimensioned by verdict. The aggregate refresh is transactional and
/// idempotent (delete + insert), so re-audits never double-count. All reads
/// are local database reads — results stay readable offline on re-audit.
/// SQL is provider-neutral (backtick identifiers, named parameters) and runs
/// on either configured provider through the locked <see cref="IDbProvider"/>.
/// </summary>
public static class RedirectMatrixResults
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Persists one row verdict as a finding inside the caller's transaction.
    /// Severity follows §10.3: chain-exceeded / final-non-200 / target-mismatch
    /// are Error; canonical-mismatch is Warning; pass is Pass. Evidence carries
    /// the full hop chain, final status, and observed canonical regardless of verdict.
    /// </summary>
    /// <param name="connection">Open database connection.</param>
    /// <param name="transaction">Active transaction owning the write.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="result">The validated row result.</param>
    /// <param name="urlId">url_records identifier of the source URL; 0 when absent (stored as null).</param>
    /// <param name="finalStatusCode">Observed final status (0 for transport failures).</param>
    /// <param name="transportError">Transport failure detail, when the fetch failed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the finding is written.</returns>
    public static async Task PersistFindingAsync(
        DbConnection connection,
        DbTransaction transaction,
        long auditId,
        RedirectMatrixRowResult result,
        long urlId,
        int finalStatusCode,
        string? transportError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(result);

        var evidence = new MatrixRowEvidence(
            result.Mapping.SourceUrl,
            result.Mapping.ExpectedTargetUrl,
            result.ActualFinalUrl,
            finalStatusCode,
            result.HopCount,
            result.Chain,
            result.CanonicalUrl,
            transportError);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO `findings` " +
            "(`audit_id`, `rule_key`, `category`, `severity`, `url_id`, `evidence_json`, `recommendation_key`, `is_suppressed`) " +
            "VALUES (@auditId, @ruleKey, @category, @severity, @urlId, @evidenceJson, NULL, 0)";
        AddParameter(command, "@auditId", auditId);
        AddParameter(command, "@ruleKey", RuleKeyOf(result.Verdict));
        AddParameter(command, "@category", (int)AuditCategory.Indexability);
        AddParameter(command, "@severity", (int)SeverityOf(result.Verdict));
        AddParameter(command, "@urlId", urlId > 0 ? urlId : DBNull.Value);
        AddParameter(command, "@evidenceJson", JsonSerializer.Serialize(evidence, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes every <c>redirect-matrix.*</c> finding of an audit inside the
    /// caller's transaction (WP-06-01 reopen, defect D4). The validation pass
    /// calls this ONCE before persisting its row verdicts, so re-validation
    /// replaces the prior finding set instead of appending — mirroring the
    /// idempotent delete + insert of <see cref="RefreshAggregatesAsync"/>.
    /// Sibling finding families are never touched.
    /// </summary>
    /// <param name="connection">Open database connection.</param>
    /// <param name="transaction">Active transaction owning the write.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of prior matrix findings removed.</returns>
    public static async Task<int> DeleteFindingsAsync(
        DbConnection connection,
        DbTransaction transaction,
        long auditId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM `findings` WHERE `audit_id` = @auditId AND `rule_key` LIKE 'redirect-matrix.%'";
        AddParameter(command, "@auditId", auditId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes the <c>redirect-matrix</c> verdict aggregates of an audit
    /// (§8.1): one transactional delete + insert of all five verdict dimension
    /// rows (zero counts included for stable offline reads). Re-running with
    /// the same results is a no-op in effect — never double-counted.
    /// </summary>
    /// <param name="provider">Configured database provider.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="results">Validated row results of the audit's matrix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the aggregates are durable.</returns>
    public static async Task RefreshAggregatesAsync(
        IDbProvider provider,
        long auditId,
        IReadOnlyList<RedirectMatrixRowResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(results);

        var counts = new Dictionary<RedirectMatrixVerdict, int>();
        foreach (RedirectMatrixVerdict verdict in Enum.GetValues<RedirectMatrixVerdict>())
        {
            counts[verdict] = 0;
        }

        foreach (var result in results)
        {
            counts[result.Verdict]++;
        }

        await using var connection = await provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM `metric_summaries` WHERE `audit_id` = @auditId AND `metric_key` = @metricKey";
            AddParameter(delete, "@auditId", auditId);
            AddParameter(delete, "@metricKey", MetricKeys.RedirectMatrix);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var (verdict, count) in counts)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO `metric_summaries` (`audit_id`, `metric_key`, `dimensions_json`, `metric_value`) " +
                "VALUES (@auditId, @metricKey, @dimensionsJson, @metricValue)";
            AddParameter(insert, "@auditId", auditId);
            AddParameter(insert, "@metricKey", MetricKeys.RedirectMatrix);
            AddParameter(insert, "@dimensionsJson", JsonSerializer.Serialize(
                new Dictionary<string, string> { ["verdict"] = MatrixVerdictNames.Of(verdict) }, JsonOptions));
            AddParameter(insert, "@metricValue", (double)count);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    /// <summary>
    /// Reads the persisted verdict aggregates of an audit — a purely local
    /// database read, so matrix results remain readable offline on re-audit
    /// without any fetch (FS-CRAWL-002 §10.3/§8.1).
    /// </summary>
    /// <param name="provider">Configured database provider.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verdict counts in enum order; empty when no matrix ran.</returns>
    public static async Task<IReadOnlyList<MatrixVerdictCount>> ReadAggregatesAsync(
        IDbProvider provider,
        long auditId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        await using var connection = await provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `dimensions_json`, `metric_value` FROM `metric_summaries` " +
            "WHERE `audit_id` = @auditId AND `metric_key` = @metricKey";
        AddParameter(command, "@auditId", auditId);
        AddParameter(command, "@metricKey", MetricKeys.RedirectMatrix);

        var counts = new Dictionary<RedirectMatrixVerdict, int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dimensions = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(0), JsonOptions);
            if (dimensions is null || !dimensions.TryGetValue("verdict", out var name))
            {
                continue;
            }

            var verdict = MatrixVerdictNames.Parse(name);
            counts[verdict] = Convert.ToInt32(reader.GetDouble(1), System.Globalization.CultureInfo.InvariantCulture);
        }

        return Enum.GetValues<RedirectMatrixVerdict>()
            .Select(verdict => new MatrixVerdictCount(verdict, counts.GetValueOrDefault(verdict)))
            .ToList();
    }

    /// <summary>
    /// Reads the persisted per-row matrix findings of an audit in insertion
    /// order — a purely local database read for offline re-audit surfaces.
    /// </summary>
    /// <param name="provider">Configured database provider.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Finding rows carrying rule key, severity, URL reference, and evidence JSON.</returns>
    public static async Task<IReadOnlyList<MatrixFindingRow>> ReadFindingsAsync(
        IDbProvider provider,
        long auditId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        await using var connection = await provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `rule_key`, `severity`, `url_id`, `evidence_json` FROM `findings` " +
            "WHERE `audit_id` = @auditId AND `rule_key` LIKE 'redirect-matrix.%' ORDER BY `id`";
        AddParameter(command, "@auditId", auditId);

        var rows = new List<MatrixFindingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new MatrixFindingRow(
                reader.GetString(0),
                (FindingSeverity)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>
    /// Resolves the url_records identifier of a canonical URL within an audit
    /// (0 when the URL has no record). Used for hub batch URL identifiers.
    /// </summary>
    /// <param name="connection">Open database connection.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="canonicalUrl">Canonical URL to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The url_records identifier, or 0.</returns>
    public static async Task<long> FindUrlIdAsync(
        DbConnection connection,
        long auditId,
        string canonicalUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(canonicalUrl);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `id` FROM `url_records` WHERE `audit_id` = @auditId AND `canonical_url` = @canonicalUrl";
        AddParameter(command, "@auditId", auditId);
        AddParameter(command, "@canonicalUrl", canonicalUrl);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0L : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Maps a verdict to its locked additive finding rule key.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <returns>The rule key constant.</returns>
    public static string RuleKeyOf(RedirectMatrixVerdict verdict) => verdict switch
    {
        RedirectMatrixVerdict.Pass => FindingRuleKeys.RedirectMatrixPass,
        RedirectMatrixVerdict.TargetMismatch => FindingRuleKeys.RedirectMatrixTargetMismatch,
        RedirectMatrixVerdict.ChainExceeded => FindingRuleKeys.RedirectMatrixChainExceeded,
        RedirectMatrixVerdict.FinalNon200 => FindingRuleKeys.RedirectMatrixFinalNon200,
        RedirectMatrixVerdict.CanonicalMismatch => FindingRuleKeys.RedirectMatrixCanonicalMismatch,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown redirect-matrix verdict."),
    };

    /// <summary>Maps a verdict to its §10.3 severity.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <returns>The finding severity.</returns>
    public static FindingSeverity SeverityOf(RedirectMatrixVerdict verdict) => verdict switch
    {
        RedirectMatrixVerdict.ChainExceeded or RedirectMatrixVerdict.FinalNon200 or RedirectMatrixVerdict.TargetMismatch => FindingSeverity.Error,
        RedirectMatrixVerdict.CanonicalMismatch => FindingSeverity.Warning,
        RedirectMatrixVerdict.Pass => FindingSeverity.Pass,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown redirect-matrix verdict."),
    };

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>
/// Evidence payload of one matrix row finding (FS-CRAWL-002 §10.3: the full
/// hop chain, final status, and observed canonical are retained regardless of
/// verdict).
/// </summary>
/// <param name="SourceUrl">Source URL of the matrix row.</param>
/// <param name="ExpectedTargetUrl">Expected target URL of the matrix row.</param>
/// <param name="ActualFinalUrl">Observed final URL, when reachable.</param>
/// <param name="FinalStatusCode">Observed final status (0 for transport failures).</param>
/// <param name="HopCount">Number of observed redirect hops.</param>
/// <param name="Chain">Observed hop chain in order.</param>
/// <param name="CanonicalUrl">Declared canonical of the final page, when present.</param>
/// <param name="TransportError">Transport failure detail, when the fetch failed.</param>
public sealed record MatrixRowEvidence(
    string SourceUrl,
    string ExpectedTargetUrl,
    string? ActualFinalUrl,
    int FinalStatusCode,
    int HopCount,
    IReadOnlyList<RedirectHop> Chain,
    string? CanonicalUrl,
    string? TransportError);

/// <summary>
/// One persisted matrix finding read back for offline re-audit surfaces.
/// </summary>
/// <param name="RuleKey">Additive redirect-matrix rule key.</param>
/// <param name="Severity">Finding severity per §10.3.</param>
/// <param name="UrlId">Source url_records reference, when present.</param>
/// <param name="EvidenceJson">Serialized <see cref="MatrixRowEvidence"/>.</param>
public sealed record MatrixFindingRow(string RuleKey, FindingSeverity Severity, long? UrlId, string EvidenceJson);
