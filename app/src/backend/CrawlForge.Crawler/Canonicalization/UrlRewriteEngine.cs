using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Canonicalization;

/// <summary>
/// Application scope of a rewrite rule (FS-CRAWL-002 §10.4). The locked
/// <see cref="UrlRewriteRule"/> contract carries pattern/replacement/enabled;
/// scope is a local configuration dimension applied when selecting rules.
/// </summary>
public enum RewriteScope
{
    /// <summary>Rule applies to every discovered URL.</summary>
    AllDiscovered = 0,

    /// <summary>Rule applies only to seed URLs.</summary>
    SeedsOnly = 1
}

/// <summary>A rewrite rule with its application scope.</summary>
/// <param name="Rule">The pattern/replacement/enabled rule.</param>
/// <param name="Scope">Where the rule participates.</param>
public sealed record ScopedUrlRewriteRule(UrlRewriteRule Rule, RewriteScope Scope = RewriteScope.AllDiscovered);

/// <summary>One rule evaluation step inside a rewrite preview.</summary>
/// <param name="Pattern">The rule pattern.</param>
/// <param name="Matched">Whether the rule changed the URL at this step.</param>
/// <param name="Before">URL before this rule.</param>
/// <param name="After">URL after this rule.</param>
public sealed record RuleRewriteStep(string Pattern, bool Matched, string Before, string After);

/// <summary>Before/after preview of one sample URL (rewrite-preview tool contract).</summary>
/// <param name="Original">The sample URL as supplied.</param>
/// <param name="Final">The URL after all enabled rules.</param>
/// <param name="Steps">Per-rule evaluation steps in application order.</param>
public sealed record RewritePreviewItem(string Original, string Final, IReadOnlyList<RuleRewriteStep> Steps);

/// <summary>Rewrite outcome with per-rule diagnostic match counters.</summary>
/// <param name="Url">The rewritten URL.</param>
/// <param name="RuleMatchCounts">Match counts keyed by rule pattern (diagnostics only).</param>
public sealed record RewriteResult(string Url, IReadOnlyDictionary<string, long> RuleMatchCounts);

/// <summary>
/// On-the-fly regex URL rewrite engine (FS-CRAWL-002 §10.4): ordered, deterministic
/// find/replace applied during canonicalization before seen-set keying. Pattern
/// evaluation is time-bounded; invalid patterns are rejected at configuration time;
/// a rewrite may never produce a non-http(s) scheme. Per-rule match counters are
/// exposed as local diagnostics, and the preview contract serves the standalone
/// rewrite-preview tool. Fully local configuration — no remote input.
/// </summary>
public sealed class UrlRewriteEngine : IUrlRewriteEngine
{
    /// <summary>Default per-evaluation match timeout (bounded-pattern philosophy).</summary>
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<string, Regex> _compiled = new(StringComparer.Ordinal);
    private readonly TimeSpan _matchTimeout;

    /// <summary>Creates the engine with an optional per-evaluation timeout.</summary>
    /// <param name="matchTimeout">Match timeout; null uses <see cref="DefaultMatchTimeout"/>.</param>
    public UrlRewriteEngine(TimeSpan? matchTimeout = null)
    {
        _matchTimeout = matchTimeout ?? DefaultMatchTimeout;
        if (_matchTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(matchTimeout), "The match timeout must be positive.");
        }
    }

    /// <inheritdoc />
    public string Rewrite(string url, IReadOnlyList<UrlRewriteRule> rules)
    {
        var current = url;
        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            current = ApplyRule(current, rule);
        }

        return current;
    }

    /// <summary>
    /// Rewrites with scoped rules: seeds-only rules participate only for seed URLs.
    /// </summary>
    /// <param name="url">Input URL.</param>
    /// <param name="rules">Scoped rules in application order.</param>
    /// <param name="isSeed">Whether the URL is a seed.</param>
    /// <returns>The rewritten URL.</returns>
    public string Rewrite(string url, IReadOnlyList<ScopedUrlRewriteRule> rules, bool isSeed)
    {
        var current = url;
        foreach (var scoped in rules)
        {
            if (!scoped.Rule.Enabled || (scoped.Scope == RewriteScope.SeedsOnly && !isSeed))
            {
                continue;
            }

            current = ApplyRule(current, scoped.Rule);
        }

        return current;
    }

    /// <summary>Rewrites and reports per-rule match counters (local diagnostics).</summary>
    /// <param name="url">Input URL.</param>
    /// <param name="rules">Rewrite rules in application order.</param>
    /// <returns>The rewritten URL plus per-pattern match counts.</returns>
    public RewriteResult RewriteWithDiagnostics(string url, IReadOnlyList<UrlRewriteRule> rules)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var current = url;
        foreach (var rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            var before = current;
            current = ApplyRule(current, rule);
            if (!string.Equals(before, current, StringComparison.Ordinal))
            {
                counts[rule.Pattern] = counts.GetValueOrDefault(rule.Pattern) + 1;
            }
        }

        return new RewriteResult(current, counts);
    }

    /// <summary>
    /// Validates rules at configuration time (FS-CRAWL-002 §10.4 / §4.3): every
    /// enabled pattern must compile within the timeout budget.
    /// </summary>
    /// <param name="rules">The candidate rule set.</param>
    /// <exception cref="ArgumentException">A pattern is not a valid regular expression.</exception>
    public void ValidateRules(IEnumerable<UrlRewriteRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule.Enabled)
            {
                GetOrCompile(rule.Pattern);
            }
        }
    }

    /// <summary>
    /// Produces before/after previews per rule for sample URLs — the contract behind
    /// the standalone rewrite-preview tool (FS-CRAWL-002 §10.7).
    /// </summary>
    /// <param name="sampleUrls">Sample URLs to preview.</param>
    /// <param name="rules">Rewrite rules in application order.</param>
    /// <returns>One preview item per sample URL.</returns>
    public IReadOnlyList<RewritePreviewItem> Preview(IEnumerable<string> sampleUrls, IReadOnlyList<UrlRewriteRule> rules)
    {
        var items = new List<RewritePreviewItem>();
        foreach (var sample in sampleUrls)
        {
            var steps = new List<RuleRewriteStep>();
            var current = sample;
            foreach (var rule in rules)
            {
                if (!rule.Enabled)
                {
                    continue;
                }

                var before = current;
                current = ApplyRule(current, rule);
                steps.Add(new RuleRewriteStep(rule.Pattern, !string.Equals(before, current, StringComparison.Ordinal), before, current));
            }

            items.Add(new RewritePreviewItem(sample, current, steps));
        }

        return items;
    }

    private string ApplyRule(string url, UrlRewriteRule rule)
    {
        var regex = GetOrCompile(rule.Pattern);
        string replaced;
        try
        {
            replaced = regex.Replace(url, rule.Replacement);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new TimeoutException($"Rewrite pattern exceeded the {_matchTimeout.TotalMilliseconds} ms evaluation budget.", ex);
        }

        GuardScheme(replaced, rule.Pattern);
        return replaced;
    }

    private Regex GetOrCompile(string pattern) =>
        _compiled.GetOrAdd(pattern, p =>
        {
            try
            {
                return new Regex(p, RegexOptions.CultureInvariant, _matchTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid rewrite pattern '{p}': {ex.Message}", ex);
            }
        });

    private static void GuardScheme(string url, string pattern)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Rewrite pattern '{pattern}' produced a non-http(s) scheme '{uri.Scheme}'.");
        }
    }
}
