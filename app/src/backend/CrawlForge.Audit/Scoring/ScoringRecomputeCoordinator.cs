using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.Scoring;

/// <summary>
/// Offline recomputation coordinator (FS-CRAWL-001 §7.2 re-audit, §7.5
/// suppression changes, §8.1 aggregation): re-applies the CURRENT project
/// suppression set to persisted finding flags, then recomputes and records
/// category/overall scores and the scoring-owned metric aggregates. Serves
/// both the re-audit path and the suppression-change path. Every operation is
/// provider-agnostic SQL over stored rows — NO URL is ever re-fetched and no
/// content artifact is required.
/// </summary>
public sealed class ScoringRecomputeCoordinator
{
    private readonly IDbProvider _provider;
    private readonly AuditScoringService _scoring;
    private readonly AuditScoreRecorder _recorder;

    /// <summary>Creates the coordinator over the configured provider.</summary>
    /// <param name="provider">Database provider (SQLite or MySQL).</param>
    /// <param name="scoring">Optional scoring service (defaults to one over the same provider).</param>
    /// <param name="recorder">Optional score recorder (defaults to one over the same provider).</param>
    public ScoringRecomputeCoordinator(
        IDbProvider provider,
        AuditScoringService? scoring = null,
        AuditScoreRecorder? recorder = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _scoring = scoring ?? new AuditScoringService(provider);
        _recorder = recorder ?? new AuditScoreRecorder(provider);
    }

    /// <summary>
    /// Re-flags findings, recomputes scores, and refreshes aggregates for one
    /// audit. Returns false when the audit does not exist or is deleted.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the audit was recomputed.</returns>
    public async Task<bool> RecomputeForAuditAsync(long auditId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);

        long projectId;
        await using (var auditCommand = connection.CreateCommand())
        {
            auditCommand.CommandText = "SELECT project_id FROM audits WHERE id = @auditId AND is_deleted = 0";
            AddParameter(auditCommand, "@auditId", auditId);
            var result = await auditCommand.ExecuteScalarAsync(cancellationToken);
            if (result is null or DBNull)
            {
                return false;
            }

            projectId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }

        var suppressions = await ReadSuppressionsAsync(connection, projectId, cancellationToken);
        await ReapplyFlagsAsync(connection, auditId, suppressions, cancellationToken);

        var drafts = await _scoring.ComputeAsync(auditId, cancellationToken);
        await _recorder.RecordAsync(auditId, drafts, cancellationToken);
        return true;
    }

    /// <summary>
    /// Recomputes every non-deleted audit of a project (suppression-change
    /// path: project-scoped suppressions carry across all audits).
    /// </summary>
    /// <param name="projectId">Project identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of audits recomputed.</returns>
    public async Task<int> RecomputeForProjectAsync(long projectId, CancellationToken cancellationToken = default)
    {
        var auditIds = new List<long>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM audits WHERE project_id = @projectId AND is_deleted = 0 ORDER BY id";
            AddParameter(command, "@projectId", projectId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                auditIds.Add(reader.GetInt64(0));
            }
        }

        foreach (var auditId in auditIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecomputeForAuditAsync(auditId, cancellationToken);
        }

        return auditIds.Count;
    }

    private static async Task ReapplyFlagsAsync(
        DbConnection connection,
        long auditId,
        IReadOnlyList<FindingSuppressionDto> suppressions,
        CancellationToken cancellationToken)
    {
        var updates = new List<(long Id, bool Flag)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, rule_key, url_id, is_suppressed FROM findings WHERE audit_id = @auditId";
            AddParameter(command, "@auditId", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                var ruleKey = reader.GetString(1);
                var urlId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                var current = Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture);
                var target = SuppressionMatcher.IsSuppressed(ruleKey, urlId, suppressions);
                if (target != current)
                {
                    updates.Add((id, target));
                }
            }
        }

        if (updates.Count == 0)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (id, flag) in updates)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE findings SET is_suppressed = @flag WHERE id = @id";
            AddParameter(update, "@flag", flag ? 1 : 0);
            AddParameter(update, "@id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<FindingSuppressionDto>> ReadSuppressionsAsync(
        DbConnection connection,
        long projectId,
        CancellationToken cancellationToken)
    {
        var suppressions = new List<FindingSuppressionDto>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, suppression_key, rule_key, url_id, finding_scope, is_suppressed, created_at " +
            "FROM finding_suppressions WHERE project_id = @projectId";
        AddParameter(command, "@projectId", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            suppressions.Add(new FindingSuppressionDto(
                Id: reader.GetInt64(0),
                ProjectId: projectId,
                SuppressionKey: reader.GetString(1),
                RuleKey: reader.IsDBNull(2) ? null : reader.GetString(2),
                UrlId: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                FindingScope: reader.IsDBNull(4) ? null : reader.GetString(4),
                IsSuppressed: Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture),
                CreatedAt: DateTimeOffset.Parse(
                    reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return suppressions;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
