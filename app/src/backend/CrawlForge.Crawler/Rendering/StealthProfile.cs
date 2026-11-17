namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// User-enabled stealth posture (§5.6): reduces common automation fingerprints
/// in the rendered page. This is a rendering option, never a WAF-bypass tool
/// (FS-CRAWL-002 §1.2; exclusion E3 — detection and reporting only).
/// </summary>
internal static class StealthProfile
{
    /// <summary>
    /// Script applied before page scripts run. Touches only automation-surface
    /// properties; it does not interact with challenges or tokens.
    /// </summary>
    public const string Script = """
        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
        window.chrome = window.chrome || { runtime: {} };
        Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
        Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
        """;
}
