using System.Data.Common;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Rebuilds a <see cref="PageAuditContext"/> offline from persisted facts so
/// every structured-data rule re-runs on re-audit without refetching
/// (FS-CRAWL-001 §9.6): the stored body artifact from the content store, the
/// URL record, the frozen configuration snapshot, and optionally recorded
/// response headers. The fetch envelope is synthetic (zero timings) and carries
/// the stored body plus headers; no network access occurs.
/// </summary>
public sealed class OfflinePageContextBuilder
{
    /// <summary>Bounded stored-body read cap (scale invariant: bounded memory).</summary>
    public const int MaxStoredBodyBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IDbProvider _provider;
    private readonly IContentStore _contentStore;
    private readonly ITolerantDomParser _parser;

    /// <summary>Creates the builder over the persistence stack.</summary>
    /// <param name="provider">Database provider.</param>
    /// <param name="contentStore">Compressed content-artifact store.</param>
    /// <param name="parser">Tolerant DOM parser.</param>
    public OfflinePageContextBuilder(IDbProvider provider, IContentStore contentStore, ITolerantDomParser parser)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _contentStore = contentStore ?? throw new ArgumentNullException(nameof(contentStore));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    /// <summary>
    /// Builds the offline context for one stored URL, or null when the URL has
    /// no stored body artifact.
    /// </summary>
    /// <param name="auditId">Owning audit reference.</param>
    /// <param name="urlId">URL record reference.</param>
    /// <param name="headers">Recorded response headers, when available.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reconstructed context, or null without a stored body.</returns>
    public async Task<PageAuditContext?> BuildAsync(
        long auditId,
        long urlId,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var url = await ReadUrlRecordAsync(auditId, urlId, cancellationToken);
        if (url is null)
        {
            return null;
        }

        var storageKey = await ReadBodyStorageKeyAsync(auditId, urlId, cancellationToken);
        if (storageKey is null)
        {
            return null;
        }

        await using var stream = await _contentStore.OpenReadAsync(storageKey);
        if (stream is null)
        {
            return null;
        }

        var body = await ReadBoundedAsync(stream, cancellationToken);
        var markup = System.Text.Encoding.UTF8.GetString(body);
        var document = _parser.Parse(markup, url.CanonicalUrl);
        var configuration = await ReadConfigurationSnapshotAsync(auditId, cancellationToken);

        var fetch = new HttpFetchResult(
            url.CanonicalUrl,
            url.FinalStatusCode ?? 200,
            headers ?? new Dictionary<string, string>(),
            body,
            TimeSpan.Zero,
            TimeSpan.Zero);

        return new PageAuditContext(auditId, url, configuration, fetch, document, null, null, null);
    }

    private async Task<UrlRecordDto?> ReadUrlRecordAsync(long auditId, long urlId, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT canonical_url, host, path, depth, discovery_source, status_class, final_status_code, " +
            "redirect_target_url, content_type, size_bytes, timing_json, robots_state, indexability_flags, is_indexable " +
            "FROM url_records WHERE id = @urlId AND audit_id = @auditId";
        AddParameter(command, "@urlId", urlId);
        AddParameter(command, "@auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new UrlRecordDto(
            urlId,
            auditId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            (DiscoverySource)reader.GetInt64(4),
            (CrawlStatusClass)reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            (RobotsState)reader.GetInt64(11),
            (IndexabilityFlags)reader.GetInt64(12),
            reader.GetInt64(13) != 0);
    }

    private async Task<string?> ReadBodyStorageKeyAsync(long auditId, long urlId, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT storage_key FROM content_artifacts WHERE audit_id = @auditId AND url_id = @urlId AND kind = @kind";
        AddParameter(command, "@auditId", auditId);
        AddParameter(command, "@urlId", urlId);
        AddParameter(command, "@kind", (long)ContentArtifactKind.Body);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    private async Task<CrawlConfigurationDto> ReadConfigurationSnapshotAsync(long auditId, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT configuration_snapshot_json FROM audits WHERE id = @auditId";
        AddParameter(command, "@auditId", auditId);
        var result = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (!string.IsNullOrWhiteSpace(result))
        {
            var snapshot = JsonSerializer.Deserialize<CrawlConfigurationDto>(result, SnapshotJsonOptions);
            if (snapshot is not null)
            {
                return snapshot;
            }
        }

        return new CrawlConfigurationDto(
            0, 0, "offline", "[]", null, null, null, RenderMode.None,
            null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;
        int read;
        while (total < MaxStoredBodyBytes &&
               (read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaxStoredBodyBytes - total)), cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            total += read;
        }

        return buffer.ToArray();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
