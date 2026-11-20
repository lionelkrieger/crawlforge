using System.Globalization;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Politeness;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// WAF-to-AIMD trigger (FS-CRAWL-002 §2.5/§3.2/§3.4, gap G07): turns a WAF
/// verdict plus the observed fetch into the AIMD response observation the
/// controller consumes. A corroborated challenge sets <c>IsChallenge=true</c>
/// on the locked <see cref="HostResponseSample"/>, which makes the controller
/// apply the multiplicative decrease to rate and concurrency with trigger
/// <c>waf-soft-block</c> and enter cooldown — emitted through the existing
/// HOTFIX-01-locked host-throttle event shape
/// (rateBefore/rateAfter/concurrencyBefore/concurrencyAfter). The observation
/// also carries the Retry-After header value and the full response latency
/// (concurrency coupling). Detection feeds politeness only — no challenge
/// solving or evasion (exclusion E3).
/// </summary>
public static class WafAimdTrigger
{
    /// <summary>
    /// Builds the AIMD observation for one fetched response and its WAF verdict.
    /// </summary>
    /// <param name="fetch">The fetch result (TTFB, status, headers, total duration).</param>
    /// <param name="verdict">The WAF evaluation verdict for the response.</param>
    /// <param name="observedAt">Observation instant (UTC).</param>
    /// <returns>The observation to feed to the AIMD controller.</returns>
    public static AimdResponseObservation BuildObservation(
        HttpFetchResult fetch,
        WafEvaluationResult verdict,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(verdict);
        var sample = new HostResponseSample(
            fetch.Ttfb.TotalMilliseconds,
            fetch.StatusCode,
            verdict.IsChallenge,
            observedAt);
        return new AimdResponseObservation(
            sample,
            RetryAfterParser.Parse(fetch.Headers, observedAt),
            fetch.TotalDuration.TotalMilliseconds);
    }

    /// <summary>
    /// Records the response observation on the per-audit AIMD controller. A
    /// challenge verdict triggers the §3.4 multiplicative decrease
    /// (waf-soft-block); a Retry-After header pauses the host for
    /// max(retry-after, D). Call this instead of recording a hardcoded
    /// <c>IsChallenge:false</c> sample (the G07 defect).
    /// </summary>
    /// <remarks>
    /// Low-level primitive: it feeds the observation unconditionally. The
    /// engine's WAF record path consults <see cref="ResolveAction"/> first and
    /// uses <see cref="RecordVerdict"/> so hard blocks stop the host instead of
    /// merely throttling (phase-10 D-021 five-class policy).
    /// </remarks>
    /// <param name="controller">The per-audit AIMD controller.</param>
    /// <param name="host">Target host of the response.</param>
    /// <param name="fetch">The fetch result.</param>
    /// <param name="verdict">The WAF verdict.</param>
    /// <param name="observedAt">Observation instant (UTC).</param>
    public static void Record(
        AimdPolitenessController controller,
        string host,
        HttpFetchResult fetch,
        WafEvaluationResult verdict,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        controller.RecordResponse(host, BuildObservation(fetch, verdict, observedAt));
    }

    /// <summary>
    /// Resolves the five-class politeness response for a verdict (D-021):
    /// soft blocks (managed-challenge, rate-limit-soft-block) throttle through
    /// AIMD; hard blocks (captcha, interstitial, botblock) stop the host; a
    /// non-challenge carries no WAF response.
    /// </summary>
    /// <param name="verdict">The WAF verdict.</param>
    /// <returns>The politeness response the engine must apply.</returns>
    public static WafBlockResponse ResolveAction(WafEvaluationResult verdict) => WafBlockPolicy.Resolve(verdict);

    /// <summary>
    /// Class-aware recording for the engine's WAF record path (D-021). Resolves
    /// the verdict's response and records the AIMD observation for every case
    /// EXCEPT a hard block: a hard block returns <see cref="WafBlockResponse.HostStop"/>
    /// without touching the controller — the engine stops the host (no further
    /// fetches), which takes precedence over throttling. Soft blocks and
    /// unclassified challenges record the challenge observation (multiplicative
    /// decrease, waf-soft-block); healthy responses record the normal sample
    /// (additive increase). The returned response tells the engine whether to
    /// stop the host.
    /// </summary>
    /// <param name="controller">The per-audit AIMD controller.</param>
    /// <param name="host">Target host of the response.</param>
    /// <param name="fetch">The fetch result.</param>
    /// <param name="verdict">The WAF verdict.</param>
    /// <param name="observedAt">Observation instant (UTC).</param>
    /// <returns>The resolved response; HostStop means the engine must stop the host.</returns>
    public static WafBlockResponse RecordVerdict(
        AimdPolitenessController controller,
        string host,
        HttpFetchResult fetch,
        WafEvaluationResult verdict,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(verdict);
        var response = WafBlockPolicy.Resolve(verdict);
        if (response != WafBlockResponse.HostStop)
        {
            controller.RecordResponse(host, BuildObservation(fetch, verdict, observedAt));
        }

        return response;
    }
}

/// <summary>
/// Parses the Retry-After response header (FS-CRAWL-002 §3.4): delta-seconds
/// or an HTTP-date. Absent or unparseable values yield null (no pause).
/// </summary>
public static class RetryAfterParser
{
    /// <summary>
    /// Parses Retry-After from response headers.
    /// </summary>
    /// <param name="headers">Response headers (any key casing).</param>
    /// <param name="now">Reference instant for HTTP-date values.</param>
    /// <returns>Delay in seconds (non-negative), or null.</returns>
    public static double? Parse(IReadOnlyDictionary<string, string>? headers, DateTimeOffset now)
    {
        if (headers is null)
        {
            return null;
        }

        string? value = null;
        foreach (var (key, headerValue) in headers)
        {
            if (string.Equals(key, "Retry-After", StringComparison.OrdinalIgnoreCase))
            {
                value = headerValue;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return Math.Max(0, seconds);
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var date))
        {
            return Math.Max(0, (date - now).TotalSeconds);
        }

        return null;
    }
}
