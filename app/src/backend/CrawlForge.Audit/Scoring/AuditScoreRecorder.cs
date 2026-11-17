using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.Scoring;

/// <summary>
/// Persists computed scores into <c>audit_scores</c> and refreshes the
/// suppression-aware scoring aggregates in <c>metric_summaries</c>
/// (FS-CRAWL-001 §8.1). Writes use transactional DELETE+INSERT replace
/// semantics so re-audits and suppression changes refresh values without
/// duplicating rows. Only metric keys under the <c>scoring.</c> prefix
/// (<see cref="ScoringMetricKeys"/>) are ever deleted or written — metric rows
/// owned by other areas (for example <c>waf-blocks</c>) are untouched.
/// Provider-agnostic parameterized SQL only.
/// </summary>
public sealed class AuditScoreRecorder
{
    private static readonly JsonSerializerOptions DimensionOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDbProvider _provider;

    /// <summary>Creates the recorder over the configured database provider.</summary>
    /// <param name="provider">Database provider (SQLite or MySQL).</param>
    public AuditScoreRecorder(IDbProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Records score drafts for one audit, replacing prior scores, and rewrites
    /// the scoring-owned metric aggregates consistently with the drafts.
    /// </summary>
    /// <param name="auditId">Owning audit reference.</param>
    /// <param name="drafts">Score drafts; a null category denotes the overall score.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of score rows written.</returns>
    public async Task<int> RecordAsync(
        long auditId,
        IReadOnlyList<AuditScoreDraft> drafts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM audit_scores WHERE audit_id = @auditId";
            AddParameter(delete, "@auditId", auditId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var written = 0;
        foreach (var draft in drafts)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO audit_scores (audit_id, category, score, error_count, warning_count, notice_count, pass_count) " +
                "VALUES (@auditId, @category, @score, @errors, @warnings, @notices, @passes)";
            AddParameter(insert, "@auditId", auditId);
            AddParameter(insert, "@category", draft.Category is { } category ? (int)category : DBNull.Value);
            AddParameter(insert, "@score", draft.Score);
            AddParameter(insert, "@errors", draft.ErrorCount);
            AddParameter(insert, "@warnings", draft.WarningCount);
            AddParameter(insert, "@notices", draft.NoticeCount);
            AddParameter(insert, "@passes", draft.PassCount);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            written++;
        }

        await RewriteMetricsAsync(connection, transaction, auditId, drafts, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return written;
    }

    /// <summary>Reads persisted scores for one audit (category order, overall last).</summary>
    /// <param name="auditId">Owning audit reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Persisted score rows.</returns>
    public async Task<IReadOnlyList<AuditScoreDto>> GetScoresAsync(
        long auditId,
        CancellationToken cancellationToken = default)
    {
        var scores = new List<AuditScoreDto>();
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, category, score, error_count, warning_count, notice_count, pass_count " +
            "FROM audit_scores WHERE audit_id = @auditId";
        AddParameter(command, "@auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            scores.Add(new AuditScoreDto(
                Id: reader.GetInt64(0),
                AuditId: auditId,
                Category: reader.IsDBNull(1) ? null : (AuditCategory)reader.GetInt32(1),
                Score: reader.GetDouble(2),
                ErrorCount: reader.GetInt32(3),
                WarningCount: reader.GetInt32(4),
                NoticeCount: reader.GetInt32(5),
                PassCount: reader.GetInt32(6)));
        }

        return scores
            .OrderBy(s => s.Category is null ? 1 : 0)
            .ThenBy(s => s.Category)
            .ToArray();
    }

    private static async Task RewriteMetricsAsync(
        DbConnection connection,
        DbTransaction transaction,
        long auditId,
        IReadOnlyList<AuditScoreDraft> drafts,
        CancellationToken cancellationToken)
    {
        // Delete ONLY scoring-owned keys; sibling metric rows are never touched.
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            var names = new List<string>(ScoringMetricKeys.All.Count);
            for (var i = 0; i < ScoringMetricKeys.All.Count; i++)
            {
                var name = "@key" + i.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                AddParameter(delete, name, ScoringMetricKeys.All[i]);
            }

            delete.CommandText =
                "DELETE FROM metric_summaries WHERE audit_id = @auditId AND metric_key IN (" +
                string.Join(", ", names) + ")";
            AddParameter(delete, "@auditId", auditId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        // Category drafts carry the non-suppressed counts; the overall draft is
        // their sum and must not be double-counted.
        var categories = drafts.Where(d => d.Category is not null).ToArray();
        var errors = categories.Sum(d => d.ErrorCount);
        var warnings = categories.Sum(d => d.WarningCount);
        var notices = categories.Sum(d => d.NoticeCount);
        var passes = categories.Sum(d => d.PassCount);

        await InsertMetricAsync(connection, transaction, auditId, ScoringMetricKeys.FindingsTotal, "{}",
            errors + warnings + notices + passes, cancellationToken);
        await InsertMetricAsync(connection, transaction, auditId, ScoringMetricKeys.SuppressedTotal, "{}",
            await CountAsync(connection, transaction,
                "SELECT COUNT(*) FROM findings WHERE audit_id = @auditId AND is_suppressed = 1", auditId, cancellationToken),
            cancellationToken);
        await InsertMetricAsync(connection, transaction, auditId, ScoringMetricKeys.UrlsTotal, "{}",
            await CountAsync(connection, transaction,
                "SELECT COUNT(*) FROM url_records WHERE audit_id = @auditId", auditId, cancellationToken),
            cancellationToken);
        await InsertMetricAsync(connection, transaction, auditId, ScoringMetricKeys.UrlsIndexable, "{}",
            await CountAsync(connection, transaction,
                "SELECT COUNT(*) FROM url_records WHERE audit_id = @auditId AND is_indexable = 1", auditId, cancellationToken),
            cancellationToken);

        var severities = new (FindingSeverity Severity, int Count)[]
        {
            (FindingSeverity.Error, errors),
            (FindingSeverity.Warning, warnings),
            (FindingSeverity.Notice, notices),
            (FindingSeverity.Pass, passes),
        };
        foreach (var (severity, count) in severities)
        {
            var dimensions = JsonSerializer.Serialize(
                new Dictionary<string, string> { [ScoringMetricKeys.SeverityDimension] = severity.ToString() },
                DimensionOptions);
            await InsertMetricAsync(connection, transaction, auditId, ScoringMetricKeys.FindingsBySeverity,
                dimensions, count, cancellationToken);
        }

        foreach (var draft in categories)
        {
            var dimensions = JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    [ScoringMetricKeys.CategoryDimension] = draft.Category!.Value.ToString(),
                },
                DimensionOptions);
            await InsertMetricAsync(connection, transaction, auditId, ScoringMetricKeys.FindingsByCategory,
                dimensions, draft.ErrorCount + draft.WarningCount + draft.NoticeCount + draft.PassCount, cancellationToken);
        }
    }

    private static async Task InsertMetricAsync(
        DbConnection connection,
        DbTransaction transaction,
        long auditId,
        string metricKey,
        string dimensionsJson,
        double value,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO metric_summaries (audit_id, metric_key, dimensions_json, metric_value) " +
            "VALUES (@auditId, @key, @dimensions, @value)";
        AddParameter(insert, "@auditId", auditId);
        AddParameter(insert, "@key", metricKey);
        AddParameter(insert, "@dimensions", dimensionsJson);
        AddParameter(insert, "@value", value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> CountAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        long auditId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameter(command, "@auditId", auditId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
