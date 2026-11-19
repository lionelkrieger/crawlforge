using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Configurable check flags for the <c>redirects.inappropriate</c> rule.
/// SPEC GAP (loop §8): the architecture names CAP-B-051 ("Redirects:
/// inappropriate redirects") but defines no checks anywhere; the phase-09
/// lead recorded the gap for the owner. These flags are an explicit rule
/// definition — NOT spec-derived — pending owner confirmation. Defaults:
/// cross-host target + temporary status.
/// </summary>
[Flags]
public enum RedirectInappropriatenessChecks
{
    /// <summary>No checks (the rule never fires).</summary>
    None = 0,

    /// <summary>A hop redirects to a different host (possible equity leak / misconfiguration).</summary>
    CrossHost = 1,

    /// <summary>A hop uses a temporary redirect status (302/303/307) where a permanent one may be intended.</summary>
    TemporaryStatus = 2,

    /// <summary>All documented default checks.</summary>
    All = CrossHost | TemporaryStatus,
}

/// <summary>
/// One fired inappropriateness check on one hop.
/// </summary>
/// <param name="HopOrdinal">Zero-based hop position.</param>
/// <param name="Check">The check that fired.</param>
/// <param name="FromUrl">Hop source URL.</param>
/// <param name="ToUrl">Hop target URL.</param>
/// <param name="StatusCode">Hop redirect status.</param>
/// <param name="Description">Human-readable reason.</param>
public sealed record RedirectInappropriatenessReason(
    int HopOrdinal,
    RedirectInappropriatenessChecks Check,
    string FromUrl,
    string ToUrl,
    int StatusCode,
    string Description);

/// <summary>
/// Evaluates redirect chains against the configurable inappropriateness
/// checks (CAP-B-051; documented rule definition pending owner confirmation —
/// see <see cref="RedirectInappropriatenessChecks"/>). Pure and stateless.
/// </summary>
public static class RedirectAppropriatenessEvaluator
{
    /// <summary>
    /// Evaluates every hop of a chain.
    /// </summary>
    /// <param name="chain">Recorded hops (any order; evaluated in ordinal order).</param>
    /// <param name="checks">Enabled checks; defaults to <see cref="RedirectInappropriatenessChecks.All"/>.</param>
    /// <returns>Fired reasons in hop order (empty when the chain is appropriate).</returns>
    public static IReadOnlyList<RedirectInappropriatenessReason> Evaluate(
        IReadOnlyList<RedirectHop> chain,
        RedirectInappropriatenessChecks checks = RedirectInappropriatenessChecks.All)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var reasons = new List<RedirectInappropriatenessReason>();
        if (checks == RedirectInappropriatenessChecks.None)
        {
            return reasons;
        }

        foreach (RedirectHop hop in chain.OrderBy(h => h.Ordinal))
        {
            if (checks.HasFlag(RedirectInappropriatenessChecks.CrossHost)
                && Uri.TryCreate(hop.FromUrl, UriKind.Absolute, out Uri? from)
                && Uri.TryCreate(hop.ToUrl, UriKind.Absolute, out Uri? to)
                && !string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add(new RedirectInappropriatenessReason(
                    hop.Ordinal, RedirectInappropriatenessChecks.CrossHost, hop.FromUrl, hop.ToUrl, hop.StatusCode,
                    $"Hop {hop.Ordinal} redirects cross-host ('{from.Host}' -> '{to.Host}')."));
            }

            if (checks.HasFlag(RedirectInappropriatenessChecks.TemporaryStatus)
                && hop.StatusCode is 302 or 303 or 307)
            {
                reasons.Add(new RedirectInappropriatenessReason(
                    hop.Ordinal, RedirectInappropriatenessChecks.TemporaryStatus, hop.FromUrl, hop.ToUrl, hop.StatusCode,
                    $"Hop {hop.Ordinal} uses temporary redirect status {hop.StatusCode}; use 301/308 when the move is permanent."));
            }
        }

        return reasons;
    }
}
