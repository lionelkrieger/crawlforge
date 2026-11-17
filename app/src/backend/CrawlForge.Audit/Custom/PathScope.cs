namespace CrawlForge.Audit.Custom;

/// <summary>
/// URL-path include/exclude directives scoping a custom rule, search item, or
/// snippet to matching pages (FS-CRAWL-001 §7.9/§7.16/§7.17). Prefix semantics:
/// a directive matches when the URL path starts with it (ordinal,
/// case-insensitive). Exclude directives win over include directives; an empty
/// include list means "all paths".
/// </summary>
/// <param name="IncludePrefixes">Path prefixes the item applies to; empty means all.</param>
/// <param name="ExcludePrefixes">Path prefixes the item never applies to.</param>
public sealed record PathScope(
    IReadOnlyList<string> IncludePrefixes,
    IReadOnlyList<string> ExcludePrefixes)
{
    /// <summary>An unconstrained scope matching every path.</summary>
    public static PathScope All { get; } = new(Array.Empty<string>(), Array.Empty<string>());

    /// <summary>Evaluates the scope against a URL path.</summary>
    /// <param name="path">URL path (e.g. <c>/products/item</c>); null never matches.</param>
    /// <returns>True when the item runs on this path.</returns>
    public bool Matches(string? path)
    {
        if (path is null)
        {
            return false;
        }

        foreach (string exclude in ExcludePrefixes)
        {
            if (path.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (IncludePrefixes.Count == 0)
        {
            return true;
        }

        foreach (string include in IncludePrefixes)
        {
            if (path.StartsWith(include, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}