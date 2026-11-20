using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Crawler.Capture;

/// <summary>
/// Dependency-free document facts over the contract DOM model
/// (<see cref="DomDocument"/>/<see cref="DomNode"/>) used by the capture-mode
/// coordinator at crawl time: the adaptive material-change comparison
/// (§5.6.1), rendered-only link discovery and the service-worker registration
/// fact (§7.19). Pure functions of the parsed documents — the analysis-time
/// response-vs-rendered comparer (Audit §7.18) re-derives the same facts
/// offline from stored artifacts; conformance tests prove the two agree.
/// </summary>
internal static partial class CaptureDocumentFacts
{
    private static readonly HashSet<string> NonVisibleTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "head", "meta", "link", "title",
    };

    /// <summary>Visible word count (whitespace-split, non-visible subtrees pruned).</summary>
    public static int WordCount(DomDocument? document)
    {
        string text = VisibleText(document);
        return text.Length == 0
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>Visible text: text nodes outside non-visible tags, whitespace-collapsed.</summary>
    public static string VisibleText(DomDocument? document)
    {
        if (document?.Root is null)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        AppendVisibleText(document.Root, builder);
        return CollapseWhitespace(builder.ToString());
    }

    /// <summary>The document title (first title element), whitespace-collapsed; null when absent.</summary>
    public static string? Title(DomDocument? document)
    {
        foreach (DomNode node in EnumerateElements(document))
        {
            if (string.Equals(node.TagName, "title", StringComparison.OrdinalIgnoreCase))
            {
                return CollapseWhitespace(node.TextContent);
            }
        }

        return null;
    }

    /// <summary>Canonical hrefs (link rel="canonical"), trimmed, in document order.</summary>
    public static IReadOnlyList<string> Canonicals(DomDocument? document)
    {
        var result = new List<string>();
        foreach (DomNode node in EnumerateElements(document))
        {
            if (!string.Equals(node.TagName, "link", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasRel(node, "canonical") && Attribute(node, "href") is { } href && !string.IsNullOrWhiteSpace(href))
            {
                result.Add(href.Trim());
            }
        }

        return result;
    }

    /// <summary>
    /// Meta-robots directive signature: one "agent=directive,directive" entry per
    /// robots meta scope (name="robots" → "*"; any name ending "bot" → that agent),
    /// sorted for order-independent comparison.
    /// </summary>
    public static string MetaRobotsSignature(DomDocument? document)
    {
        var parts = new List<string>();
        foreach (DomNode node in EnumerateElements(document))
        {
            if (!string.Equals(node.TagName, "meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? name = Attribute(node, "name");
            string? content = Attribute(node, "content");
            if (name is null || content is null)
            {
                continue;
            }

            string? agent = RobotsAgent(name);
            if (agent is null)
            {
                continue;
            }

            var directives = content
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(d => d.ToLowerInvariant())
                .Where(d => d.Length > 0)
                .ToList();
            if (directives.Count > 0)
            {
                parts.Add(agent + "=" + string.Join(",", directives));
            }
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    /// <summary>Whether any meta-robots scope carries noindex (or "none", which implies it).</summary>
    public static bool HasNoindex(DomDocument? document)
    {
        foreach (DomNode node in EnumerateElements(document))
        {
            if (!string.Equals(node.TagName, "meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? name = Attribute(node, "name");
            string? content = Attribute(node, "content");
            if (name is null || content is null || RobotsAgent(name) is null)
            {
                continue;
            }

            foreach (string directive in content.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(directive, "noindex", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(directive, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>hreflang alternate signature: sorted "hreflang=href" entries.</summary>
    public static string HreflangSignature(DomDocument? document)
    {
        var parts = new List<string>();
        foreach (DomNode node in EnumerateElements(document))
        {
            if (!string.Equals(node.TagName, "link", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasRel(node, "alternate")
                && Attribute(node, "hreflang") is { } hreflang
                && !string.IsNullOrWhiteSpace(hreflang)
                && Attribute(node, "href") is { } href)
            {
                parts.Add(hreflang.Trim() + "=" + href.Trim());
            }
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    /// <summary>The set of anchor hrefs (trimmed, non-empty), ordinal.</summary>
    public static HashSet<string> LinkHrefs(DomDocument? document)
    {
        var hrefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (DomNode node in EnumerateElements(document))
        {
            if (string.Equals(node.TagName, "a", StringComparison.OrdinalIgnoreCase)
                && Attribute(node, "href") is { } href
                && !string.IsNullOrWhiteSpace(href))
            {
                hrefs.Add(href.Trim());
            }
        }

        return hrefs;
    }

    /// <summary>
    /// The set of anchor hrefs resolved absolute against the page URL (falling
    /// back to the trimmed href when resolution fails). Normalizing both captures
    /// the same way keeps the adaptive link-set comparison and rendered-only link
    /// discovery immune to relative-vs-absolute serialization differences.
    /// </summary>
    public static HashSet<string> NormalizedLinkHrefs(DomDocument? document, string pageUrl)
    {
        var hrefs = new HashSet<string>(StringComparer.Ordinal);
        Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri);
        foreach (string href in LinkHrefs(document))
        {
            hrefs.Add(baseUri is not null && Uri.TryCreate(baseUri, href, out var resolved)
                ? resolved.ToString()
                : href);
        }

        return hrefs;
    }

    /// <summary>
    /// Anchors of the rendered document whose href is absent from the raw
    /// document, resolved absolute against the page URL. Carries anchor text and
    /// the nofollow relation so the engine can apply its link directives.
    /// </summary>
    public static IReadOnlyList<RenderedLinkCandidate> RenderedOnlyLinks(DomDocument? raw, DomDocument? rendered, string pageUrl)
    {
        if (rendered?.Root is null || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri))
        {
            return [];
        }

        HashSet<string> rawHrefs = NormalizedLinkHrefs(raw, pageUrl);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<RenderedLinkCandidate>();
        foreach (DomNode node in EnumerateElements(rendered))
        {
            if (!string.Equals(node.TagName, "a", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? href = Attribute(node, "href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            string trimmed = href.Trim();
            if (!Uri.TryCreate(baseUri, trimmed, out var resolved)
                || resolved.Scheme is not ("http" or "https"))
            {
                continue;
            }

            string absolute = resolved.ToString();
            if (rawHrefs.Contains(absolute) || !seen.Add(absolute))
            {
                continue;
            }

            string? anchorText = string.IsNullOrWhiteSpace(node.TextContent) ? null : CollapseWhitespace(node.TextContent);
            candidates.Add(new RenderedLinkCandidate(absolute, anchorText, HasRel(node, "nofollow")));
        }

        return candidates;
    }

    /// <summary>
    /// Service-worker registration fact (§7.19): a script calling
    /// <c>navigator.serviceWorker.register(...)</c>. Returns the registered
    /// script URL when one is declared, or an empty string when a registration
    /// call is present without a literal argument; null when none is observed.
    /// Renders bypass service workers — this is a detection fact only.
    /// </summary>
    public static string? ServiceWorkerRegistration(DomDocument? document)
    {
        foreach (DomNode node in EnumerateElements(document))
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
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        return null;
    }

    [GeneratedRegex(@"serviceWorker\s*\.\s*register\s*\(\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceWorkerRegisterPattern();

    private static string? RobotsAgent(string metaName)
    {
        if (string.Equals(metaName, "robots", StringComparison.OrdinalIgnoreCase))
        {
            return "*";
        }

        return metaName.EndsWith("bot", StringComparison.OrdinalIgnoreCase) && metaName.Length > 3
            ? metaName.ToLowerInvariant()
            : null;
    }

    private static bool HasRel(DomNode node, string relation)
    {
        if (Attribute(node, "rel") is not { } rel)
        {
            return false;
        }

        return rel.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(r => string.Equals(r, relation, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Attribute(DomNode node, string name)
    {
        foreach (KeyValuePair<string, string> attribute in node.Attributes)
        {
            if (string.Equals(attribute.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    private static IEnumerable<DomNode> EnumerateElements(DomDocument? document)
    {
        if (document?.Root is null)
        {
            yield break;
        }

        var stack = new Stack<DomNode>();
        stack.Push(document.Root);
        while (stack.Count > 0)
        {
            DomNode node = stack.Pop();
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }

    private static void AppendVisibleText(DomNode node, System.Text.StringBuilder builder)
    {
        if (NonVisibleTags.Contains(node.TagName))
        {
            return;
        }

        if (!string.IsNullOrEmpty(node.TextContent))
        {
            builder.Append(node.TextContent).Append(' ');
        }

        foreach (DomNode child in node.Children)
        {
            AppendVisibleText(child, builder);
        }
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }
}
