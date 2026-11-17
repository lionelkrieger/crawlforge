using System.Text;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Intelligence.NGrams;

/// <summary>
/// Extracts the four n-gram zone texts (FS-CRAWL-002 §5.3) from the contract
/// DOM: title (never scoped — head fact), headings H1–H6 and body (scope
/// applied), plus H1-only text for primary-term selection (§5.4: the H1
/// weight applies to H1 text only, not H2–H6). Inbound-anchor text is not a
/// DOM extraction — it comes from recorded internal link edges.
/// </summary>
/// <remarks>
/// Scoping integration: the body zone consumes the resolved Scoped Visible
/// Text Tree. The locked <see cref="IContentScopeResolver"/> returns text (not
/// a node set), so heading scoping uses a documented containment rule: a
/// heading is in scope when its normalized text occurs in the resolved scoped
/// text. With no scope configured every heading is included (baseline).
/// </remarks>
public static class ZoneTextExtractor
{
    private static readonly HashSet<string> SkippedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "head", "meta", "link", "title", "base",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "nav", "aside", "main",
        "h1", "h2", "h3", "h4", "h5", "h6", "li", "ul", "ol", "table", "tr", "td", "th",
        "blockquote", "pre", "br", "hr", "figure", "figcaption", "dl", "dt", "dd", "form",
    };

    /// <summary>Extracts the full document title (never scoped).</summary>
    /// <param name="document">Parsed document.</param>
    /// <returns>Title text, or empty string when absent.</returns>
    public static string ExtractTitle(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var title = FindFirst(document.Root, "title");
        return title is null ? string.Empty : Collapse(CollectText(title));
    }

    /// <summary>Extracts H1–H6 heading texts in document order.</summary>
    /// <param name="document">Parsed document.</param>
    /// <param name="scopedText">Resolved scoped text; null includes all headings.</param>
    /// <returns>Heading texts joined with sentence-separating newlines.</returns>
    public static string ExtractHeadings(DomDocument document, string? scopedText)
    {
        ArgumentNullException.ThrowIfNull(document);
        var headings = new List<string>();
        CollectHeadings(document.Root, scopedText, h1Only: false, headings);
        return string.Join('\n', headings);
    }

    /// <summary>Extracts H1-only texts for primary-term selection (§5.4).</summary>
    /// <param name="document">Parsed document.</param>
    /// <param name="scopedText">Resolved scoped text; null includes all H1s.</param>
    /// <returns>H1 texts joined with sentence-separating newlines.</returns>
    public static string ExtractH1(DomDocument document, string? scopedText)
    {
        ArgumentNullException.ThrowIfNull(document);
        var headings = new List<string>();
        CollectHeadings(document.Root, scopedText, h1Only: true, headings);
        return string.Join('\n', headings);
    }

    /// <summary>
    /// Extracts full visible body text (baseline when no scope is configured;
    /// the scoped tree replaces this when scoping ran).
    /// </summary>
    /// <param name="document">Parsed document.</param>
    /// <returns>Collapsed visible text.</returns>
    public static string ExtractVisibleText(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        AppendVisibleText(document.Root, builder);
        return Collapse(builder.ToString());
    }

    /// <summary>Extracts the declared language from the html lang attribute.</summary>
    /// <param name="document">Parsed document.</param>
    /// <returns>Language tag, or null when absent.</returns>
    public static string? ExtractLanguage(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var html = FindFirst(document.Root, "html");
        if (html is not null && html.Attributes.TryGetValue("lang", out var lang) && !string.IsNullOrWhiteSpace(lang))
        {
            return lang.Trim();
        }

        return null;
    }

    /// <summary>
    /// Extracts the page's declared rel=canonical target, resolved against the
    /// page URL. Returns null when absent, multiple, or malformed — the
    /// cannibalization canonical-downgrade rule (§5.5) requires a single
    /// well-formed declaration.
    /// </summary>
    /// <param name="document">Parsed document.</param>
    /// <param name="pageUrl">Base URL for resolving relative hrefs.</param>
    /// <returns>Absolute canonical target, or null.</returns>
    public static string? ExtractCanonicalTarget(DomDocument document, string pageUrl)
    {
        ArgumentNullException.ThrowIfNull(document);
        var canonicals = new List<string>();
        CollectCanonicals(document.Root, canonicals);
        if (canonicals.Count != 1 || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        return Uri.TryCreate(baseUri, canonicals[0], out var target)
            && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps)
            ? target.AbsoluteUri
            : null;
    }

    private static void CollectHeadings(DomNode node, string? scopedText, bool h1Only, List<string> headings)
    {
        var isHeading = h1Only
            ? string.Equals(node.TagName, "h1", StringComparison.OrdinalIgnoreCase)
            : node.TagName.Length == 2
                && (node.TagName[0] == 'h' || node.TagName[0] == 'H')
                && node.TagName[1] >= '1' && node.TagName[1] <= '6';
        if (isHeading)
        {
            var text = Collapse(CollectText(node));
            if (text.Length > 0 && (scopedText is null || ContainsNormalized(scopedText, text)))
            {
                headings.Add(text);
            }
        }

        foreach (var child in node.Children)
        {
            CollectHeadings(child, scopedText, h1Only, headings);
        }
    }

    private static bool ContainsNormalized(string scopedText, string headingText)
    {
        var normalizedScope = Collapse(scopedText);
        return normalizedScope.Contains(headingText, StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectCanonicals(DomNode node, List<string> canonicals)
    {
        if (string.Equals(node.TagName, "link", StringComparison.OrdinalIgnoreCase)
            && node.Attributes.TryGetValue("rel", out var rel)
            && rel.Split(' ').Any(part => string.Equals(part, "canonical", StringComparison.OrdinalIgnoreCase))
            && node.Attributes.TryGetValue("href", out var href)
            && !string.IsNullOrWhiteSpace(href))
        {
            canonicals.Add(href.Trim());
        }

        foreach (var child in node.Children)
        {
            CollectCanonicals(child, canonicals);
        }
    }

    private static DomNode? FindFirst(DomNode node, string tag)
    {
        if (string.Equals(node.TagName, tag, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindFirst(child, tag);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string CollectText(DomNode node)
    {
        // The node's own text is always collected (e.g. the title element is
        // skipped during whole-document visible-text walks, but its text IS
        // the title zone). Descendants go through the visibility filter.
        var builder = new StringBuilder();
        builder.Append(node.TextContent);
        foreach (var child in node.Children)
        {
            AppendVisibleText(child, builder);
        }

        return builder.ToString();
    }

    private static void AppendVisibleText(DomNode node, StringBuilder builder)
    {
        if (SkippedTags.Contains(node.TagName))
        {
            return;
        }

        if (BlockTags.Contains(node.TagName))
        {
            builder.Append(' ');
        }

        builder.Append(node.TextContent);
        foreach (var child in node.Children)
        {
            AppendVisibleText(child, builder);
        }

        if (BlockTags.Contains(node.TagName))
        {
            builder.Append(' ');
        }
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
            }
            else
            {
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
