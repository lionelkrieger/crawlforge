using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Configurable check flags for the inappropriate-redirect rule (CR-029,
/// applied; FS-CRAWL-001 §7.4 "Inappropriate redirects" paragraph, D-021).
/// The redirect check flags four cases, each a separate toggle on the
/// configuration's analysis tab, all on by default. The flags mirror the
/// locked <see cref="RedirectCheckSettings"/> document one-for-one;
/// <see cref="FromSettings"/> maps the configuration wire document (absent/null
/// = all four enabled).
/// </summary>
[Flags]
public enum RedirectInappropriatenessChecks
{
    /// <summary>No checks (the rule never fires).</summary>
    None = 0,

    /// <summary>Case 1: a hop redirects to a different host.</summary>
    CrossHost = 1,

    /// <summary>Case 2: a hop uses a temporary redirect status (302/303/307).</summary>
    TemporaryStatus = 2,

    /// <summary>Case 3: the final target is non-indexable per CR-024
    /// (search-indexable semantics, including canonical-elsewhere).</summary>
    NonIndexableTarget = 4,

    /// <summary>Case 4: an HTTPS URL redirects to HTTP.</summary>
    HttpsDowngrade = 8,

    /// <summary>All four CR-029 checks (the default when the document is absent).</summary>
    All = CrossHost | TemporaryStatus | NonIndexableTarget | HttpsDowngrade,
}

/// <summary>
/// Maps the locked <see cref="RedirectCheckSettings"/> configuration document
/// onto the evaluator's check flags (CR-029: absent/null document = all ON).
/// </summary>
public static class RedirectInappropriatenessChecksExtensions
{
    /// <summary>
    /// Converts a configuration redirect-checks document to check flags.
    /// A null document enables all four checks (the CR-029 default).
    /// </summary>
    /// <param name="settings">The configuration document, or null.</param>
    /// <returns>The enabled check flags.</returns>
    public static RedirectInappropriatenessChecks FromSettings(RedirectCheckSettings? settings)
    {
        if (settings is null)
        {
            return RedirectInappropriatenessChecks.All;
        }

        var checks = RedirectInappropriatenessChecks.None;
        if (settings.CrossHost)
        {
            checks |= RedirectInappropriatenessChecks.CrossHost;
        }

        if (settings.TemporaryStatus)
        {
            checks |= RedirectInappropriatenessChecks.TemporaryStatus;
        }

        if (settings.NonIndexableTarget)
        {
            checks |= RedirectInappropriatenessChecks.NonIndexableTarget;
        }

        if (settings.HttpsDowngrade)
        {
            checks |= RedirectInappropriatenessChecks.HttpsDowngrade;
        }

        return checks;
    }
}

/// <summary>
/// Final-target facts for the CR-029 non-indexable-target case (case 3).
/// The redirect evaluator lives in the crawler layer and cannot consume the
/// audit layer's <c>IndexabilityStateEvaluator</c>; it applies the same CR-024
/// search-indexable semantics to these facts (parity is proven by component
/// test). All directive facts are optional: with only the final status known,
/// the case still fires on a non-200 target; noindex/robots/canonical facts
/// refine the judgment when the engine supplies them.
/// </summary>
/// <param name="FinalStatusCode">Final HTTP status of the redirect target; null when unknown.</param>
/// <param name="FinalUrl">Final target URL (base for canonical resolution).</param>
/// <param name="RobotsBlocked">Robots exclusion verdict for the target.</param>
/// <param name="MetaRobotsNoindex">The target's meta robots carry noindex/none under any agent.</param>
/// <param name="HeaderRobotsNoindex">The target's X-Robots-Tag carries noindex/none under any agent.</param>
/// <param name="CanonicalHrefs">The target's declared canonical references (HTML first, then header).</param>
public sealed record RedirectTargetIndexabilityFacts(
    int? FinalStatusCode,
    string? FinalUrl = null,
    bool RobotsBlocked = false,
    bool MetaRobotsNoindex = false,
    bool HeaderRobotsNoindex = false,
    IReadOnlyList<string>? CanonicalHrefs = null);

/// <summary>
/// One fired inappropriateness check on one hop (or on the final target for
/// the non-indexable-target case).
/// </summary>
/// <param name="HopOrdinal">Zero-based hop position (the final hop for case 3).</param>
/// <param name="Check">The check that fired.</param>
/// <param name="FromUrl">Hop source URL.</param>
/// <param name="ToUrl">Hop target URL (the final target for case 3).</param>
/// <param name="StatusCode">Hop redirect status (the final status for case 3).</param>
/// <param name="Description">Human-readable reason naming the case, hop and target.</param>
public sealed record RedirectInappropriatenessReason(
    int HopOrdinal,
    RedirectInappropriatenessChecks Check,
    string FromUrl,
    string ToUrl,
    int StatusCode,
    string Description);

/// <summary>
/// Evaluates redirect chains against the four CR-029 inappropriateness cases
/// (FS-CRAWL-001 §7.4 as amended by CR-029, D-021): cross-host hop, temporary
/// redirect status, non-indexable final target (CR-024 semantics) and
/// HTTPS→HTTP downgrade. Pure and stateless; each enabled case is independent
/// (the configuration's analysis tab toggles them one by one).
/// </summary>
public static class RedirectAppropriatenessEvaluator
{
    /// <summary>
    /// Evaluates every hop of a chain plus the final-target case.
    /// </summary>
    /// <param name="chain">Recorded hops (any order; evaluated in ordinal order).</param>
    /// <param name="checks">Enabled checks; defaults to all four CR-029 cases.</param>
    /// <param name="targetFacts">Final-target indexability facts for case 3;
    /// null skips the case (the judgment is never guessed).</param>
    /// <returns>Fired reasons in hop order (empty when the chain is appropriate).</returns>
    public static IReadOnlyList<RedirectInappropriatenessReason> Evaluate(
        IReadOnlyList<RedirectHop> chain,
        RedirectInappropriatenessChecks checks = RedirectInappropriatenessChecks.All,
        RedirectTargetIndexabilityFacts? targetFacts = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var reasons = new List<RedirectInappropriatenessReason>();
        if (checks == RedirectInappropriatenessChecks.None)
        {
            return reasons;
        }

        var ordered = chain.OrderBy(h => h.Ordinal).ToList();
        foreach (RedirectHop hop in ordered)
        {
            if (checks.HasFlag(RedirectInappropriatenessChecks.CrossHost)
                && Uri.TryCreate(hop.FromUrl, UriKind.Absolute, out Uri? from)
                && Uri.TryCreate(hop.ToUrl, UriKind.Absolute, out Uri? to)
                && !string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add(new RedirectInappropriatenessReason(
                    hop.Ordinal, RedirectInappropriatenessChecks.CrossHost, hop.FromUrl, hop.ToUrl, hop.StatusCode,
                    $"Case cross-host: hop {hop.Ordinal} redirects to a different host ('{from.Host}' -> '{to.Host}'); final target '{hop.ToUrl}'."));
            }

            if (checks.HasFlag(RedirectInappropriatenessChecks.TemporaryStatus)
                && hop.StatusCode is 302 or 303 or 307)
            {
                reasons.Add(new RedirectInappropriatenessReason(
                    hop.Ordinal, RedirectInappropriatenessChecks.TemporaryStatus, hop.FromUrl, hop.ToUrl, hop.StatusCode,
                    $"Case temporary-status: hop {hop.Ordinal} uses temporary redirect status {hop.StatusCode} towards '{hop.ToUrl}'; use 301/308 when the move is permanent."));
            }

            if (checks.HasFlag(RedirectInappropriatenessChecks.HttpsDowngrade)
                && Uri.TryCreate(hop.FromUrl, UriKind.Absolute, out Uri? downgradeFrom)
                && Uri.TryCreate(hop.ToUrl, UriKind.Absolute, out Uri? downgradeTo)
                && string.Equals(downgradeFrom.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && string.Equals(downgradeTo.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add(new RedirectInappropriatenessReason(
                    hop.Ordinal, RedirectInappropriatenessChecks.HttpsDowngrade, hop.FromUrl, hop.ToUrl, hop.StatusCode,
                    $"Case https-downgrade: hop {hop.Ordinal} redirects from HTTPS to HTTP ('{hop.FromUrl}' -> '{hop.ToUrl}')."));
            }
        }

        if (checks.HasFlag(RedirectInappropriatenessChecks.NonIndexableTarget) && targetFacts is not null)
        {
            var disqualifier = DescribeNonIndexableTarget(targetFacts);
            if (disqualifier is not null)
            {
                var lastHop = ordered.Count > 0 ? ordered[^1] : null;
                var toUrl = targetFacts.FinalUrl ?? lastHop?.ToUrl ?? string.Empty;
                reasons.Add(new RedirectInappropriatenessReason(
                    lastHop?.Ordinal ?? 0,
                    RedirectInappropriatenessChecks.NonIndexableTarget,
                    lastHop?.FromUrl ?? toUrl,
                    toUrl,
                    targetFacts.FinalStatusCode ?? lastHop?.StatusCode ?? 0,
                    $"Case non-indexable-target: the final target '{toUrl}' is not search-indexable per CR-024 ({disqualifier})."));
            }
        }

        return reasons;
    }

    /// <summary>
    /// CR-024 search-indexable semantics over the final-target facts (parity
    /// with the audit layer's IndexabilityStateEvaluator): exactly-200 status,
    /// robots allowed, no noindex in meta robots OR X-Robots-Tag, canonical
    /// self-referencing or absent (multiple distinct targets and unresolvable
    /// canonicals also disqualify). Returns the first disqualifying
    /// observation, or null when the target is search-indexable.
    /// </summary>
    private static string? DescribeNonIndexableTarget(RedirectTargetIndexabilityFacts facts)
    {
        if (facts.FinalStatusCode is not 200)
        {
            return facts.FinalStatusCode is { } status
                ? $"final status {status} is not 200"
                : "final status is unknown";
        }

        if (facts.RobotsBlocked)
        {
            return "robots exclusion blocks crawling";
        }

        if (facts.MetaRobotsNoindex)
        {
            return "meta robots carry noindex";
        }

        if (facts.HeaderRobotsNoindex)
        {
            return "X-Robots-Tag carries noindex";
        }

        var canonicals = facts.CanonicalHrefs;
        if (canonicals is { Count: > 0 })
        {
            if (!Uri.TryCreate(facts.FinalUrl, UriKind.Absolute, out Uri? pageUri)
                || (pageUri.Scheme != Uri.UriSchemeHttp && pageUri.Scheme != Uri.UriSchemeHttps))
            {
                return "the page URL is not an absolute http(s) URL, so canonical references cannot resolve";
            }

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var href in canonicals)
            {
                if (!Uri.TryCreate(pageUri, href, out Uri? resolved)
                    || (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps))
                {
                    return "a canonical reference is unresolvable or non-http(s)";
                }

                targets.Add(Normalize(resolved.AbsoluteUri));
            }

            if (targets.Count > 1)
            {
                return "multiple distinct canonical targets (unresolved conflict)";
            }

            var canonical = targets.First();
            if (!string.Equals(canonical, Normalize(pageUri.AbsoluteUri), StringComparison.OrdinalIgnoreCase))
            {
                return "the canonical points to a different URL (canonical elsewhere)";
            }
        }

        return null;
    }

    private static string Normalize(string url) => url.TrimEnd('/');
}
