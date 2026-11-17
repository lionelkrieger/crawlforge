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
/// (additive render-test output, FS-CRAWL-002 Appendix B).
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
    WafEvaluationResult? PostRenderWafVerdict);

/// <summary>
/// One captured browser console message.
/// </summary>
/// <param name="Level">Console level (log, warn, error, ...).</param>
/// <param name="Text">Message text.</param>
/// <param name="Timestamp">Capture timestamp (UTC).</param>
public sealed record ConsoleMessage(string Level, string Text, DateTimeOffset Timestamp);
