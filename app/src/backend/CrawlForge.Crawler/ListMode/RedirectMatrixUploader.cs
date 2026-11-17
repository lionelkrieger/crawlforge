using System.Data.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Migration-matrix upload (FS-CRAWL-002 §10.3/§10.7): two-column
/// Source_URL / Expected_Target_URL rows persist in <c>redirect_mappings</c>
/// attached to the audit (unique per audit + source URL, ordinal preserved),
/// and the upload auto-configures a list-only audit seeded from the Source
/// column through the locked <see cref="IListModeIngestor"/>. Persistence is
/// provider-neutral SQL through <see cref="IDbProvider"/>; seeding reuses the
/// §10.2 ingestion pipeline (validation, rewriting, canonicalization, dedup).
/// </summary>
public sealed class RedirectMatrixUploader
{
    private readonly IDbProvider _provider;
    private readonly IListModeIngestor _ingestor;

    /// <summary>Creates the uploader over the locked contracts.</summary>
    /// <param name="provider">Configured database provider.</param>
    /// <param name="ingestor">List Mode ingestor used for list-only auto-seeding.</param>
    public RedirectMatrixUploader(IDbProvider provider, IListModeIngestor ingestor)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(ingestor);
        _provider = provider;
        _ingestor = ingestor;
    }

    /// <summary>
    /// Persists parsed matrix rows and optionally auto-seeds the audit in
    /// list-only mode from the Source column. Rows whose (audit, source URL)
    /// already exists are counted as duplicates and never re-inserted.
    /// </summary>
    /// <param name="auditId">Audit identifier the matrix attaches to.</param>
    /// <param name="parsed">Parsed matrix rows (ordinal order).</param>
    /// <param name="seedListMode">When true, seed the audit list-only from the Source column.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Upload counters plus the optional seeding result.</returns>
    public async Task<MatrixUploadResult> UploadAsync(
        long auditId,
        MatrixParseResult parsed,
        bool seedListMode = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = await ReadExistingSourceUrlsAsync(auditId, cancellationToken).ConfigureAwait(false);
        var persisted = new List<MatrixInputRow>();
        var duplicateCount = 0;

        foreach (var row in parsed.Rows)
        {
            if (!existing.Add(row.SourceUrl))
            {
                duplicateCount++;
                continue;
            }

            persisted.Add(row);
        }

        if (persisted.Count > 0)
        {
            await PersistMappingsAsync(auditId, persisted, cancellationToken).ConfigureAwait(false);
        }

        ListIngestionResult? seeding = null;
        if (seedListMode && persisted.Count > 0)
        {
            // §10.3: the upload auto-configures a list-only audit seeded from
            // the Source column (allowDiscovery=false ⇒ depth 0, discovery off).
            seeding = await _ingestor.IngestAsync(
                auditId,
                persisted.Select(row => row.SourceUrl).ToList(),
                allowDiscovery: false,
                cancellationToken).ConfigureAwait(false);
        }

        return new MatrixUploadResult(persisted.Count, duplicateCount, parsed.InvalidCount, seeding);
    }

    /// <summary>
    /// Reads the persisted matrix rows of an audit in ordinal order
    /// (FS-CRAWL-002 §10.7 results surface input).
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Persisted mappings as locked DTOs.</returns>
    public async Task<IReadOnlyList<Contracts.Entities.RedirectMappingDto>> ReadMappingsAsync(
        long auditId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT `id`, `source_url`, `expected_target_url`, `ordinal` FROM `redirect_mappings` " +
            "WHERE `audit_id` = @auditId ORDER BY `ordinal`";
        AddParameter(command, "@auditId", auditId);

        var rows = new List<Contracts.Entities.RedirectMappingDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new Contracts.Entities.RedirectMappingDto(
                reader.GetInt64(0),
                auditId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return rows;
    }

    private async Task<HashSet<string>> ReadExistingSourceUrlsAsync(long auditId, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT `source_url` FROM `redirect_mappings` WHERE `audit_id` = @auditId";
        AddParameter(command, "@auditId", auditId);

        var urls = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            urls.Add(reader.GetString(0));
        }

        return urls;
    }

    private async Task PersistMappingsAsync(long auditId, List<MatrixInputRow> rows, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO `redirect_mappings` (`audit_id`, `source_url`, `expected_target_url`, `ordinal`) " +
                "VALUES (@auditId, @sourceUrl, @expectedTargetUrl, @ordinal)";
            AddParameter(command, "@auditId", auditId);
            AddParameter(command, "@sourceUrl", row.SourceUrl);
            AddParameter(command, "@expectedTargetUrl", row.ExpectedTargetUrl);
            AddParameter(command, "@ordinal", row.Ordinal);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
