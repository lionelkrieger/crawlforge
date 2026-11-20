using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Proxy;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// Canonical writer for <see cref="ContentArtifactKind.NetworkWaterfall"/>
/// content artifacts (CR-006/CR-031). BOTH capture paths persist the identical
/// locked shape so the oversized-image rule reads them uniformly:
/// <list type="bullet">
/// <item>rendered (JS) mode — the CDP-direct waterfall
/// (<see cref="RenderedPage.NetworkWaterfall"/>);</item>
/// <item>http (non-rendered) mode — the loopback proxy's per-subresource
/// captures (<see cref="ProxyCapturedRequestDetail"/>), mapped onto the same
/// <see cref="RenderedNetworkEntry"/> shape.</item>
/// </list>
/// Storage shape: a camelCase JSON array of <see cref="RenderedNetworkEntry"/>
/// (one artifact per URL). WP-10-05's <c>ImageRuleAnalyzer</c> reader accepts
/// the array form (or an object wrapper) case-insensitively and maps
/// <c>Url → TransferredBytes</c> for positive measurements; 0/absence is
/// "not measured", never "pass".
/// </summary>
public static class NetworkWaterfallArtifactWriter
{
    /// <summary>The canonical camelCase serializer shared by both capture paths.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Serializes waterfall entries to the canonical camelCase JSON array.</summary>
    /// <param name="entries">The locked-shape entries.</param>
    /// <returns>UTF-8 JSON array bytes.</returns>
    public static byte[] Serialize(IReadOnlyList<RenderedNetworkEntry> entries) =>
        JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);

    /// <summary>Persists a rendered (CDP-direct) waterfall artifact for one URL.</summary>
    /// <param name="store">The content store.</param>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="urlId">Owning URL record.</param>
    /// <param name="entries">The CDP-direct waterfall entries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted artifact index row.</returns>
    public static Task<ContentArtifactDto> PersistAsync(
        IContentStore store,
        long auditId,
        long urlId,
        IReadOnlyList<RenderedNetworkEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var bytes = Serialize(entries);
        return store.PutAsync(auditId, urlId, ContentArtifactKind.NetworkWaterfall, new MemoryStream(bytes), cancellationToken);
    }

    /// <summary>
    /// Persists http-mode (non-rendered) proxy captures as a NetworkWaterfall
    /// artifact for one URL (CR-031). Each proxy observation is mapped onto the
    /// locked <see cref="RenderedNetworkEntry"/> shape with its counted bytes.
    /// </summary>
    /// <param name="store">The content store.</param>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="urlId">Owning URL record.</param>
    /// <param name="captures">The proxy's detailed per-subresource captures for the URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted artifact index row.</returns>
    public static Task<ContentArtifactDto> PersistProxyCapturesAsync(
        IContentStore store,
        long auditId,
        long urlId,
        IReadOnlyList<ProxyCapturedRequestDetail> captures,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(captures);
        var entries = new List<RenderedNetworkEntry>(captures.Count);
        foreach (var capture in captures)
        {
            entries.Add(capture.ToRenderedNetworkEntry());
        }

        return PersistAsync(store, auditId, urlId, entries, cancellationToken);
    }
}
