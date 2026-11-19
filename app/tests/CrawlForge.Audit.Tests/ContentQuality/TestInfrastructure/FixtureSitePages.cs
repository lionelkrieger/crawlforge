namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// EXACT fixture-site markup for regression tests (WP-09-05): mirrored
/// verbatim from <c>app/tests/fixture-site/pages.mjs</c> (read-only source of
/// truth, CR-015) so component regressions run against the real trap HTML the
/// acceptance harness serves. The <c>page()</c> wrapper and <c>words()/para()</c>
/// generators reproduce pages.mjs lines 4–8 byte-for-byte.
/// </summary>
internal static class FixtureSitePages
{
    public const string BaseUrl = "http://127.0.0.1:47311";

    /// <summary>pages.mjs line 4: the page() wrapper.</summary>
    public static string Page(string title, string head = "", string body = "", string lang = "en") =>
        $"<!doctype html><html lang=\"{lang}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>{title}</title>{head}</head><body>{body}</body></html>";

    /// <summary>pages.mjs line 7: words(n, seed) — seed + (i % 17), space-joined.</summary>
    public static string Words(int n, string seed) =>
        string.Join(" ", Enumerable.Range(0, n).Select(i => $"{seed}{i % 17}"));

    /// <summary>pages.mjs line 8: para(n, seed).</summary>
    public static string Para(int n, string seed) => $"<p>{Words(n, seed)}</p>";

    /// <summary>pages.mjs line 109 — trap "thin-page": 6 visible words.</summary>
    public static string ThinHtml => Page("Thin page", body: "<h1>Thin</h1><p>Only a few words here.</p>");

    /// <summary>pages.mjs line 81 — trap "soft-404-server": 200 OK error-like page.</summary>
    public static string Soft404Html => Page(
        "Page not found",
        body: "<h1>Page not found</h1><p>Sorry, the page you are looking for does not exist.</p>");

    /// <summary>pages.mjs lines 48–52 — trap "canonical-conflict" (HTML side; the
    /// server adds Link: &lt;/pages/canonical-target-header.html&gt;; rel="canonical", server.mjs line 62).</summary>
    public static string CanonicalConflictHtml => Page(
        "Canonical conflict",
        head: "<link rel=\"canonical\" href=\"/pages/canonical-target-html.html\">",
        body: $"<h1>Canonical conflict</h1>{Para(150, "canon")}");

    /// <summary>server.mjs line 62 — the Link header served with canonical-conflict.html.</summary>
    public const string CanonicalConflictLinkHeader = "</pages/canonical-target-header.html>; rel=\"canonical\"";

    /// <summary>pages.mjs line 46 — trap "noindex" (CR-024 / G-NOINDEX-FLAG-QUESTION).</summary>
    public static string NoindexHtml => Page(
        "Noindex page",
        head: "<meta name=\"robots\" content=\"noindex, follow\">",
        body: $"<h1>Not for the index</h1>{Para(150, "noidx")}");

    /// <summary>pages.mjs line 107 — trap "duplicate-titles" page A.</summary>
    public static string DupTitleAHtml => Page(
        "Duplicate title",
        body: $"<h1>Duplicate A</h1>{Para(150, "dupa")}");

    /// <summary>pages.mjs line 108 — trap "duplicate-titles" page B.</summary>
    public static string DupTitleBHtml => Page(
        "Duplicate title",
        body: $"<h1>Duplicate B</h1>{Para(150, "dupb")}");

    /// <summary>pages.mjs lines 93–96 — trap "missing-alt" (image inventory facts).</summary>
    public static string ImagesHtml => Page(
        "Images without alt",
        body: "<h1>Images</h1><img src=\"/assets/logo.png\"><img src=\"/assets/banner.jpg\" alt=\"Banner\"><p>Images above: one missing alt, one with alt.</p>");

    /// <summary>pages.mjs line 45 — a healthy 150-word page (redirect target).</summary>
    public static string RedirectTargetHtml => Page(
        "Redirect chain target",
        body: $"<h1>Redirect target</h1>{Para(150, "target")}");
}
