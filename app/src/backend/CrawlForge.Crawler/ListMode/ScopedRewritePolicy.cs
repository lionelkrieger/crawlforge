using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// One on-the-fly rewrite rule with its §10.4 scope dimension. The locked
/// <see cref="UrlRewriteRule"/> contract carries pattern/replacement/enabled;
/// scope (seeds-only | all-discovered) is the List Mode configuration dimension
/// carried alongside it (FS-CRAWL-002 §10.4: "Rule fields: pattern, replacement,
/// scope (seeds-only | all-discovered), enabled flag").
/// </summary>
/// <param name="Rule">The locked rewrite rule.</param>
/// <param name="Scope">Where the rule applies; default seeds-only (baseline).</param>
public sealed record ScopedRewriteRule(UrlRewriteRule Rule, RewriteRuleScope Scope = RewriteRuleScope.SeedsOnly);

/// <summary>
/// One step of a rewrite preview (FS-CRAWL-002 §10.7 rewrite-preview surface):
/// the URL before and after one rule, in application order.
/// </summary>
/// <param name="RuleIndex">Zero-based position of the rule in application order.</param>
/// <param name="Pattern">The rule pattern.</param>
/// <param name="Before">URL before the rule.</param>
/// <param name="After">URL after the rule (unchanged when the rule did not apply).</param>
/// <param name="Changed">Whether the rule changed the URL.</param>
public sealed record RewritePreviewRow(int RuleIndex, string Pattern, string Before, string After, bool Changed);

/// <summary>
/// Live preview of the rewrite pipeline over one sample URL (§10.7): every
/// rule's before/after step plus the final output. Purely local.
/// </summary>
/// <param name="InputUrl">The sample URL.</param>
/// <param name="OutputUrl">The URL after all applicable rules.</param>
/// <param name="Steps">Per-rule steps in application order.</param>
public sealed record RewritePreviewResult(string InputUrl, string OutputUrl, IReadOnlyList<RewritePreviewRow> Steps);

/// <summary>
/// Deterministic, scope-aware application of on-the-fly URL rewrite rules
/// (FS-CRAWL-002 §10.4). Rules apply in configuration order through the locked
/// <see cref="IUrlRewriteEngine"/>; seeds receive every enabled rule, discovered
/// URLs only the enabled <see cref="RewriteRuleScope.AllDiscovered"/> rules.
/// Invalid patterns are rejected at configuration time (the snapshot pattern:
/// the rule set is frozen when the policy is constructed at audit start);
/// pattern evaluation is time-bounded; a rewrite may never produce a
/// non-http(s) scheme; per-rule counters are exact. All work is local — no
/// endpoint is contacted.
/// <para>
/// This component extends the seed-only <see cref="RewriteRuleGuard"/> used by
/// the ingestion pipeline with the §10.4 scope dimension for discovered URLs;
/// the engine consults <see cref="ApplyToDiscovered"/> during link discovery
/// and <see cref="ApplyToSeed"/> for seed/list URLs.
/// </para>
/// </summary>
public sealed class ScopedRewritePolicy
{
    private readonly IReadOnlyList<ScopedRewriteRule> _rules;
    private readonly int[] _counters;

    /// <summary>
    /// Creates a policy over the validated rule set. Invalid patterns are
    /// rejected here (configuration time), including disabled rules so enabling
    /// them later cannot introduce a bad pattern.
    /// </summary>
    /// <param name="rules">Scoped rules in application order.</param>
    /// <exception cref="ArgumentException">A rule pattern is not a valid regular expression.</exception>
    public ScopedRewritePolicy(IReadOnlyList<ScopedRewriteRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var diagnostics = RewriteRuleGuard.Validate(rules.Select(r => r.Rule).ToList());
        var invalid = diagnostics.Where(d => !d.IsValid).ToList();
        if (invalid.Count > 0)
        {
            throw new ArgumentException(
                "Invalid rewrite pattern(s): " + string.Join("; ", invalid.Select(d => $"rule {d.RuleIndex}: {d.Reason}")),
                nameof(rules));
        }

        _rules = rules;
        _counters = new int[rules.Count];
    }

    /// <summary>Per-rule rewrite counters accumulated since construction.</summary>
    /// <returns>One counter per rule, in application order.</returns>
    public IReadOnlyList<RewriteRuleCounter> Counters()
    {
        var result = new List<RewriteRuleCounter>(_rules.Count);
        for (var i = 0; i < _rules.Count; i++)
        {
            result.Add(new RewriteRuleCounter(i, _rules[i].Rule.Pattern, Volatile.Read(ref _counters[i])));
        }

        return result;
    }

    /// <summary>
    /// Applies every enabled rule to a seed/list URL in order (§10.4). A rule
    /// whose result is not an absolute http(s) URL is ignored (scheme invariant).
    /// </summary>
    /// <param name="url">Input seed URL.</param>
    /// <param name="engine">The locked rewrite engine.</param>
    /// <returns>The rewritten URL, or the input when no rule changed it validly.</returns>
    public string ApplyToSeed(string url, IUrlRewriteEngine engine) => Apply(url, engine, isSeed: true);

    /// <summary>
    /// Applies the enabled all-discovered rules to a discovered URL in order
    /// (§10.4 scope). Seeds-only rules never touch discovered URLs.
    /// </summary>
    /// <param name="url">Input discovered URL.</param>
    /// <param name="engine">The locked rewrite engine.</param>
    /// <returns>The rewritten URL, or the input when no rule changed it validly.</returns>
    public string ApplyToDiscovered(string url, IUrlRewriteEngine engine) => Apply(url, engine, isSeed: false);

    /// <summary>
    /// Produces the live per-rule before/after preview of one sample URL
    /// (§10.7). Rules that do not apply (disabled or out of scope) appear as
    /// unchanged steps so the preview shows the full pipeline. The preview is
    /// a dry run: it never mutates the per-rule rewrite counters.
    /// </summary>
    /// <param name="url">Sample URL.</param>
    /// <param name="engine">The locked rewrite engine.</param>
    /// <param name="isSeed">Whether the sample is a seed/list URL (true) or a discovered URL (false).</param>
    /// <returns>The preview result with per-rule steps.</returns>
    public RewritePreviewResult Preview(string url, IUrlRewriteEngine engine, bool isSeed)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(engine);

        var steps = new List<RewritePreviewRow>(_rules.Count);
        var current = url;
        for (var i = 0; i < _rules.Count; i++)
        {
            var scoped = _rules[i];
            var before = current;
            if (Applies(scoped, isSeed))
            {
                current = RewriteOnce(current, scoped.Rule, engine);
            }

            steps.Add(new RewritePreviewRow(i, scoped.Rule.Pattern, before, current, !string.Equals(before, current, StringComparison.Ordinal)));
        }

        return new RewritePreviewResult(url, current, steps);
    }

    /// <summary>
    /// Wraps plain locked rules with a uniform scope (baseline: seeds-only,
    /// reproducing the ingestion pipeline's behavior).
    /// </summary>
    /// <param name="rules">Plain rewrite rules in application order.</param>
    /// <param name="scope">Scope assigned to every rule.</param>
    /// <returns>The scoped rule set.</returns>
    public static IReadOnlyList<ScopedRewriteRule> FromPlainRules(
        IReadOnlyList<UrlRewriteRule> rules,
        RewriteRuleScope scope = RewriteRuleScope.SeedsOnly)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.Select(rule => new ScopedRewriteRule(rule, scope)).ToList();
    }

    private string Apply(string url, IUrlRewriteEngine engine, bool isSeed)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(engine);

        var current = url;
        for (var i = 0; i < _rules.Count; i++)
        {
            var scoped = _rules[i];
            if (!Applies(scoped, isSeed))
            {
                continue;
            }

            current = ApplyRule(current, scoped.Rule, engine, i);
        }

        return current;
    }

    private string ApplyRule(string current, UrlRewriteRule rule, IUrlRewriteEngine engine, int ruleIndex)
    {
        var rewritten = RewriteOnce(current, rule, engine);
        if (!string.Equals(rewritten, current, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _counters[ruleIndex]);
        }

        return rewritten;
    }

    /// <summary>
    /// One rule application with the §10.4 scheme invariant, no counting:
    /// a rewrite that changes nothing or produces a non-http(s) URL is ignored.
    /// </summary>
    private static string RewriteOnce(string current, UrlRewriteRule rule, IUrlRewriteEngine engine)
    {
        var rewritten = engine.Rewrite(current, new[] { rule });
        if (string.Equals(rewritten, current, StringComparison.Ordinal))
        {
            return current;
        }

        if (!ListInputParser.TryValidateUrl(rewritten, out _))
        {
            // §10.4: a rewrite may never produce a non-http(s) scheme.
            return current;
        }

        return rewritten;
    }

    private static bool Applies(ScopedRewriteRule scoped, bool isSeed) =>
        scoped.Rule.Enabled && (isSeed || scoped.Scope == RewriteRuleScope.AllDiscovered);
}
