using System.Collections.Concurrent;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Content-hash embedding cache (FS-CRAWL-002 §12.3): re-audits reuse stored
/// vectors for unchanged content. Two layers: an in-job memory cache keyed by
/// (content hash, model tag) deduplicating identical content across URLs, and
/// stored-vector reuse from url_embeddings when the persisted payload's
/// content hash and model tag both match the current artifact. A model-tag
/// change never reuses vectors (§12.3 prohibits mixing tags within an audit).
/// </summary>
public sealed class EmbeddingCache
{
    private readonly ConcurrentDictionary<(string ContentHash, string ModelTag), float[]> _memory = new();

    /// <summary>Number of distinct cached vectors in this job.</summary>
    public int Count => _memory.Count;

    /// <summary>Looks up a vector by content hash and model tag.</summary>
    /// <param name="contentHash">Current artifact content hash.</param>
    /// <param name="modelTag">Active model tag.</param>
    /// <param name="vector">Cached vector when present.</param>
    /// <returns>True on a cache hit.</returns>
    public bool TryGet(string contentHash, string modelTag, out float[] vector)
    {
        if (_memory.TryGetValue((contentHash, modelTag), out var cached))
        {
            vector = cached;
            return true;
        }

        vector = [];
        return false;
    }

    /// <summary>Stores a vector under its content hash and model tag.</summary>
    /// <param name="contentHash">Artifact content hash.</param>
    /// <param name="modelTag">Model tag that produced the vector.</param>
    /// <param name="vector">Vector components.</param>
    public void Store(string contentHash, string modelTag, float[] vector) =>
        _memory[(contentHash, modelTag)] = vector;

    /// <summary>
    /// Attempts stored-vector reuse: the existing url_embeddings row for the
    /// URL is reusable only when its payload content hash equals the current
    /// artifact hash AND its model tag equals the active tag.
    /// </summary>
    /// <param name="embeddings">url_embeddings read repository.</param>
    /// <param name="auditId">Audit reference.</param>
    /// <param name="urlId">URL reference.</param>
    /// <param name="contentHash">Current artifact content hash.</param>
    /// <param name="modelTag">Active model tag.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reusable stored row, or null when re-embedding is required.</returns>
    public static async Task<UrlEmbeddingDto?> TryReuseStoredAsync(
        IReadRepository<UrlEmbeddingDto> embeddings,
        long auditId,
        long urlId,
        string contentHash,
        string modelTag,
        CancellationToken cancellationToken = default)
    {
        var page = await embeddings.ListAsync(new ListQuery(
            Offset: 0,
            Limit: 1,
            Filters: new Dictionary<string, string>
            {
                ["AuditId"] = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["UrlId"] = urlId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }), cancellationToken);

        var row = page.Items.FirstOrDefault();
        if (row is null || !string.Equals(row.ModelTag, modelTag, StringComparison.Ordinal))
        {
            return null;
        }

        var payload = EmbeddingPayload.TryParse(row.EmbeddingPayloadJson);
        if (payload is null || !string.Equals(payload.ContentHash, contentHash, StringComparison.Ordinal))
        {
            return null;
        }

        return row;
    }
}
