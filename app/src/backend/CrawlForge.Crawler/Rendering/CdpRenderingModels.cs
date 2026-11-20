using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// One service-worker registration observed during a render (§7.19). Renders
/// BYPASS service workers on every page; the registration fact is exposed so
/// the capture layer can emit the informational
/// <c>render.service-worker-registered</c> finding (WP-10-02).
/// </summary>
/// <param name="ScopeUrl">Registration scope URL.</param>
/// <param name="ScriptUrl">Registered script URL, when observed.</param>
public sealed record ServiceWorkerRegistrationFact(string ScopeUrl, string? ScriptUrl);

/// <summary>The captured artifacts of one rendered page.</summary>
/// <param name="FinalUrl">Final URL after navigation (client redirects included).</param>
/// <param name="Html">Post-render DOM HTML.</param>
/// <param name="Screenshot">PNG screenshot bytes.</param>
public sealed record PageCapture(string FinalUrl, string Html, byte[] Screenshot);

/// <summary>
/// The detailed result of one render through the direct-CDP pool: the locked
/// <see cref="RenderedPage"/> plus the observability facts the locked contract
/// does not yet carry (service-worker registrations, browser identity). The
/// wiring handoff requests additive <c>RenderedPage</c> members for these;
/// until then this concrete surface exposes them without a contract change.
/// </summary>
/// <param name="Page">The locked render result (waterfall included).</param>
/// <param name="ServiceWorkerRegistrations">Registrations observed (renders bypass them).</param>
/// <param name="Browser">The resolved local browser identity used for the render.</param>
public sealed record CdpRenderResult(
    RenderedPage Page,
    IReadOnlyList<ServiceWorkerRegistrationFact> ServiceWorkerRegistrations,
    ResolvedBrowser Browser);

/// <summary>
/// Resource-blocking profiles for the rendering control layer (§5.6.2). The
/// DEFAULT profile blocks nothing so Core Web Vitals and tag checks are not
/// skewed; the opt-in "fast" profile blocks selected resource classes (the
/// spec's example: fonts and media). Blocking is enforced by the CDP Fetch
/// domain (BlockedByClient) — zero outbound for blocked requests.
/// </summary>
public static class ResourceBlockingProfiles
{
    /// <summary>Default profile: block nothing.</summary>
    public static IReadOnlyList<string> None { get; } = [];

    /// <summary>Opt-in fast profile: block fonts and media (§5.6.2 example).</summary>
    public static IReadOnlyList<string> Fast { get; } = ["Font", "Media"];
}
