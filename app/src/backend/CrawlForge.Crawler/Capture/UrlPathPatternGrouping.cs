namespace CrawlForge.Crawler.Capture;

/// <summary>
/// URL-path-pattern grouping and override matching for capture modes
/// (FS-CRAWL-001 §5.6.1: "URLs are grouped by URL path pattern (for example
/// /blog/*, /product/*) until template identification is available"; the
/// <c>CapturePatternOverride</c> contract leaves the matching semantics to the
/// engine's URL-pattern grouping).
/// <para>
/// Grouping rule (deterministic, documented): the group key is the URL path with
/// its final segment replaced by <c>*</c>. A page and the children of its
/// directory therefore share one template group: <c>/blog/post-1</c> and
/// <c>/blog/</c> both group to <c>/blog/*</c>; <c>/a/b/c</c> groups to
/// <c>/a/b/*</c>; the site root <c>/</c> is its own group. Query strings and
/// fragments never participate (grouping is path-only).
/// </para>
/// <para>
/// Override matching rule: a pattern matches a path when it equals the path
/// (ordinal, case-sensitive) or when its <c>*</c> wildcards expand to any
/// sequence of characters (including <c>/</c>, so <c>/blog/*</c> matches nested
/// paths). The first matching override in configuration order wins.
/// </para>
/// </summary>
public static class UrlPathPatternGrouping
{
    /// <summary>
    /// Computes the template group key of a URL path: the path with its final
    /// segment replaced by <c>*</c> (root <c>/</c> stays <c>/</c>).
    /// </summary>
    /// <param name="path">URL path (may carry query/fragment; they are stripped).</param>
    /// <returns>The group key, never empty.</returns>
    public static string GroupKey(string? path)
    {
        string clean = CleanPath(path);
        if (clean == "/")
        {
            return "/";
        }

        // A directory path ("/blog/") groups with its children ("/blog/post-1"):
        // both yield "/blog/*". A file path replaces its final segment.
        if (clean.EndsWith('/'))
        {
            return clean + "*";
        }

        int lastSlash = clean.LastIndexOf('/');
        string parent = lastSlash <= 0 ? "/" : clean.Substring(0, lastSlash + 1);
        return parent + "*";
    }

    /// <summary>
    /// Whether a capture-override pattern matches a URL path. Supports exact
    /// equality and <c>*</c> wildcards (matching any characters, including
    /// <c>/</c>). Matching is ordinal and case-sensitive.
    /// </summary>
    /// <param name="pattern">Override pattern (for example "/blog/*").</param>
    /// <param name="path">URL path to test.</param>
    /// <returns>True when the pattern matches.</returns>
    public static bool Matches(string? pattern, string? path)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        string clean = CleanPath(path);
        if (!pattern.Contains('*'))
        {
            return string.Equals(pattern, clean, StringComparison.Ordinal);
        }

        return GlobMatch(pattern, clean);
    }

    private static string CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        string value = path.Trim();
        int cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            value = value.Substring(0, cut);
        }

        if (value.Length == 0)
        {
            return "/";
        }

        return value.StartsWith('/') ? value : "/" + value;
    }

    private static bool GlobMatch(string pattern, string value)
    {
        // Iterative wildcard match with backtracking on the last '*': bounded,
        // no recursion (pathological patterns cannot stall a capture worker).
        int p = 0;
        int v = 0;
        int star = -1;
        int starV = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                starV = v;
            }
            else if (p < pattern.Length && pattern[p] == value[v])
            {
                p++;
                v++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++starV;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
