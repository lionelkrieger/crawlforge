namespace CrawlForge.Contracts.Crawl;

/// <summary>
/// Headless Chromium rendering pool (FS-CRAWL-001 §5.6) with device profiles,
/// optional stealth profile, infinite-scroll handling, and console capture.
/// The stealth profile is a user-configured rendering option, never a
/// WAF-bypass tool (FS-CRAWL-002 §1.2).
/// </summary>
public interface IRenderingPool
{
    /// <summary>Renders a URL and returns the rendered document and artifacts.</summary>
    /// <param name="request">Render request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered page.</returns>
    Task<RenderedPage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A rendering request.
/// </summary>
/// <param name="Url">Target URL.</param>
/// <param name="Device">Device profile to emulate.</param>
/// <param name="InfiniteScroll">Whether to drive infinite-scroll loading.</param>
/// <param name="CaptureConsole">Whether to capture console messages.</param>
/// <param name="Stealth">Whether the user-enabled stealth profile is applied.</param>
/// <param name="TimeoutSeconds">Optional render timeout.</param>
public sealed record RenderRequest(
    string Url,
    DeviceProfile Device,
    bool InfiniteScroll = false,
    bool CaptureConsole = true,
    bool Stealth = false,
    int? TimeoutSeconds = null);

/// <summary>
/// A device emulation profile.
/// </summary>
/// <param name="Name">Profile name.</param>
/// <param name="ViewportWidth">Viewport width in pixels.</param>
/// <param name="ViewportHeight">Viewport height in pixels.</param>
/// <param name="UserAgent">User-agent string.</param>
/// <param name="IsMobile">Whether the profile is a mobile device.</param>
public sealed record DeviceProfile(
    string Name,
    int ViewportWidth,
    int ViewportHeight,
    string UserAgent,
    bool IsMobile);

/// <summary>
/// A rendered page with artifacts and the post-render WAF verdict
/// (additive render-test output, FS-CRAWL-002 Appendix B). Amendment WP-10-A1
/// (D-021) appends the captured network waterfall (FS-CRAWL-001 §5.6.2): the
/// rendering control layer captures rendered traffic directly, and the
/// waterfall is built from that capture (CR-006).
/// </summary>
/// <param name="FinalUrl">Final URL after navigation.</param>
/// <param name="Html">Rendered DOM HTML.</param>
/// <param name="Screenshot">Screenshot bytes.</param>
/// <param name="ConsoleMessages">Captured console messages.</param>
/// <param name="PostRenderWafVerdict">WAF verdict after rendering, when evaluated.</param>
public sealed record RenderedPage(
    string FinalUrl,
    string Html,
    ReadOnlyMemory<byte> Screenshot,
    IReadOnlyList<ConsoleMessage> ConsoleMessages,
    WafEvaluationResult? PostRenderWafVerdict)
{
    /// <summary>Additive (WP-10-A1, D-021): network waterfall captured by the
    /// rendering control layer during this render (FS-CRAWL-001 §5.6.2).
    /// Null means no waterfall was captured (baseline renders); an empty list
    /// means a capture ran and observed no requests.</summary>
    [AdditiveMember]
    public IReadOnlyList<RenderedNetworkEntry>? NetworkWaterfall { get; init; }
}

/// <summary>
/// One request/response entry of a rendered-capture network waterfall
/// (FS-CRAWL-001 §5.6.2; amendment WP-10-A1, D-021). Fields mirror the
/// observable CDP network facts; transferredBytes feeds the CR-031 rendered
/// image-size measurement. Storage kind:
/// <see cref="Enums.ContentArtifactKind.NetworkWaterfall"/>.
/// </summary>
/// <param name="Url">Request URL.</param>
/// <param name="ResourceType">Resource type label (document, script, image, ...).</param>
/// <param name="Method">HTTP request method.</param>
/// <param name="StatusCode">HTTP response status; 0 when no HTTP response exists
/// (for example cache-only or data-URI entries).</param>
/// <param name="MimeType">Response MIME type, when observed.</param>
/// <param name="TransferredBytes">Bytes transferred over the network for this
/// entry (0 when unknown); CR-031 rendered image payloads read this field.</param>
/// <param name="EncodedDataLength">Encoded (compressed) body length in bytes
/// (0 when unknown).</param>
/// <param name="TimingJson">Resource-timing payload (request start, TTFB, end),
/// when observed.</param>
/// <param name="FromCache">Whether the entry was served from the browser cache.</param>
/// <param name="FromServiceWorker">Whether the entry was served by a service
/// worker (renders bypass service workers; §7.19).</param>
public sealed record RenderedNetworkEntry(
    string Url,
    string ResourceType,
    string Method,
    int StatusCode,
    string? MimeType = null,
    long TransferredBytes = 0,
    long EncodedDataLength = 0,
    string? TimingJson = null,
    bool FromCache = false,
    bool FromServiceWorker = false);

/// <summary>
/// One captured browser console message.
/// </summary>
/// <param name="Level">Console level (log, warn, error, ...).</param>
/// <param name="Text">Message text.</param>
/// <param name="Timestamp">Capture timestamp (UTC).</param>
public sealed record ConsoleMessage(string Level, string Text, DateTimeOffset Timestamp);
