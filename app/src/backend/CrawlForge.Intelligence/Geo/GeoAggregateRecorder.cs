using System.Data.Common;
using System.Text.Json;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.Geo;

/// <summary>
/// Writes the GEO dashboard aggregates (FS-CRAWL-002 §13.4) into
/// <c>metric_summaries</c>: <c>geo-readiness</c> (dimension: check, plus an
/// overall row) and <c>governance-coverage</c> (dimension: purpose, plus an
/// overall row — the share of matrix cells with an explicit verdict).
/// Replace semantics are scoped EXCLUSIVELY to these two locked metric keys;
/// sibling metric rows are never touched. Provider-agnostic parameterized SQL.
/// </summary>
public sealed class GeoAggregateRecorder
{
    private static readonly JsonSerializerOptions DimensionOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDbProvider _provider;

    /// <summary>Creates the recorder over the configured provider.</summary>
    /// <param name="provider">Database provider (SQLite or MySQL).</param>
    public GeoAggregateRecorder(IDbProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>Records readiness check scores (0–1) plus the overall score (0–100).</summary>
    /// <param name="auditId">Owning audit reference.</param>
    /// <param name="checks">Per-check scores.</param>
    /// <param name="overallScore">Overall readiness score (0–100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of metric rows written.</returns>
    public async Task<int> RecordReadinessAsync(
        long auditId,
        IReadOnlyList<GeoCheckScore> checks,
        double overallScore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checks);
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await DeleteKeyAsync(connection, transaction, auditId, MetricKeys.GeoReadiness, cancellationToken);
        var written = 0;
        foreach (var check in checks)
        {
            await InsertAsync(connection, transaction, auditId, MetricKeys.GeoReadiness,
                JsonSerializer.Serialize(new { check = check.Check }, DimensionOptions),
                check.Score, cancellationToken);
            written++;
        }

        await InsertAsync(connection, transaction, auditId, MetricKeys.GeoReadiness,
            JsonSerializer.Serialize(new { check = "overall" }, DimensionOptions),
            overallScore, cancellationToken);
        written++;

        await transaction.CommitAsync(cancellationToken);
        return written;
    }

    /// <summary>
    /// Records governance coverage: the share of matrix cells carrying an
    /// explicit (Allow/Deny) verdict, per purpose and overall.
    /// </summary>
    /// <param name="auditId">Owning audit reference.</param>
    /// <param name="rows">Persisted matrix rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of metric rows written.</returns>
    public async Task<int> RecordGovernanceCoverageAsync(
        long auditId,
        IReadOnlyList<GovernanceMatrixRowDto> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await DeleteKeyAsync(connection, transaction, auditId, MetricKeys.GovernanceCoverage, cancellationToken);
        var written = 0;
        foreach (var purpose in new[] { AiAgentPurpose.Search, AiAgentPurpose.Rag, AiAgentPurpose.Training })
        {
            var cells = rows.Where(r => r.Purpose == purpose).ToArray();
            var coverage = cells.Length > 0
                ? (double)cells.Count(c => c.Verdict != GovernanceVerdict.Unspecified) / cells.Length
                : 0;
            await InsertAsync(connection, transaction, auditId, MetricKeys.GovernanceCoverage,
                JsonSerializer.Serialize(new { purpose = purpose.ToString() }, DimensionOptions),
                Math.Round(coverage, 4), cancellationToken);
            written++;
        }

        var overall = rows.Count > 0
            ? (double)rows.Count(r => r.Verdict != GovernanceVerdict.Unspecified) / rows.Count
            : 0;
        await InsertAsync(connection, transaction, auditId, MetricKeys.GovernanceCoverage,
            JsonSerializer.Serialize(new { purpose = "overall" }, DimensionOptions),
            Math.Round(overall, 4), cancellationToken);
        written++;

        await transaction.CommitAsync(cancellationToken);
        return written;
    }

    private static async Task DeleteKeyAsync(
        DbConnection connection,
        DbTransaction transaction,
        long auditId,
        string metricKey,
        CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM metric_summaries WHERE audit_id = @auditId AND metric_key = @key";
        AddParameter(delete, "@auditId", auditId);
        AddParameter(delete, "@key", metricKey);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAsync(
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

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
