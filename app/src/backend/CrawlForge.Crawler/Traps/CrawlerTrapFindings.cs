using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Traps;

/// <summary>
/// Persistence and offline reads of crawler-trap findings (FS-CRAWL-001 §5.14:
/// "a flagged pattern is throttled and raised as a finding naming the pattern,
/// its URL count and examples"). One site-wide finding per flagged pattern
/// under the additive rule key <see cref="CrawlerTrapRuleKeys.TrapDetected"/>,
/// category <see cref="AuditCategory.Crawl"/> (crawl-health), severity
/// <see cref="FindingSeverity.Warning"/>. Evidence carries the pattern key,
/// family, URL counts, near-duplicate ratio and bounded example URLs.
/// <para>
/// The write is transactional and idempotent (delete + insert of the audit's
/// <c>crawler-trap.*</c> finding set), so re-validation never duplicates
/// findings — mirroring the redirect-matrix aggregate pattern. SQL is
/// provider-neutral (backtick identifiers, named parameters) through the
/// locked <see cref="IDbProvider"/>; all reads are local database reads.
/// </para>
/// </summary>
public static class CrawlerTrapFindings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Persists trap detections as site-wide findings inside one transaction:
    /// the audit's prior <c>crawler-trap.*</c> findings are replaced, never
    /// appended. Sibling finding families are never touched.
    /// </summary>
    /// <param name="provider">Configured database provider.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="detections">Detections to persist (one finding each).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of findings written.</returns>
    public static async Task<int> PersistAsync(
        IDbProvider provider,
        long auditId,
        IReadOnlyList<TrapDetection> detections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(detections);

        await using var connection = await provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM `findings` WHERE `audit_id` = @auditId AND `rule_key` LIKE 'crawler-trap.%'";
            AddParameter(delete, "@auditId", auditId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var detection in detections)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO `findings` " +
                "(`audit_id`, `rule_key`, `category`, `severity`, `url_id`, `evidence_json`, `recommendation_key`, `is_suppressed`) " +
                "VALUES (@auditId, @ruleKey, @category, @severity, NULL, @evidenceJson, NULL, 0)";
            AddParameter(insert, "@auditId", auditId);
            AddParameter(insert, "@ruleKey", CrawlerTrapRuleKeys.TrapDetected);
            AddParameter(insert, "@category", (int)AuditCategory.Crawl);
            AddParameter(insert, "@severity", (int)FindingSeverity.Warning);
            AddParameter(insert, "@evidenceJson", JsonSerializer.Serialize(detection, JsonOptions));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return detections.Count;
    }

    /// <summary>
    /// Reads the persisted crawler-trap findings of an audit in insertion
    /// order — a purely local database read for offline report surfaces.
    /// </summary>
    /// <param name="provider">Configured database provider.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Finding rows carrying rule key, severity and evidence JSON.</returns>
    public static async Task<IReadOnlyList<TrapFindingRow>> ReadFindingsAsync(
        IDbProvider provider,
        long auditId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        await using var connection = await provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `rule_key`, `severity`, `evidence_json` FROM `findings` " +
            "WHERE `audit_id` = @auditId AND `rule_key` LIKE 'crawler-trap.%' ORDER BY `id`";
        AddParameter(command, "@auditId", auditId);

        var rows = new List<TrapFindingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TrapFindingRow(
                reader.GetString(0),
                (FindingSeverity)reader.GetInt32(1),
                reader.GetString(2)));
        }

        return rows;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>
/// One persisted crawler-trap finding read back for offline report surfaces.
/// </summary>
/// <param name="RuleKey">Additive crawler-trap rule key.</param>
/// <param name="Severity">Finding severity (Warning per §5.14 reporting).</param>
/// <param name="EvidenceJson">Serialized <see cref="TrapDetection"/>.</param>
public sealed record TrapFindingRow(string RuleKey, FindingSeverity Severity, string EvidenceJson);
