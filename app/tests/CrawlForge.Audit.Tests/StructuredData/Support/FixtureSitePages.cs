namespace CrawlForge.Audit.Tests.StructuredData.Support;

/// <summary>
/// Verbatim fixture-site pages for the WP-09-04 traps, reproduced from
/// <c>app/tests/fixture-site/pages.mjs</c> (CR-015): the <c>page()</c> wrapper,
/// the <c>para(n, seed)</c> word generator, and the exact head/body markup of
/// <c>structured-data-errors</c> and <c>hreflang-no-return</c>. Component tests
/// feed these REAL fixture documents through the production adapters; the
/// phase gate later proves the same traps through the wired product.
/// </summary>
public static class FixtureSitePages
{
    /// <summary>Fixture base URL (expected-results.json base_url).</summary>
    public const string BaseUrl = "http://127.0.0.1:47311";

    /// <summary>Canonical URL of the structured-data-errors trap page.</summary>
    public const string StructuredDataUrl = BaseUrl + "/pages/structured-data.html";

    /// <summary>Canonical URL of the hreflang English source page.</summary>
    public const string HreflangEnglishUrl = BaseUrl + "/pages/hreflang-en.html";

    /// <summary>Canonical URL of the hreflang French target page (no return tag).</summary>
    public const string HreflangFrenchUrl = BaseUrl + "/pages/hreflang-fr.html";

    /// <summary>pages.mjs page() wrapper (verbatim shape).</summary>
    public static string Page(string title, string head, string body, string lang = "en") =>
        $"<!doctype html><html lang=\"{lang}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>{title}</title>{head}</head><body>{body}</body></html>";

    /// <summary>pages.mjs para(n, seed): n words cycling seed + (i % 17).</summary>
    public static string Para(int n, string seed) =>
        $"<p>{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{seed}{i % 17}"))}</p>";

    /// <summary>
    /// structured-data-errors trap (pages.mjs '/pages/structured-data.html'):
    /// a Product JSON-LD block missing name/image/offers, followed by a
    /// malformed Organization JSON-LD block (unterminated object).
    /// </summary>
    public static string StructuredDataErrors => Page(
        "Structured data errors",
        """
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"Product","description":"Missing name and offers"}</script>
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"Organization", "name": "Broken block"</script>
        """,
        $"<h1>Structured data errors</h1>{Para(150, "sd")}");

    /// <summary>
    /// hreflang-no-return source page (pages.mjs '/pages/hreflang-en.html'):
    /// declares en self-reference + fr alternate; the French page never links back.
    /// </summary>
    public static string HreflangEnglish => Page(
        "Hreflang English",
        """<link rel="alternate" hreflang="en" href="/pages/hreflang-en.html"><link rel="alternate" hreflang="fr" href="/pages/hreflang-fr.html">""",
        $"""<h1>English</h1>{Para(150, "en")}<a href="/pages/hreflang-fr.html">Français</a>""");

    /// <summary>
    /// hreflang-no-return target page (pages.mjs '/pages/hreflang-fr.html'):
    /// French content with NO hreflang annotations at all.
    /// </summary>
    public static string HreflangFrench => Page(
        "Hreflang French",
        "",
        $"<h1>Français</h1>{Para(150, "fr")}",
        lang: "fr");
}
