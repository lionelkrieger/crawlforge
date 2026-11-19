using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// Persists WAF challenge verdicts (FS-CRAWL-002 §2.6): the per-URL
/// <c>waf_detections</c> row (unique per audit and URL), the blocked URL
/// classification (<c>status_class = Blocked</c> with the <c>waf-challenge</c>
/// sub-classification and provider attribution — blocked URLs are never
/// fetched again), and the <c>waf-challenge-detected</c> finding. Detection
/// and reporting only (exclusion E3).
/// </summary>
public sealed class WafDetectionRecorder
{
    /// <summary>The block-reason sub-classification written to url_records for WAF challenges.</summary>
    public const string WafChallengeBlockReason = "waf-challenge";

    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.ffffff";

    private readonly IDbProvider _provider;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the recorder over a database provider.</summary>
    /// <param name="provider">Database provider holding the audit schema.</param>
    /// <param name="timeProvider">Clock for detection timestamps; defaults to the system clock.</param>
    public WafDetectionRecorder(IDbProvider provider, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Records a corroborated challenge verdict: upserts the detection row,
    /// classifies the URL as blocked with the waf-challenge sub-classification,
    /// and reports the per-URL finding. Re-recording the same (audit, URL)
    /// updates the existing row instead of duplicating it.
    /// </summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="urlId">Owning URL record.</param>
    /// <param name="result">Corroborated challenge verdict.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted detection row.</returns>
    public async Task<WafDetectionDto> RecordChallengeAsync(
        long auditId,
        long urlId,
        WafEvaluationResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsChallenge)
        {
            throw new ArgumentException("Only corroborated challenge verdicts may be recorded as detections.", nameof(result));
        }

        var detectedAt = FormatTimestamp(_timeProvider.GetUtcNow());
        var signatureKey = result.MatchedSignatureKeys.Count > 0 ? result.MatchedSignatureKeys[0] : null;

        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long detectionId;
        await using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = "SELECT id FROM waf_detections WHERE audit_id = @audit AND url_id = @url;";
            probe.Parameters.Add(CreateParameter(probe, "@audit", auditId));
            probe.Parameters.Add(CreateParameter(probe, "@url", urlId));
            var existing = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null and not DBNull)
            {
                detectionId = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE waf_detections SET provider = @provider, challenge_class = @class, evidence_json = @evidence, " +
                    "signature_key = @signature, resolved_by_rendering = 0, detected_at = @detected WHERE id = @id;";
                update.Parameters.Add(CreateParameter(update, "@provider", (int)result.Provider));
                update.Parameters.Add(CreateParameter(update, "@class", (int)result.ChallengeClass));
                update.Parameters.Add(CreateParameter(update, "@evidence", result.EvidenceJson));
                update.Parameters.Add(CreateParameter(update, "@signature", (object?)signatureKey ?? DBNull.Value));
                update.Parameters.Add(CreateParameter(update, "@detected", detectedAt));
                update.Parameters.Add(CreateParameter(update, "@id", detectionId));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO waf_detections (audit_id, url_id, provider, challenge_class, evidence_json, signature_key, resolved_by_rendering, detected_at) " +
                    "VALUES (@audit, @url, @provider, @class, @evidence, @signature, 0, @detected);";
                insert.Parameters.Add(CreateParameter(insert, "@audit", auditId));
                insert.Parameters.Add(CreateParameter(insert, "@url", urlId));
                insert.Parameters.Add(CreateParameter(insert, "@provider", (int)result.Provider));
                insert.Parameters.Add(CreateParameter(insert, "@class", (int)result.ChallengeClass));
                insert.Parameters.Add(CreateParameter(insert, "@evidence", result.EvidenceJson));
                insert.Parameters.Add(CreateParameter(insert, "@signature", (object?)signatureKey ?? DBNull.Value));
                insert.Parameters.Add(CreateParameter(insert, "@detected", detectedAt));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                detectionId = await LastInsertIdAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            }
        }

        // Blocked classification: never fetched again; excluded from content findings.
        await using (var classify = connection.CreateCommand())
        {
            classify.Transaction = transaction;
            classify.CommandText =
                "UPDATE url_records SET status_class = @status, block_reason = @reason, waf_provider = @provider, challenge_class = @class " +
                "WHERE id = @url AND audit_id = @audit;";
            classify.Parameters.Add(CreateParameter(classify, "@status", (int)CrawlStatusClass.Blocked));
            classify.Parameters.Add(CreateParameter(classify, "@reason", WafChallengeBlockReason));
            classify.Parameters.Add(CreateParameter(classify, "@provider", (int)result.Provider));
            classify.Parameters.Add(CreateParameter(classify, "@class", (int)result.ChallengeClass));
            classify.Parameters.Add(CreateParameter(classify, "@url", urlId));
            classify.Parameters.Add(CreateParameter(classify, "@audit", auditId));
            await classify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertFindingOnceAsync(
            connection,
            transaction,
            auditId,
            urlId,
            FindingRuleKeys.WafChallengeDetected,
            AuditCategory.Indexability,
            FindingSeverity.Warning,
            result.EvidenceJson,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new WafDetectionDto(
            detectionId,
            auditId,
            urlId,
            result.Provider,
            result.ChallengeClass,
            result.EvidenceJson,
            signatureKey,
            ResolvedByRendering: false,
            ParseTimestamp(detectedAt));
    }

    /// <summary>
    /// Reports a suspected-challenge notice for a sub-threshold or
    /// single-facet hit. The URL classification is never changed by a notice.
    /// </summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="urlId">Owning URL record.</param>
    /// <param name="result">Non-challenge verdict carrying suspected evidence.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the notice is persisted.</returns>
    public async Task RecordSuspectedNoticeAsync(
        long auditId,
        long urlId,
        WafEvaluationResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await InsertFindingOnceAsync(
            connection,
            transaction: null,
            auditId,
            urlId,
            FindingRuleKeys.SuspectedChallenge,
            AuditCategory.Indexability,
            FindingSeverity.Notice,
            result.EvidenceJson,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks a detection resolved by rendering and reclassifies the URL as a
    /// successful fetch, reporting the challenge-resolved-by-render notice.
    /// </summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="urlId">Owning URL record.</param>
    /// <param name="evidenceJson">Render-resolution evidence payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a detection row was updated.</returns>
    public async Task<bool> MarkResolvedByRenderingAsync(
        long auditId,
        long urlId,
        string evidenceJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceJson);
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int affected;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE waf_detections SET resolved_by_rendering = 1 WHERE audit_id = @audit AND url_id = @url;";
            update.Parameters.Add(CreateParameter(update, "@audit", auditId));
            update.Parameters.Add(CreateParameter(update, "@url", urlId));
            affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (affected > 0)
        {
            await using (var reclassify = connection.CreateCommand())
            {
                reclassify.Transaction = transaction;
                reclassify.CommandText =
                    "UPDATE url_records SET status_class = @status, block_reason = NULL, waf_provider = NULL, challenge_class = NULL " +
                    "WHERE id = @url AND audit_id = @audit;";
                reclassify.Parameters.Add(CreateParameter(reclassify, "@status", (int)CrawlStatusClass.Success));
                reclassify.Parameters.Add(CreateParameter(reclassify, "@url", urlId));
                reclassify.Parameters.Add(CreateParameter(reclassify, "@audit", auditId));
                await reclassify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertFindingOnceAsync(
                connection,
                transaction,
                auditId,
                urlId,
                FindingRuleKeys.ChallengeResolvedByRender,
                AuditCategory.Indexability,
                FindingSeverity.Notice,
                evidenceJson,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Lists all detections of an audit, ordered by URL reference.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Persisted detection rows.</returns>
    public async Task<IReadOnlyList<WafDetectionDto>> GetDetectionsAsync(long auditId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, audit_id, url_id, provider, challenge_class, evidence_json, signature_key, resolved_by_rendering, detected_at " +
            "FROM waf_detections WHERE audit_id = @audit ORDER BY url_id;";
        command.Parameters.Add(CreateParameter(command, "@audit", auditId));
        var detections = new List<WafDetectionDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            detections.Add(ReadDetection(reader));
        }

        return detections;
    }

    private static WafDetectionDto ReadDetection(DbDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetInt64(2),
        (WafProvider)reader.GetInt32(3),
        (WafChallengeClass)reader.GetInt32(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt32(7) == 1,
        ParseTimestamp(reader.GetString(8)));

    private static async Task InsertFindingOnceAsync(
        DbConnection connection,
        DbTransaction? transaction,
        long auditId,
        long urlId,
        string ruleKey,
        AuditCategory category,
        FindingSeverity severity,
        string evidenceJson,
        CancellationToken cancellationToken)
    {
        await using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = "SELECT COUNT(*) FROM findings WHERE audit_id = @audit AND rule_key = @rule AND url_id = @url;";
            probe.Parameters.Add(CreateParameter(probe, "@audit", auditId));
            probe.Parameters.Add(CreateParameter(probe, "@rule", ruleKey));
            probe.Parameters.Add(CreateParameter(probe, "@url", urlId));
            var existing = Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (existing > 0)
            {
                return;
            }
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO findings (audit_id, rule_key, category, severity, url_id, evidence_json, recommendation_key, is_suppressed) " +
            "VALUES (@audit, @rule, @category, @severity, @url, @evidence, NULL, 0);";
        insert.Parameters.Add(CreateParameter(insert, "@audit", auditId));
        insert.Parameters.Add(CreateParameter(insert, "@rule", ruleKey));
        insert.Parameters.Add(CreateParameter(insert, "@category", (int)category));
        insert.Parameters.Add(CreateParameter(insert, "@severity", (int)severity));
        insert.Parameters.Add(CreateParameter(insert, "@url", urlId));
        insert.Parameters.Add(CreateParameter(insert, "@evidence", evidenceJson));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> LastInsertIdAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MAX(id) FROM waf_detections;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static DbParameter CreateParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }

    private static string FormatTimestamp(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.ParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
