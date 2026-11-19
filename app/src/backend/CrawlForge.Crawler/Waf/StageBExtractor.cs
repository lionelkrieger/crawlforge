using System.Text.RegularExpressions;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// One start tag extracted by the bounded Stage B pre-parse: the tag name and
/// its attributes (lowercase names, decoded-as-authored values).
/// </summary>
/// <param name="Name">Tag name (as authored).</param>
/// <param name="Attributes">Attribute name/value map (names lowercase).</param>
public sealed record StageBTag(string Name, IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// Bounded pre-parse extraction of a leading HTML snippet (FS-CRAWL-002 §2.2
/// Stage B): title text, script sources and inline scripts, and a node-bounded
/// list of start tags for dom-marker selectors. Never a full document parse;
/// every regex is time-bounded and the tag scan stops at the policy node limit.
/// </summary>
/// <param name="RawSnippet">The bounded snippet exactly as supplied.</param>
/// <param name="Title">First title element text, when present.</param>
/// <param name="ScriptSources">Script src attribute values in document order.</param>
/// <param name="InlineScripts">Inline script bodies in document order.</param>
/// <param name="Tags">Start tags in document order, bounded by the node limit.</param>
public sealed record StageBDocument(
    string RawSnippet,
    string? Title,
    IReadOnlyList<string> ScriptSources,
    IReadOnlyList<string> InlineScripts,
    IReadOnlyList<StageBTag> Tags);

/// <summary>
/// Extracts the Stage B evidence document from a bounded HTML snippet.
/// Deterministic and hostile-input safe: regex match timeouts and the node
/// limit bound the work; extraction failures degrade to empty evidence,
/// never throw into the fetch path.
/// </summary>
public static class StageBExtractor
{
    /// <summary>Per-regex match timeout bounding the pre-parse (milliseconds).</summary>
    public const int MatchTimeoutMs = 250;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(MatchTimeoutMs);

    private static readonly Regex TitleRegex = new(
        @"<title\b[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        Timeout);

    private static readonly Regex ScriptRegex = new(
        @"<script\b([^>]*)>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        Timeout);

    private static readonly Regex TagRegex = new(
        @"<([a-zA-Z][a-zA-Z0-9:_-]*)((?:\s+[^<>]*?)?)\s*/?>",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        Timeout);

    private static readonly Regex AttributeRegex = new(
        @"([a-zA-Z_:][-a-zA-Z0-9_:.]*)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+)))?",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        Timeout);

    private static readonly Regex SrcRegex = new(
        @"\bsrc\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        Timeout);

    /// <summary>
    /// Extracts the bounded evidence document. The snippet must already be
    /// bounded by the detection policy; the node limit additionally caps the
    /// tag scan.
    /// </summary>
    /// <param name="snippet">Leading HTML snippet (policy-bounded).</param>
    /// <param name="nodeLimit">Maximum number of start tags inspected.</param>
    /// <returns>The Stage B evidence document.</returns>
    public static StageBDocument Extract(string snippet, int nodeLimit)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        if (nodeLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeLimit), nodeLimit, "The Stage B node limit must be positive.");
        }

        string? title = null;
        var scriptSources = new List<string>();
        var inlineScripts = new List<string>();
        var tags = new List<StageBTag>();

        try
        {
            var titleMatch = TitleRegex.Match(snippet);
            if (titleMatch.Success)
            {
                title = titleMatch.Groups[1].Value.Trim();
            }

            foreach (Match script in ScriptRegex.Matches(snippet))
            {
                if (scriptSources.Count + inlineScripts.Count >= nodeLimit)
                {
                    break;
                }

                var attributes = script.Groups[1].Value;
                var src = SrcRegex.Match(attributes);
                if (src.Success)
                {
                    scriptSources.Add(FirstNonEmpty(src.Groups[1].Value, src.Groups[2].Value, src.Groups[3].Value));
                }

                var inline = script.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(inline))
                {
                    inlineScripts.Add(inline);
                }
            }

            foreach (Match tag in TagRegex.Matches(snippet))
            {
                if (tags.Count >= nodeLimit)
                {
                    break;
                }

                tags.Add(new StageBTag(tag.Groups[1].Value, ParseAttributes(tag.Groups[2].Value)));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Bounded pre-parse (§2.2): hostile markup degrades to the evidence
            // collected so far; the pass never stalls a fetch worker.
        }

        return new StageBDocument(snippet, title, scriptSources, inlineScripts, tags);
    }

    private static IReadOnlyDictionary<string, string> ParseAttributes(string attributeText)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(attributeText))
        {
            return attributes;
        }

        foreach (Match attribute in AttributeRegex.Matches(attributeText))
        {
            var name = attribute.Groups[1].Value.ToLowerInvariant();
            var value = FirstNonEmpty(
                attribute.Groups[2].Success ? attribute.Groups[2].Value : string.Empty,
                attribute.Groups[3].Success ? attribute.Groups[3].Value : string.Empty,
                attribute.Groups[4].Success ? attribute.Groups[4].Value : string.Empty);
            attributes[name] = value;
        }

        return attributes;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }
}
