namespace CrawlForge.Contracts.Crawl;

/// <summary>
/// Parses robots.txt bodies and evaluates allow/disallow verdicts
/// (FS-CRAWL-001 §4; robots test tool §8.7). Parsing is purely local.
/// </summary>
public interface IRobotsTxtParser
{
    /// <summary>Parses a robots.txt body into groups, rules, and sitemap hints.</summary>
    /// <param name="robotsBody">Raw robots.txt content.</param>
    /// <returns>The parsed robots document.</returns>
    RobotsDocument Parse(string robotsBody);

    /// <summary>Evaluates the verdict for a URL under a user-agent.</summary>
    /// <param name="document">Parsed robots document.</param>
    /// <param name="url">URL to test.</param>
    /// <param name="userAgent">User-agent token to match.</param>
    /// <returns>Allow/disallow verdict with the matched rule.</returns>
    RobotsVerdict Evaluate(RobotsDocument document, string url, string userAgent);
}

/// <summary>
/// A parsed robots.txt document.
/// </summary>
/// <param name="Groups">User-agent groups in document order.</param>
/// <param name="Sitemaps">Sitemap URLs declared in the document.</param>
public sealed record RobotsDocument(
    IReadOnlyList<RobotsGroup> Groups,
    IReadOnlyList<string> Sitemaps);

/// <summary>
/// One user-agent group of a robots document.
/// </summary>
/// <param name="UserAgents">User-agent tokens the group applies to.</param>
/// <param name="Rules">Allow/disallow rules in document order.</param>
/// <param name="CrawlDelaySeconds">Optional crawl-delay directive.</param>
public sealed record RobotsGroup(
    IReadOnlyList<string> UserAgents,
    IReadOnlyList<RobotsRule> Rules,
    double? CrawlDelaySeconds);

/// <summary>
/// A single allow/disallow rule.
/// </summary>
/// <param name="Allow">True for Allow, false for Disallow.</param>
/// <param name="PathPattern">Path pattern of the rule.</param>
public sealed record RobotsRule(bool Allow, string PathPattern);

/// <summary>
/// Robots verdict for one URL (robots test tool output).
/// </summary>
/// <param name="Allowed">Whether crawling the URL is allowed.</param>
/// <param name="MatchedRule">The rule that decided the verdict, if any.</param>
public sealed record RobotsVerdict(bool Allowed, RobotsRule? MatchedRule);

/// <summary>
/// Canonicalizes URLs to the canonical form used as the unique crawl key
/// (FS-CRAWL-001 §5.2; unique per (audit, canonical URL)).
/// </summary>
public interface IUrlCanonicalizer
{
    /// <summary>Returns the canonical form of a URL.</summary>
    /// <param name="url">Input URL.</param>
    /// <returns>Canonical URL string.</returns>
    string Canonicalize(string url);
}

/// <summary>
/// Applies user-defined regular-expression URL rewrite rules on the fly
/// (FS-CRAWL-002 §10). Rewriting is local configuration only.
/// </summary>
public interface IUrlRewriteEngine
{
    /// <summary>Rewrites a URL by applying the enabled rules in order.</summary>
    /// <param name="url">Input URL.</param>
    /// <param name="rules">Rewrite rules in application order.</param>
    /// <returns>The rewritten URL (unchanged when no rule matches).</returns>
    string Rewrite(string url, IReadOnlyList<UrlRewriteRule> rules);
}

/// <summary>
/// One regular-expression URL rewrite rule.
/// </summary>
/// <param name="Pattern">Regular expression matched against the URL.</param>
/// <param name="Replacement">Replacement expression (may reference capture groups).</param>
/// <param name="Enabled">Whether the rule participates in rewriting.</param>
public sealed record UrlRewriteRule(string Pattern, string Replacement, bool Enabled = true);
