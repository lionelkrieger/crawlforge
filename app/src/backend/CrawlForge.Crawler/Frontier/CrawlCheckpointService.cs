using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Frontier;

/// <summary>
/// Persists crawl resume state into <c>crawl_checkpoints</c> (FS-CRAWL-001 §5.7;
/// schema catalog) using provider-agnostic parameterized SQL over the locked
/// <see cref="IDbProvider"/> contract. Checkpoints are appended transactionally on
/// pause and periodically during runs; the latest row per audit wins on resume.
/// The additive <c>aimd_state_json</c> payload column is carried verbatim — its
/// content is owned by the politeness controller contract.
/// </summary>
public sealed class CrawlCheckpointService : ICrawlCheckpointService
{
    private readonly IDbProvider _provider;

    /// <summary>Creates the checkpoint service over the configured provider.</summary>
    /// <param name="provider">The configured database provider.</param>
    public CrawlCheckpointService(IDbProvider provider)
    {
        _provider = provider;
    }

    /// <inheritdoc />
    public async Task SaveAsync(CrawlCheckpointDto checkpoint, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO crawl_checkpoints (audit_id, serialized_frontier, seen_set_reference, counters_json, saved_at, aimd_state_json) " +
            "VALUES (@audit, @frontier, @seen, @counters, @saved, @aimd)";
        AddParameter(command, "@audit", checkpoint.AuditId);
        AddParameter(command, "@frontier", checkpoint.SerializedFrontier);
        AddParameter(command, "@seen", (object?)checkpoint.SeenSetReference ?? DBNull.Value);
        AddParameter(command, "@counters", checkpoint.CountersJson);
        AddParameter(command, "@saved", checkpoint.SavedAt.ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "@aimd", (object?)checkpoint.AimdStateJson ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CrawlCheckpointDto?> LoadLatestAsync(long auditId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, audit_id, serialized_frontier, seen_set_reference, counters_json, saved_at, aimd_state_json " +
            "FROM crawl_checkpoints WHERE audit_id = @audit ORDER BY saved_at DESC, id DESC LIMIT 1";
        AddParameter(command, "@audit", auditId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CrawlCheckpointDto(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
        {
            AimdStateJson = reader.IsDBNull(6) ? null : reader.GetString(6)
        };
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
