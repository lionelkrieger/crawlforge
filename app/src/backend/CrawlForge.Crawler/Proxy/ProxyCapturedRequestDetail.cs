using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Proxy;

/// <summary>
/// One request observed through the intercepting proxy WITH its transferred
/// byte counts (CR-031; phase-09 notation gap 7). The locked
/// <see cref="ProxyCapturedRequest"/> contract carries no size field; this
/// concrete capture surface records the per-subresource transferred bytes so
/// the oversized-image rule can measure images in http (non-rendered) capture
/// mode. Rendered traffic is measured by the CDP-direct waterfall instead
/// (CR-006 — <c>RenderedNetworkEntry.TransferredBytes</c>).
/// <para>
/// The wiring handoff requests an additive <c>TransferredBytes</c> member on
/// the locked <see cref="ProxyCapturedRequest"/> so the locked drain surface
/// can carry sizes; until then the engine reads sizes through
/// <see cref="LoopbackInterceptingProxy.DrainDetailedCapturedAsync"/>.
/// </para>
/// </summary>
/// <param name="Url">Requested URL.</param>
/// <param name="Method">HTTP method.</param>
/// <param name="StatusCode">Observed status code.</param>
/// <param name="ContentType">Response content type, when present.</param>
/// <param name="CapturedAt">Capture timestamp (UTC).</param>
/// <param name="TransferredBytes">
/// Bytes transferred over the network for this subresource: response head +
/// response body actually relayed from the destination. 0 when nothing was
/// transferred (local block, upstream failure) or the size is opaque
/// (CONNECT tunnel — TLS end-to-end, no per-subresource visibility).
/// </param>
/// <param name="ResponseBodyBytes">Response body bytes relayed (0 when unknown).</param>
public sealed record ProxyCapturedRequestDetail(
    string Url,
    string Method,
    int StatusCode,
    string? ContentType,
    DateTimeOffset CapturedAt,
    long TransferredBytes,
    long ResponseBodyBytes)
{
    /// <summary>Projects onto the locked capture record (size-less contract).</summary>
    /// <returns>The locked-shape record.</returns>
    public ProxyCapturedRequest ToLockedRecord() =>
        new(Url, Method, StatusCode, ContentType, CapturedAt);

    /// <summary>
    /// Maps this proxy observation onto the LOCKED <see cref="RenderedNetworkEntry"/>
    /// shape so http-mode (non-rendered) captures can be persisted as a
    /// <c>ContentArtifactKind.NetworkWaterfall</c> artifact and consumed by the
    /// oversized-image rule (CR-031) exactly like the CDP-direct rendered
    /// waterfall (CR-006).
    /// <para>
    /// Byte-counting convention: <c>TransferredBytes</c> = response head + body
    /// actually relayed (the proxy's counted bytes); <c>EncodedDataLength</c> =
    /// counted response body bytes (Content-Length when present, counted body
    /// otherwise). CONNECT tunnels and local blocks carry 0 → the analyzer
    /// treats 0/absence as "not measured", never "pass". The proxy has no CDP
    /// initiator/timing/cache visibility, so <c>ResourceType</c> is inferred
    /// from the response content type, <c>TimingJson</c> is null, and
    /// <c>FromCache</c>/<c>FromServiceWorker</c> are false.
    /// </para>
    /// </summary>
    /// <returns>The locked-shape waterfall entry.</returns>
    public RenderedNetworkEntry ToRenderedNetworkEntry() =>
        new(
            Url,
            ClassifyResourceType(ContentType),
            Method,
            StatusCode,
            MimeType: ContentType,
            TransferredBytes: TransferredBytes,
            EncodedDataLength: ResponseBodyBytes,
            TimingJson: null,
            FromCache: false,
            FromServiceWorker: false);

    private static string ClassifyResourceType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return "other";
        }

        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (mime.StartsWith("image/", StringComparison.Ordinal))
        {
            return "image";
        }

        return mime switch
        {
            "text/html" or "application/xhtml+xml" => "document",
            "text/css" => "stylesheet",
            "text/javascript" or "application/javascript" or "application/x-javascript" or "text/ecmascript" or "application/ecmascript" => "script",
            "application/json" or "text/json" or "application/ld+json" => "fetch",
            "text/event-stream" => "eventsource",
            _ when mime.StartsWith("font/", StringComparison.Ordinal) || mime is "application/font-woff" or "application/font-woff2" or "application/x-font-ttf" => "font",
            _ when mime.StartsWith("video/", StringComparison.Ordinal) || mime.StartsWith("audio/", StringComparison.Ordinal) => "media",
            _ => "other",
        };
    }
}
