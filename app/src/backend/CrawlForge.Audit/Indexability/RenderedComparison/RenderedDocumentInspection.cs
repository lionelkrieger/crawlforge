using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Indexability.RenderedComparison;

/// <summary>
/// Directive and site-type facts extracted from a captured document for the
/// response-vs-rendered comparison (FS-CRAWL-001 §7.18/§7.19; WP-10-02). Pure
/// functions of the contract DOM model so the comparison re-runs identically
/// from stored artifacts on re-audit (§9.6). The crawl-time adaptive
/// material-change evaluation (Crawler Capture) derives the same facts; the
/// RenderedDocumentConformanceTests prove the two agree on the fixture pages.
/// </summary>
public static partial class RenderedDocumentInspection
{
    /// <summary>Sorted canonical href signature (order-independent comparison).</summary>
    public static string CanonicalSignature(DomDocument? document)
    {
        var parts = HtmlDirectiveExtractor.Extract(document).Canonicals
            .Select(c => c.Href)
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
        return string.Join("\n", parts);
    }

    /// <summary>Sorted meta-robots signature ("agent=directive,directive" per scope).</summary>
    public static string MetaRobotsSignature(DomDocument? document)
    {
        var parts = HtmlDirectiveExtractor.Extract(document).Robots
            .Select(r => r.Agent + "=" + string.Join(",", r.Directives))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        return string.Join("\n", parts);
    }

    /// <summary>Sorted hreflang alternate signature ("hreflang=href" per alternate).</summary>
    public static string HreflangSignature(DomDocument? document)
    {
        var parts = HtmlDirectiveExtractor.Extract(document).HreflangAlternates
            .Select(a => a.Hreflang + "=" + a.Href)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Whether the document carries a noindex directive in any meta-robots scope
    /// ("noindex", or "none" which implies it).
    /// </summary>
    public static bool HasMetaNoindex(DomDocument? document)
    {
        foreach (HtmlRobotsDirective robots in HtmlDirectiveExtractor.Extract(document).Robots)
        {
            if (robots.Directives.Any(d => d is "noindex" or "none"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The raw content attributes of the noindex-bearing meta-robots scopes
    /// (dual-evidence detail for the critical noindex delta).
    /// </summary>
    public static IReadOnlyList<string> NoindexContents(DomDocument? document) =>
        HtmlDirectiveExtractor.Extract(document).Robots
            .Where(r => r.Directives.Any(d => d is "noindex" or "none"))
            .Select(r => r.RawContent)
            .ToList();

    /// <summary>
    /// Whether an HTTP X-Robots-Tag header carries noindex (or "none"). The raw
    /// capture can be noindexed by header alone; the rendered capture has no
    /// HTTP headers, so this fact applies to the raw side only.
    /// </summary>
    public static bool HasHeaderNoindex(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> header in headers)
        {
            if (!string.Equals(header.Key, "x-robots-tag", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string directive in header.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Strip any agent prefix ("googlebot: noindex").
                string token = directive.Contains(':') ? directive.Substring(directive.IndexOf(':') + 1).Trim() : directive;
                if (string.Equals(token, "noindex", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(token, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Service-worker registration fact (§7.19): a script calling
    /// <c>navigator.serviceWorker.register(...)</c>. Renders bypass service
    /// workers; this is a detection fact reported as informational.
    /// </summary>
    /// <param name="document">Captured document (raw or rendered).</param>
    /// <returns>The registration fact, or null when none is observed.</returns>
    public static ServiceWorkerFact? DetectServiceWorker(DomDocument? document)
    {
        foreach (DomNode node in DomTraversal.EnumerateElements(document))
        {
            if (!string.Equals(node.TagName, "script", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = node.TextContent;
            if (string.IsNullOrEmpty(text)
                || !text.Contains("serviceWorker", StringComparison.OrdinalIgnoreCase)
                || !text.Contains("register", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match match = ServiceWorkerRegisterPattern().Match(text);
            return new ServiceWorkerFact(match.Success ? match.Groups[1].Value : null);
        }

        return null;
    }

    [GeneratedRegex(@"serviceWorker\s*\.\s*register\s*\(\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceWorkerRegisterPattern();
}

/// <summary>
/// A detected service-worker registration (§7.19).
/// </summary>
/// <param name="ScriptUrl">The registered script URL when declared literally; null otherwise.</param>
public sealed record ServiceWorkerFact(string? ScriptUrl);
