using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// Offline re-detection from stored response bodies (FS-CRAWL-002 §2.6,
/// acceptance criterion #35): a re-audit re-evaluates the stored body through
/// the compressed content store with the current effective catalogue — fully
/// offline, zero network (exclusions E2/E4). Observed headers recorded in the
/// original detection evidence are reused when present so the two-facet
/// corroboration guard applies identically; without them the re-evaluation is
/// body-only and can at most report a suspected challenge. Detection and
/// reporting only (exclusion E3).
/// </summary>
public sealed class WafStoredBodyRedetector
{
    private readonly IWafChallengeEvaluator _evaluator;
    private readonly IContentStore _contentStore;
    private readonly IDbProvider _provider;
    private readonly WafDetectionPolicy _policy;

    /// <summary>Creates the re-detector over an initialized evaluator and the local stores.</summary>
    /// <param name="evaluator">Evaluator initialized with the current effective catalogue.</param>
    /// <param name="contentStore">Compressed content store holding stored bodies.</param>
    /// <param name="provider">Database provider holding the audit schema.</param>
    /// <param name="policy">Detection policy bounding the stored-body read; null uses the defaults.</param>
    public WafStoredBodyRedetector(
        IWafChallengeEvaluator evaluator,
        IContentStore contentStore,
        IDbProvider provider,
        WafDetectionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(contentStore);
        ArgumentNullException.ThrowIfNull(provider);
        _evaluator = evaluator;
        _contentStore = contentStore;
        _provider = provider;
        _policy = policy ?? WafDetectionPolicy.Default;
    }

    /// <summary>
    /// Re-evaluates one URL from its stored body. Returns null when no body
    /// artifact is stored for the (audit, URL) pair.
    /// </summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="urlId">Owning URL record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fresh verdict, or null when no stored body exists.</returns>
    public async Task<WafEvaluationResult?> RedetectAsync(long auditId, long urlId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        string? storageKey = null;
        string? canonicalUrl = null;
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText =
                "SELECT a.storage_key, u.canonical_url FROM content_artifacts a " +
                "JOIN url_records u ON u.id = a.url_id " +
                "WHERE a.audit_id = @audit AND a.url_id = @url AND a.kind = @kind;";
            probe.Parameters.Add(CreateParameter(probe, "@audit", auditId));
            probe.Parameters.Add(CreateParameter(probe, "@url", urlId));
            probe.Parameters.Add(CreateParameter(probe, "@kind", (int)ContentArtifactKind.Body));
            await using var reader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                storageKey = reader.GetString(0);
                canonicalUrl = reader.GetString(1);
            }
        }

        if (storageKey is null || canonicalUrl is null)
        {
            return null;
        }

        var (headers, statusCode) = await ReadObservedFactsAsync(connection, auditId, urlId, cancellationToken).ConfigureAwait(false);

        await using var stream = await _contentStore.OpenReadAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        var snippet = await ReadBoundedSnippetAsync(stream, cancellationToken).ConfigureAwait(false);
        return _evaluator.Evaluate(new WafEvaluationInput(canonicalUrl, statusCode, headers, snippet));
    }

    private async Task<string> ReadBoundedSnippetAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var buffer = new char[_policy.BodySnippetLimit];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return new string(buffer, 0, totalRead);
    }

    private static async Task<(IReadOnlyDictionary<string, string> Headers, int StatusCode)> ReadObservedFactsAsync(
        DbConnection connection,
        long auditId,
        long urlId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT evidence_json FROM waf_detections WHERE audit_id = @audit AND url_id = @url;";
        command.Parameters.Add(CreateParameter(command, "@audit", auditId));
        command.Parameters.Add(CreateParameter(command, "@url", urlId));
        var evidence = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (evidence is not string evidenceJson)
        {
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), 200);
        }

        try
        {
            using var document = JsonDocument.Parse(evidenceJson);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.TryGetProperty("observedHeaders", out var headersElement)
                && headersElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in headersElement.EnumerateObject())
                {
                    headers[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            var statusCode = document.RootElement.TryGetProperty("statusCode", out var statusElement)
                && statusElement.TryGetInt32(out var status)
                ? status
                : 200;
            return (headers, statusCode);
        }
        catch (JsonException)
        {
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), 200);
        }
    }

    private static DbParameter CreateParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }
}
