using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Emits redirect findings from recorded chains and chain violations
/// (CAP-A-010/011, CAP-B-049/050/051): <c>redirects.loop</c> (Error),
/// <c>redirects.chain</c> (Warning for 2+ hops; Error on chain-cap violation),
/// and <c>redirects.inappropriate</c> (Warning; configurable check set —
/// documented rule definition pending owner confirmation, see
/// <see cref="RedirectInappropriatenessChecks"/>). Pure component: facts in,
/// <see cref="FindingDto"/> drafts out (Id 0); the engine persists them
/// (WP-09-11 wiring). Evidence carries the full hop chain in the canonical
/// <see cref="RedirectChainJson"/> shape. Stateless; safe under concurrency.
/// </summary>
public sealed class RedirectFindingAnalyzer
{
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Analyzes a completed fetch's chain for the source URL.
    /// </summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="urlId">Source url_records identifier.</param>
    /// <param name="originalUrl">Source canonical URL.</param>
    /// <param name="chain">Recorded hops (empty emits nothing).</param>
    /// <param name="finalUrl">Terminal URL, when known.</param>
    /// <param name="finalStatusCode">Terminal HTTP status, when known.</param>
    /// <param name="checks">Enabled inappropriateness checks.</param>
    /// <returns>Finding drafts (chain reporting + inappropriateness).</returns>
    public IReadOnlyList<FindingDto> AnalyzeChain(
        long auditId,
        long urlId,
        string originalUrl,
        IReadOnlyList<RedirectHop> chain,
        string? finalUrl = null,
        int? finalStatusCode = null,
        RedirectInappropriatenessChecks checks = RedirectInappropriatenessChecks.All)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var findings = new List<FindingDto>();
        if (chain.Count == 0)
        {
            return findings;
        }

        var ordered = chain.OrderBy(h => h.Ordinal).ToList();
        var chainJson = JsonSerializer.Deserialize<JsonElement>(RedirectChainJson.SerializeHops(ordered));

        if (ordered.Count >= 2)
        {
            findings.Add(Finding(auditId, RedirectRuleKeys.Chain, FindingSeverity.Warning, urlId, JsonSerializer.Serialize(new
            {
                url = originalUrl,
                urlId,
                hopCount = ordered.Count,
                chain = chainJson,
                finalUrl,
                finalStatusCode,
            }, EvidenceJson)));
        }

        IReadOnlyList<RedirectInappropriatenessReason> reasons = RedirectAppropriatenessEvaluator.Evaluate(ordered, checks);
        if (reasons.Count > 0)
        {
            findings.Add(Finding(auditId, RedirectRuleKeys.Inappropriate, FindingSeverity.Warning, urlId, JsonSerializer.Serialize(new
            {
                url = originalUrl,
                urlId,
                hopCount = ordered.Count,
                chain = chainJson,
                reasons = reasons.Select(r => new
                {
                    hopOrdinal = r.HopOrdinal,
                    check = CheckName(r.Check),
                    fromUrl = r.FromUrl,
                    toUrl = r.ToUrl,
                    statusCode = r.StatusCode,
                    description = r.Description,
                }).ToList(),
            }, EvidenceJson)));
        }

        return findings;
    }

    /// <summary>
    /// Emits the finding for a recorder chain violation (loop or hop cap),
    /// retaining the observed hop prefix as evidence.
    /// </summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="urlId">Source url_records identifier.</param>
    /// <param name="originalUrl">Source canonical URL.</param>
    /// <param name="violation">The recorder violation.</param>
    /// <param name="partialChain">Hops recorded before the violation.</param>
    /// <returns>A single Error finding draft.</returns>
    public FindingDto AnalyzeViolation(
        long auditId,
        long urlId,
        string originalUrl,
        RedirectChainException violation,
        IReadOnlyList<RedirectHop> partialChain)
    {
        ArgumentNullException.ThrowIfNull(violation);
        ArgumentNullException.ThrowIfNull(partialChain);
        return ViolationFinding(auditId, urlId, originalUrl, violation.Reason, violation.Message, partialChain);
    }

    /// <summary>
    /// Emits the finding for a fetcher-level redirect failure (the fetcher's
    /// visited-URL guard raises <see cref="FetchFailedException"/> with
    /// <see cref="FetchFailureKind.RedirectLoop"/> /
    /// <see cref="FetchFailureKind.RedirectChainExceeded"/> when no recorder
    /// context is set). Non-redirect failure kinds emit nothing.
    /// </summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="urlId">Source url_records identifier.</param>
    /// <param name="originalUrl">Source canonical URL.</param>
    /// <param name="failure">The classified fetch failure.</param>
    /// <param name="partialChain">Hops recorded before the failure (may be empty).</param>
    /// <returns>The finding draft, or null for non-redirect failures.</returns>
    public FindingDto? AnalyzeFetchFailure(
        long auditId,
        long urlId,
        string originalUrl,
        FetchFailedException failure,
        IReadOnlyList<RedirectHop> partialChain)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(partialChain);
        return failure.Kind switch
        {
            FetchFailureKind.RedirectLoop => ViolationFinding(auditId, urlId, originalUrl, RedirectChainViolation.Loop, failure.Message, partialChain),
            FetchFailureKind.RedirectChainExceeded => ViolationFinding(auditId, urlId, originalUrl, RedirectChainViolation.ChainCapExceeded, failure.Message, partialChain),
            _ => null,
        };
    }

    private FindingDto ViolationFinding(
        long auditId,
        long urlId,
        string originalUrl,
        RedirectChainViolation reason,
        string message,
        IReadOnlyList<RedirectHop> partialChain)
    {
        var ordered = partialChain.OrderBy(h => h.Ordinal).ToList();
        var ruleKey = reason == RedirectChainViolation.Loop ? RedirectRuleKeys.Loop : RedirectRuleKeys.Chain;
        var violationName = reason == RedirectChainViolation.Loop ? "loop" : "chain-cap-exceeded";
        return Finding(auditId, ruleKey, FindingSeverity.Error, urlId, JsonSerializer.Serialize(new
        {
            url = originalUrl,
            urlId,
            violation = violationName,
            message,
            hopCount = ordered.Count,
            chain = JsonSerializer.Deserialize<JsonElement>(RedirectChainJson.SerializeHops(ordered)),
        }, EvidenceJson));
    }

    private static string CheckName(RedirectInappropriatenessChecks check) => check switch
    {
        RedirectInappropriatenessChecks.CrossHost => "cross-host",
        RedirectInappropriatenessChecks.TemporaryStatus => "temporary-status",
        _ => check.ToString(),
    };

    private static FindingDto Finding(long auditId, string ruleKey, FindingSeverity severity, long urlId, string evidenceJson) =>
        new(
            Id: 0,
            AuditId: auditId,
            RuleKey: ruleKey,
            Category: AuditCategory.Indexability,
            Severity: severity,
            UrlId: urlId,
            EvidenceJson: evidenceJson,
            RecommendationKey: null,
            IsSuppressed: false);
}
