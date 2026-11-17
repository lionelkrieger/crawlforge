using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Indexability;

/// <summary>
/// One meta-robots directive scope extracted from HTML: the agent ("*" for
/// <c>name="robots"</c>) and its lowercase directive list, with the raw
/// content attribute retained as evidence.
/// </summary>
/// <param name="Agent">Agent scope; "*" for the generic robots meta.</param>
/// <param name="Directives">Lowercase directive tokens in declaration order.</param>
/// <param name="RawContent">The raw content attribute value.</param>
public sealed record HtmlRobotsDirective(
    string Agent,
    IReadOnlyList<string> Directives,
    string RawContent);

/// <summary>One <c>link rel="canonical"</c> element extracted from HTML.</summary>
/// <param name="Href">The href attribute as declared.</param>
public sealed record HtmlCanonicalLink(string Href);

/// <summary>One <c>link rel="alternate" hreflang="..."</c> element.</summary>
/// <param name="Hreflang">The hreflang attribute value.</param>
/// <param name="Href">The href attribute as declared.</param>
public sealed record HtmlHreflangAlternate(string Hreflang, string Href);

/// <summary>
/// All indexability-relevant directives extracted from one HTML document:
/// the HTML side of the §15.4 equivalency comparison.
/// </summary>
/// <param name="Robots">Meta-robots scopes in document order.</param>
/// <param name="Canonicals">Canonical link elements in document order.</param>
/// <param name="HreflangAlternates">hreflang alternate links in document order.</param>
public sealed record HtmlDirectives(
    IReadOnlyList<HtmlRobotsDirective> Robots,
    IReadOnlyList<HtmlCanonicalLink> Canonicals,
    IReadOnlyList<HtmlHreflangAlternate> HreflangAlternates);

/// <summary>
/// Extracts meta-robots, canonical, and hreflang directives from the contract
/// DOM model (FS-CRAWL-001 §7.3 inventory; FS-CRAWL-002 §15.4). Pure and
/// offline: re-runs identically from stored bodies on re-audit.
/// </summary>
public static class HtmlDirectiveExtractor
{
    /// <summary>Extracts the directive set from a parsed document.</summary>
    /// <param name="document">Parsed document; null yields an empty set.</param>
    /// <returns>The extracted directives in document order.</returns>
    public static HtmlDirectives Extract(DomDocument? document)
    {
        var robots = new List<HtmlRobotsDirective>();
        var canonicals = new List<HtmlCanonicalLink>();
        var alternates = new List<HtmlHreflangAlternate>();

        foreach (DomNode node in DomTraversal.EnumerateElements(document))
        {
            if (string.Equals(node.TagName, "meta", StringComparison.OrdinalIgnoreCase))
            {
                string? name = DomTraversal.Attribute(node, "name");
                string? content = DomTraversal.Attribute(node, "content");
                if (name is null || content is null)
                {
                    continue;
                }

                string? agent = ResolveRobotsAgent(name);
                if (agent is not null)
                {
                    List<string> directives = content
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(d => d.ToLowerInvariant())
                        .Where(d => d.Length > 0)
                        .ToList();
                    if (directives.Count > 0)
                    {
                        robots.Add(new HtmlRobotsDirective(agent, directives, content));
                    }
                }
            }
            else if (string.Equals(node.TagName, "link", StringComparison.OrdinalIgnoreCase))
            {
                string? rel = DomTraversal.Attribute(node, "rel");
                string? href = DomTraversal.Attribute(node, "href");
                if (rel is null || href is null)
                {
                    continue;
                }

                HashSet<string> relations = rel
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(r => r.ToLowerInvariant())
                    .ToHashSet(StringComparer.Ordinal);

                if (relations.Contains("canonical"))
                {
                    canonicals.Add(new HtmlCanonicalLink(href.Trim()));
                }

                if (relations.Contains("alternate"))
                {
                    string? hreflang = DomTraversal.Attribute(node, "hreflang");
                    if (!string.IsNullOrWhiteSpace(hreflang))
                    {
                        alternates.Add(new HtmlHreflangAlternate(hreflang.Trim(), href.Trim()));
                    }
                }
            }
        }

        return new HtmlDirectives(robots, canonicals, alternates);
    }

    private static string? ResolveRobotsAgent(string metaName)
    {
        if (string.Equals(metaName, "robots", StringComparison.OrdinalIgnoreCase))
        {
            return HeaderDirectiveParser.GenericAgent;
        }

        // Agent-specific meta robots (googlebot, bingbot, ...): any name
        // ending in "bot" is treated as an agent scope.
        if (metaName.EndsWith("bot", StringComparison.OrdinalIgnoreCase) && metaName.Length > 3)
        {
            return metaName.ToLowerInvariant();
        }

        return null;
    }
}