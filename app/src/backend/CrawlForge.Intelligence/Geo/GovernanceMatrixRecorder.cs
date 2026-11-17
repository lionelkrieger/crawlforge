using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.Geo;

/// <summary>
/// Persists governance matrix rows into <c>governance_matrix</c> (schema
/// catalog: audit reference, agent name, scope value, purpose, verdict,
/// evidence). Replace semantics per audit keep re-evaluation idempotent.
/// Provider-agnostic parameterized SQL; the batch is transactional.
/// ADVISORY data only — this recorder never touches robots.txt or crawl state.
/// </summary>
public sealed class GovernanceMatrixRecorder
{
    private readonly IDbProvider _provider;

    /// <summary>Creates the recorder over the configured provider.</summary>
    /// <param name="provider">Database provider (SQLite or MySQL).</param>
    public GovernanceMatrixRecorder(IDbProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>Records matrix rows for one audit, replacing the prior set.</summary>
    /// <param name="auditId">Owning audit reference.</param>
    /// <param name="rows">Matrix rows from the evaluator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    public async Task<int> RecordAsync(
        long auditId,
        IReadOnlyList<GovernanceMatrixRowDto> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM governance_matrix WHERE audit_id = @auditId";
            AddParameter(delete, "@auditId", auditId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var written = 0;
        foreach (var row in rows)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO governance_matrix (audit_id, agent_name, scope_value, purpose, verdict, evidence_json) " +
                "VALUES (@auditId, @agent, @scope, @purpose, @verdict, @evidence)";
            AddParameter(insert, "@auditId", auditId);
            AddParameter(insert, "@agent", row.AgentName);
            AddParameter(insert, "@scope", row.ScopeValue);
            AddParameter(insert, "@purpose", (int)row.Purpose);
            AddParameter(insert, "@verdict", (int)row.Verdict);
            AddParameter(insert, "@evidence", row.EvidenceJson);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            written++;
        }

        await transaction.CommitAsync(cancellationToken);
        return written;
    }

    /// <summary>Reads persisted matrix rows for one audit (agent, scope, purpose order).</summary>
    /// <param name="auditId">Owning audit reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Persisted matrix rows.</returns>
    public async Task<IReadOnlyList<GovernanceMatrixRowDto>> GetRowsAsync(
        long auditId,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<GovernanceMatrixRowDto>();
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, agent_name, scope_value, purpose, verdict, evidence_json " +
            "FROM governance_matrix WHERE audit_id = @auditId";
        AddParameter(command, "@auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new GovernanceMatrixRowDto(
                Id: reader.GetInt64(0),
                AuditId: auditId,
                AgentName: reader.GetString(1),
                ScopeValue: reader.GetString(2),
                Purpose: (AiAgentPurpose)reader.GetInt32(3),
                Verdict: (GovernanceVerdict)reader.GetInt32(4),
                EvidenceJson: reader.GetString(5)));
        }

        return rows
            .OrderBy(r => r.AgentName, StringComparer.Ordinal)
            .ThenBy(r => r.ScopeValue, StringComparer.Ordinal)
            .ThenBy(r => r.Purpose)
            .ToArray();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
