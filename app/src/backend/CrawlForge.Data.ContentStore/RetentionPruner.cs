using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.Repositories;

namespace CrawlForge.Data.ContentStore;

/// <summary>
/// Transactional retention pruner (FS-CRAWL-001 §9.5): removes audit-scoped rows
/// across every baseline and additive table in one atomic transaction, then
/// reclaims orphaned content-store files. Configuration tables are never pruned.
///
/// Policy semantics (null fields are unconstrained):
/// - <c>AuditId</c> targets one explicit audit (bypasses keep-last protection).
/// - <c>OlderThan</c> targets audits whose finished/started timestamp is older,
///   and prunes automation action-log entries older than the cutoff.
/// - <c>KeepLastAudits</c> protects the N most recent live audits per project;
///   on its own it prunes everything older than the kept window.
/// Soft-deleted audits are always candidates. File cleanup runs only after the
/// database transaction commits, so a failed prune rolls back with files intact.
/// </summary>
public sealed class RetentionPruner : IRetentionPruner
{
    // Audit-scoped tables pruned directly by audit_id (baseline + additive).
    private static readonly string[] AuditScopedTables =
    [
        "link_edges",
        "findings",
        "metric_summaries",
        "crawl_checkpoints",
        "content_artifacts",
        "url_embeddings",
        "extracted_content",
        "content_search_results",
        "snippet_results",
        "technology_detections",
        "audit_scores",
        "waf_detections",
        "host_throttle_events",
        "ngram_frequencies",
        "ngram_site_summaries",
        "redirect_mappings",
        "semantic_link_opportunities",
        "semantic_clusters",
        "governance_matrix",
        "analytics_detections",
        "schedule_runs"
    ];

    private readonly IDbProvider _provider;
    private readonly IArtifactFileStore _files;

    /// <summary>Creates the pruner.</summary>
    /// <param name="provider">The configured database provider.</param>
    /// <param name="files">File-level access to the compressed content store.</param>
    public RetentionPruner(IDbProvider provider, IArtifactFileStore files)
    {
        _provider = provider;
        _files = files;
    }

    /// <inheritdoc />
    public async Task<RetentionPruneResult> PruneAsync(RetentionPolicy policy, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);

        var audits = await LoadAuditsAsync(connection, cancellationToken);
        var candidates = SelectCandidates(audits, policy);

        var deletedRows = 0;
        var prunedTables = new List<string>();
        var artifactKeys = new HashSet<string>(StringComparer.Ordinal);

        if (candidates.Count > 0)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var inClause = string.Join(", ", candidates.Select((_, i) => "@a" + i));
            var auditParameters = candidates.Select((id, i) => ("@a" + i, (object)id)).ToList();

            // Collect referenced artifact keys before their index rows disappear.
            await using (var collect = connection.CreateCommand())
            {
                collect.Transaction = transaction;
                collect.CommandText = $"SELECT storage_key FROM content_artifacts WHERE audit_id IN ({inClause})";
                AddParameters(collect, auditParameters);
                await using var reader = await collect.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    artifactKeys.Add(reader.GetString(0));
                }
            }

            // Children first: url_elements hangs off url_records.
            deletedRows += await ExecuteDeleteAsync(connection, transaction,
                $"DELETE FROM url_elements WHERE url_id IN (SELECT id FROM url_records WHERE audit_id IN ({inClause}))",
                "url_elements", auditParameters, prunedTables, cancellationToken);

            foreach (var table in AuditScopedTables)
            {
                deletedRows += await ExecuteDeleteAsync(connection, transaction,
                    $"DELETE FROM {table} WHERE audit_id IN ({inClause})",
                    table, auditParameters, prunedTables, cancellationToken);
            }

            deletedRows += await ExecuteDeleteAsync(connection, transaction,
                $"DELETE FROM url_records WHERE audit_id IN ({inClause})",
                "url_records", auditParameters, prunedTables, cancellationToken);

            deletedRows += await ExecuteDeleteAsync(connection, transaction,
                $"DELETE FROM audits WHERE id IN ({inClause})",
                "audits", auditParameters, prunedTables, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        // Age-based pruning of the global automation action log (no audit reference).
        if (policy.OlderThan is { } cutoff)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var cutoffText = cutoff.ToString("O", CultureInfo.InvariantCulture);
            deletedRows += await ExecuteDeleteAsync(connection, transaction,
                "DELETE FROM automation_action_log WHERE timestamp < @cutoff",
                "automation_action_log", [("@cutoff", cutoffText)], prunedTables, cancellationToken);
            deletedRows += await ExecuteDeleteAsync(connection, transaction,
                "DELETE FROM schedule_runs WHERE audit_id IS NULL AND started_at < @cutoff",
                "schedule_runs", [("@cutoff", cutoffText)], prunedTables, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Post-commit file cleanup: reclaim keys no index row references anymore,
        // then sweep any orphaned files left by interrupted operations.
        if (artifactKeys.Count > 0 || candidates.Count > 0)
        {
            await ReclaimFilesAsync(connection, artifactKeys, cancellationToken);
        }

        return new RetentionPruneResult(deletedRows, prunedTables);
    }

    private sealed record AuditRow(long Id, long ProjectId, bool IsDeleted, DateTimeOffset? Timestamp);

    private static async Task<List<AuditRow>> LoadAuditsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var audits = new List<AuditRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, project_id, is_deleted, COALESCE(finished_at, started_at) FROM audits ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTimeOffset? timestamp = reader.IsDBNull(3)
                ? null
                : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            audits.Add(new AuditRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) != 0, timestamp));
        }

        return audits;
    }

    private static List<long> SelectCandidates(List<AuditRow> audits, RetentionPolicy policy)
    {
        var keepLastOnly = policy.AuditId is null && policy.OlderThan is null && policy.KeepLastAudits is not null;

        var protectedIds = new HashSet<long>();
        if (policy.KeepLastAudits is { } keep)
        {
            foreach (var projectGroup in audits.Where(a => !a.IsDeleted).GroupBy(a => a.ProjectId))
            {
                foreach (var audit in projectGroup
                    .OrderByDescending(a => a.Timestamp ?? DateTimeOffset.MinValue)
                    .ThenByDescending(a => a.Id)
                    .Take(keep))
                {
                    protectedIds.Add(audit.Id);
                }
            }
        }

        var candidates = new List<long>();
        foreach (var audit in audits)
        {
            if (policy.AuditId is { } explicitId)
            {
                if (audit.Id == explicitId)
                {
                    candidates.Add(audit.Id);
                }

                continue;
            }

            if (protectedIds.Contains(audit.Id))
            {
                continue;
            }

            var matchesAge = policy.OlderThan is { } olderThan && audit.Timestamp is { } ts && ts < olderThan;
            if (audit.IsDeleted || matchesAge || keepLastOnly)
            {
                candidates.Add(audit.Id);
            }
        }

        return candidates;
    }

    private static async Task<int> ExecuteDeleteAsync(
        DbConnection connection, DbTransaction transaction, string sql, string table,
        List<(string Name, object Value)> parameters, List<string> prunedTables, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected > 0 && !prunedTables.Contains(table))
        {
            prunedTables.Add(table);
        }

        return affected;
    }

    private async Task ReclaimFilesAsync(DbConnection connection, HashSet<string> touchedKeys, CancellationToken cancellationToken)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT storage_key FROM content_artifacts";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                referenced.Add(reader.GetString(0));
            }
        }

        foreach (var key in touchedKeys)
        {
            if (!referenced.Contains(key))
            {
                await TryDeleteFileAsync(key, cancellationToken);
            }
        }

        // Orphan sweep: files on disk without any index reference.
        foreach (var key in await _files.ListStorageKeysAsync(cancellationToken))
        {
            if (!referenced.Contains(key))
            {
                await TryDeleteFileAsync(key, cancellationToken);
            }
        }
    }

    private async Task TryDeleteFileAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _files.DeleteCompressedAsync(key, cancellationToken);
        }
        catch (IOException)
        {
            // Orphaned files remain reclaimable by the next prune; the database
            // transaction has already committed and must not be failed by file IO.
        }
    }

    private static void AddParameters(DbCommand command, List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
