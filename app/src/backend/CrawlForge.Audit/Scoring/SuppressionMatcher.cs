using CrawlForge.Contracts.Entities;

namespace CrawlForge.Audit.Scoring;

/// <summary>
/// Suppression coverage matching (FS-CRAWL-001 §7.5), aligned EXACTLY with the
/// accepted pipeline keystone's <c>FindingEmitter.IsSuppressed</c> semantics:
/// a suppression covers a finding when every declared dimension matches —
/// rule key (null = any rule), URL scope (null = any URL), and free-form
/// finding scope (matched against the rule key). Inactive rows
/// (<c>IsSuppressed = false</c>) and rows with no declared dimension
/// (accidental blanket suppression guard) cover nothing. Matching is ordinal.
/// </summary>
public static class SuppressionMatcher
{
    /// <summary>
    /// Tests whether one suppression covers a finding identified by rule key
    /// and optional URL reference. Mirrors the keystone per-suppression logic.
    /// </summary>
    /// <param name="suppression">Suppression to test.</param>
    /// <param name="ruleKey">Finding rule key.</param>
    /// <param name="urlId">Finding URL reference, if any.</param>
    /// <returns>True when the suppression covers the finding.</returns>
    public static bool Covers(FindingSuppressionDto suppression, string ruleKey, long? urlId)
    {
        ArgumentNullException.ThrowIfNull(suppression);
        ArgumentNullException.ThrowIfNull(ruleKey);

        if (!suppression.IsSuppressed)
        {
            return false;
        }

        var hasDimension = suppression.RuleKey is not null
            || suppression.UrlId is not null
            || suppression.FindingScope is not null;
        if (!hasDimension)
        {
            return false;
        }

        var ruleMatches = suppression.RuleKey is null ||
            string.Equals(suppression.RuleKey, ruleKey, StringComparison.Ordinal);
        var urlMatches = suppression.UrlId is null || suppression.UrlId == urlId;
        var scopeMatches = suppression.FindingScope is null ||
            string.Equals(suppression.FindingScope, ruleKey, StringComparison.Ordinal);

        return ruleMatches && urlMatches && scopeMatches;
    }

    /// <summary>
    /// Tests whether any active suppression in the set covers the finding.
    /// Mirrors the keystone set-level logic used at emission time.
    /// </summary>
    /// <param name="ruleKey">Finding rule key.</param>
    /// <param name="urlId">Finding URL reference, if any.</param>
    /// <param name="suppressions">Project suppressions (any state).</param>
    /// <returns>True when an active suppression covers the finding.</returns>
    public static bool IsSuppressed(string ruleKey, long? urlId, IEnumerable<FindingSuppressionDto> suppressions)
    {
        ArgumentNullException.ThrowIfNull(suppressions);
        foreach (var suppression in suppressions)
        {
            if (Covers(suppression, ruleKey, urlId))
            {
                return true;
            }
        }

        return false;
    }
}
