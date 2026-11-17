using System.Text.RegularExpressions;
using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Configuration-time validation and deterministic application of on-the-fly
/// rewrite rules (FS-CRAWL-002 §10.4). Invalid patterns are rejected at
/// configuration time; pattern evaluation is time-bounded; rules apply in
/// order through the locked <see cref="IUrlRewriteEngine"/> contract; a rewrite
/// may never produce a non-http(s) scheme. Per-rule counters are recorded as
/// local diagnostics. All work is local — no endpoint is contacted.
/// </summary>
public sealed class RewriteRuleGuard
{
    /// <summary>Time bound for one pattern evaluation (§7.16 bound philosophy).</summary>
    public static TimeSpan PatternTimeout { get; } = TimeSpan.FromMilliseconds(250);

    private readonly IReadOnlyList<UrlRewriteRule> _validRules;
    private readonly int[] _counters;

    /// <summary>
    /// Creates a guard over the validated subset of the supplied rules.
    /// Invalid patterns are rejected here (configuration time); use
    /// <see cref="Validate"/> first to surface per-rule diagnostics.
    /// </summary>
    /// <param name="rules">Rewrite rules in application order.</param>
    /// <exception cref="ArgumentException">A rule pattern is not a valid regular expression.</exception>
    public RewriteRuleGuard(IReadOnlyList<UrlRewriteRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var diagnostics = Validate(rules);
        var invalid = diagnostics.Where(d => !d.IsValid).ToList();
        if (invalid.Count > 0)
        {
            throw new ArgumentException(
                "Invalid rewrite pattern(s): " + string.Join("; ", invalid.Select(d => $"rule {d.RuleIndex}: {d.Reason}")),
                nameof(rules));
        }

        _validRules = rules;
        _counters = new int[rules.Count];
    }

    /// <summary>Per-rule rewrite counters accumulated since construction.</summary>
    public IReadOnlyList<RewriteRuleCounter> Counters
    {
        get
        {
            var result = new List<RewriteRuleCounter>(_validRules.Count);
            for (var i = 0; i < _validRules.Count; i++)
            {
                result.Add(new RewriteRuleCounter(i, _validRules[i].Pattern, _counters[i]));
            }

            return result;
        }
    }

    /// <summary>
    /// Validates every rule pattern at configuration time: each must compile
    /// as a regular expression under the evaluation time bound. Disabled rules
    /// are still validated so enabling them later cannot introduce a bad pattern.
    /// </summary>
    /// <param name="rules">Rewrite rules in application order.</param>
    /// <returns>One diagnostic per rule, in order.</returns>
    public static IReadOnlyList<RewriteRuleDiagnostic> Validate(IReadOnlyList<UrlRewriteRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var diagnostics = new List<RewriteRuleDiagnostic>(rules.Count);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            try
            {
                _ = new Regex(rule.Pattern, RegexOptions.None, PatternTimeout);
                diagnostics.Add(new RewriteRuleDiagnostic(i, rule.Pattern, true, null));
            }
            catch (ArgumentException ex)
            {
                diagnostics.Add(new RewriteRuleDiagnostic(i, rule.Pattern, false, ex.Message));
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// Applies the enabled rules deterministically in order through the locked
    /// rewrite engine, one rule at a time so per-rule counters are exact. A rule
    /// whose result is not an absolute http(s) URL is ignored (the scheme
    /// invariant of §10.4); the previous URL is kept.
    /// </summary>
    /// <param name="url">Input URL.</param>
    /// <param name="engine">The locked rewrite engine (sibling implementation or test double).</param>
    /// <returns>The rewritten URL, or the input when no rule changed it validly.</returns>
    public string Apply(string url, IUrlRewriteEngine engine)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(engine);

        var current = url;
        for (var i = 0; i < _validRules.Count; i++)
        {
            var rule = _validRules[i];
            if (!rule.Enabled)
            {
                continue;
            }

            var rewritten = engine.Rewrite(current, new[] { rule });
            if (string.Equals(rewritten, current, StringComparison.Ordinal))
            {
                continue;
            }

            if (!ListInputParser.TryValidateUrl(rewritten, out _))
            {
                // §10.4: a rewrite may never produce a non-http(s) scheme.
                continue;
            }

            current = rewritten;
            _counters[i]++;
        }

        return current;
    }
}

/// <summary>
/// Configuration-time validation diagnostic of one rewrite rule.
/// </summary>
/// <param name="RuleIndex">Zero-based position of the rule in application order.</param>
/// <param name="Pattern">The rule pattern.</param>
/// <param name="IsValid">Whether the pattern compiled under the time bound.</param>
/// <param name="Reason">Rejection reason when invalid; otherwise null.</param>
public sealed record RewriteRuleDiagnostic(int RuleIndex, string Pattern, bool IsValid, string? Reason);
