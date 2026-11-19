using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Classification of one fetch for the ORIGINAL (source) URL record — the
/// probe-2 / G27 defect fix: when redirects were followed, the engine stored
/// the source URL with the FINAL 2xx status and an empty chain. Per FS-CRAWL-001
/// §5.3/§5.4 a redirect source is a <c>redirected</c> URL: its record carries
/// the first hop's 3xx status, the first hop's target, and the full hop chain.
/// Terminal facts (final URL/status) travel alongside for the wiring layer.
/// </summary>
/// <param name="StatusClass">Status class of the source URL record.</param>
/// <param name="SourceStatusCode">HTTP status the source URL itself returned (first hop when redirected).</param>
/// <param name="RedirectTargetUrl">First-hop redirect target, when redirected.</param>
/// <param name="FinalUrl">Terminal URL after redirects (the source URL when none).</param>
/// <param name="FinalStatusCode">Terminal HTTP status, when the fetch completed.</param>
/// <param name="Chain">Full recorded hop chain (empty when no redirect occurred).</param>
/// <param name="IsRedirectSource">Whether the source URL issued a redirect.</param>
public sealed record ClassifiedFetchOutcome(
    CrawlStatusClass StatusClass,
    int? SourceStatusCode,
    string? RedirectTargetUrl,
    string FinalUrl,
    int? FinalStatusCode,
    IReadOnlyList<RedirectHop> Chain,
    bool IsRedirectSource);

/// <summary>
/// Maps fetch results and classified failures to source-URL record facts
/// (FS-CRAWL-001 §5.3 per-URL states, §5.4 hop recording). Pure and stateless;
/// the engine (WP-09-11 wiring) calls this after <c>FetchAsync</c> with the
/// chain read from <see cref="IRedirectChainRecorder.GetChainAsync"/> under the
/// fetch's <see cref="FetchContextScope"/>.
/// </summary>
public static class FetchOutcomeClassifier
{
    /// <summary>
    /// Classifies a completed fetch for the source URL record.
    /// </summary>
    /// <param name="originalUrl">The URL the fetch was issued for (frontier entry).</param>
    /// <param name="result">The fetch result (terminal facts).</param>
    /// <param name="chain">Recorded hop chain of the fetch (empty when none).</param>
    /// <param name="isChallenge">
    /// WAF challenge verdict on the terminal response. Applies only when the
    /// source did not redirect — a challenge behind a redirect belongs to the
    /// terminal URL's record, not the redirect source.
    /// </param>
    /// <returns>The source-URL classification.</returns>
    public static ClassifiedFetchOutcome Classify(
        string originalUrl,
        HttpFetchResult result,
        IReadOnlyList<RedirectHop> chain,
        bool isChallenge = false)
    {
        ArgumentNullException.ThrowIfNull(originalUrl);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(chain);

        if (chain.Count > 0)
        {
            var ordered = chain.OrderBy(h => h.Ordinal).ToList();
            return new ClassifiedFetchOutcome(
                CrawlStatusClass.Redirect,
                ordered[0].StatusCode,
                ordered[0].ToUrl,
                result.FinalUrl,
                result.StatusCode,
                ordered,
                IsRedirectSource: true);
        }

        var statusClass = isChallenge && result.StatusCode is >= 200 and < 300
            ? CrawlStatusClass.Blocked
            : FetchStatusClassifier.Classify(result.StatusCode);
        return new ClassifiedFetchOutcome(
            statusClass,
            result.StatusCode,
            RedirectTargetUrl: null,
            result.FinalUrl,
            result.StatusCode,
            Array.Empty<RedirectHop>(),
            IsRedirectSource: false);
    }

    /// <summary>
    /// Classifies a transport/policy failure for the source URL record.
    /// Redirect-loop and chain-cap failures keep the <c>redirected</c> class
    /// with the observed hop prefix (the source did issue 3xx responses);
    /// other failures are <c>error</c> (FetchError).
    /// </summary>
    /// <param name="originalUrl">The URL the fetch was issued for.</param>
    /// <param name="failure">The classified fetch failure.</param>
    /// <param name="chain">Hops recorded before the failure (may be empty).</param>
    /// <returns>The source-URL classification.</returns>
    public static ClassifiedFetchOutcome ClassifyFailure(
        string originalUrl,
        FetchFailedException failure,
        IReadOnlyList<RedirectHop> chain)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var violation = failure.Kind switch
        {
            FetchFailureKind.RedirectLoop => RedirectChainViolation.Loop,
            FetchFailureKind.RedirectChainExceeded => RedirectChainViolation.ChainCapExceeded,
            _ => (RedirectChainViolation?)null,
        };
        return ClassifyViolation(originalUrl, violation, chain);
    }

    /// <summary>
    /// Classifies a recorder chain violation (loop / hop cap) for the source
    /// URL record, retaining the observed hop prefix.
    /// </summary>
    /// <param name="originalUrl">The URL the fetch was issued for.</param>
    /// <param name="violation">The violation, or null for a non-redirect failure.</param>
    /// <param name="chain">Hops recorded before the violation (may be empty).</param>
    /// <returns>The source-URL classification.</returns>
    public static ClassifiedFetchOutcome ClassifyViolation(
        string originalUrl,
        RedirectChainViolation? violation,
        IReadOnlyList<RedirectHop> chain)
    {
        ArgumentNullException.ThrowIfNull(originalUrl);
        ArgumentNullException.ThrowIfNull(chain);
        var ordered = chain.OrderBy(h => h.Ordinal).ToList();

        if (violation is null)
        {
            return new ClassifiedFetchOutcome(
                CrawlStatusClass.FetchError,
                SourceStatusCode: null,
                RedirectTargetUrl: null,
                originalUrl,
                FinalStatusCode: null,
                ordered,
                IsRedirectSource: false);
        }

        return new ClassifiedFetchOutcome(
            CrawlStatusClass.Redirect,
            ordered.Count > 0 ? ordered[0].StatusCode : null,
            ordered.Count > 0 ? ordered[0].ToUrl : null,
            originalUrl,
            FinalStatusCode: null,
            ordered,
            IsRedirectSource: ordered.Count > 0);
    }
}
