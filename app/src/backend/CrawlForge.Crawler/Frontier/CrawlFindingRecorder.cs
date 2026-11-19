using System.Data.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Frontier;

/// <summary>
/// Persists crawl-phase finding drafts (sitemap correlation, orphan,
/// broken-link, redirect findings) into the <c>findings</c> table through
/// provider-agnostic parameterized SQL over the locked <see cref="IDbProvider"/>
/// contract — the same accepted pattern as Waf/WafDetectionRecorder.
/// Idempotent per (audit, rule key, URL): re-running a crawl pass never
/// duplicates findings; site-wide findings (null URL) dedup on NULL.
/// Thread-safe through a write gate; intended as a singleton.
/// </summary>
public sealed class CrawlFindingRecorder
{
    private readonly IDbProvider _provider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the recorder over the configured provider.</summary>
    /// <param name="provider">The configured database provider.</param>
    public CrawlFindingRecorder(IDbProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Records finding drafts transactionally, skipping duplicates.
    /// </summary>
    /// <param name="auditId">Owning audit identifier (stamped on every draft).</param>
    /// <param name="findings">Finding drafts (Id 0) produced by the crawl analyzers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of findings newly inserted.</returns>
    public async Task<int> RecordAsync(long auditId, IReadOnlyList<FindingDto> findings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Count == 0)
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var inserted = 0;
            await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (FindingDto finding in findings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ExistsAsync(connection, transaction, auditId, finding, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO findings (audit_id, rule_key, category, severity, url_id, evidence_json, recommendation_key, is_suppressed) " +
                    "VALUES (@audit, @rule, @category, @severity, @url, @evidence, @recommendation, @suppressed)";
                AddParameter(insert, "@audit", auditId);
                AddParameter(insert, "@rule", finding.RuleKey);
                AddParameter(insert, "@category", (int)finding.Category);
                AddParameter(insert, "@severity", (int)finding.Severity);
                AddParameter(insert, "@url", (object?)finding.UrlId ?? DBNull.Value);
                AddParameter(insert, "@evidence", finding.EvidenceJson);
                AddParameter(insert, "@recommendation", (object?)finding.RecommendationKey ?? DBNull.Value);
                AddParameter(insert, "@suppressed", finding.IsSuppressed ? 1 : 0);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                inserted++;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return inserted;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<bool> ExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        long auditId,
        FindingDto finding,
        CancellationToken cancellationToken)
    {
        await using var probe = connection.CreateCommand();
        probe.Transaction = transaction;
        if (finding.UrlId is { } urlId)
        {
            probe.CommandText = "SELECT COUNT(*) FROM findings WHERE audit_id = @audit AND rule_key = @rule AND url_id = @url";
            AddParameter(probe, "@url", urlId);
        }
        else
        {
            probe.CommandText = "SELECT COUNT(*) FROM findings WHERE audit_id = @audit AND rule_key = @rule AND url_id IS NULL";
        }

        AddParameter(probe, "@audit", auditId);
        AddParameter(probe, "@rule", finding.RuleKey);
        var count = Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        return count > 0;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
