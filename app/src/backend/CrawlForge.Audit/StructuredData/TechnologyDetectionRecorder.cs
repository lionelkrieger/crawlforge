using System.Data.Common;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Persists technology detections into <c>technology_detections</c> (schema
/// catalog: audit reference, URL reference, technology key, category,
/// confidence). Upserts are idempotent per (audit, URL, technology key) so
/// offline re-audits refresh confidence without duplicating rows. Provider-
/// agnostic parameterized SQL only; the batch is transactional.
/// </summary>
public sealed class TechnologyDetectionRecorder
{
    private readonly IDbProvider _provider;

    /// <summary>Creates the recorder over the configured database provider.</summary>
    /// <param name="provider">Database provider (SQLite or MySQL).</param>
    public TechnologyDetectionRecorder(IDbProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Records detections for one URL, replacing prior confidence values for
    /// the same technology keys and removing keys no longer detected.
    /// </summary>
    /// <param name="auditId">Owning audit reference.</param>
    /// <param name="urlId">Owning URL reference.</param>
    /// <param name="detections">Detection drafts from the detector.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of detection rows written.</returns>
    public async Task<int> RecordAsync(
        long auditId,
        long urlId,
        IReadOnlyList<Contracts.Audit.TechnologyDetectionDraft> detections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detections);
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Re-audit semantics: the fresh detection set fully replaces the prior
        // set for this (audit, URL) pair.
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM technology_detections WHERE audit_id = @auditId AND url_id = @urlId";
            AddParameter(delete, "@auditId", auditId);
            AddParameter(delete, "@urlId", urlId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var written = 0;
        foreach (var detection in detections)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO technology_detections (audit_id, url_id, technology_key, category, confidence) " +
                "VALUES (@auditId, @urlId, @key, @category, @confidence)";
            AddParameter(insert, "@auditId", auditId);
            AddParameter(insert, "@urlId", urlId);
            AddParameter(insert, "@key", detection.TechnologyKey);
            AddParameter(insert, "@category", detection.Category);
            AddParameter(insert, "@confidence", detection.Confidence);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            written++;
        }

        await transaction.CommitAsync(cancellationToken);
        return written;
    }

    /// <summary>Reads persisted detections for one URL in key order.</summary>
    /// <param name="auditId">Owning audit reference.</param>
    /// <param name="urlId">Owning URL reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Persisted detections as (key, category, confidence) tuples.</returns>
    public async Task<IReadOnlyList<(string Key, string Category, double Confidence)>> GetDetectionsAsync(
        long auditId, long urlId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT technology_key, category, confidence FROM technology_detections " +
            "WHERE audit_id = @auditId AND url_id = @urlId ORDER BY technology_key";
        AddParameter(command, "@auditId", auditId);
        AddParameter(command, "@urlId", urlId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<(string, string, double)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));
        }

        return results;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
