using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Emits redirect findings from recorded chains and chain violations
/// (CAP-A-010/011, CAP-B-049/050/051): <c>redirects.loop</c> (Error),
/// <c>redirects.chain</c> (Warning for 2+ hops; Error on chain-cap violation),
/// and the four CR-029 inappropriate-redirect case findings
/// (<c>redirects.inappropriate-{cross-host,temporary-status,non-indexable-target,
/// https-downgrade}</c>, Warning; each case an independent configuration
/// toggle, all on by default — absent/null <see cref="RedirectCheckSettings"/>
/// = all ON). Every case finding names the case, the triggering hop and the
/// final target. Pure component: facts in, <see cref="FindingDto"/> drafts out
/// (Id 0); the engine persists them. Evidence carries the full hop chain in
/// the canonical <see cref="RedirectChainJson"/> shape. Stateless; safe under
/// concurrency.
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
    /// <param name="checks">Enabled inappropriateness checks (map the configuration's
    /// <see cref="RedirectCheckSettings"/> with
    /// <see cref="RedirectInappropriatenessChecksExtensions.FromSettings"/>).</param>
    /// <param name="targetFacts">Optional final-target indexability facts for the
    /// non-indexable-target case (noindex/robots/canonical). When null, the terminal
    /// status/URL alone drive the case (non-200 targets still fire).</param>
    /// <returns>Finding drafts (chain reporting + one finding per fired case).</returns>
    public IReadOnlyList<FindingDto> AnalyzeChain(
        long auditId,
        long urlId,
        string originalUrl,
        IReadOnlyList<RedirectHop> chain,
        string? finalUrl = null,
        int? finalStatusCode = null,
        RedirectInappropriatenessChecks checks = RedirectInappropriatenessChecks.All,
        RedirectTargetIndexabilityFacts? targetFacts = null)
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

        // CR-029 case 3 facts: explicit facts win; otherwise the terminal
        // status/URL alone still express the CR-024 status semantics. With no
        // terminal facts at all the case cannot be judged and stays silent.
        var facts = targetFacts;
        if (facts is null && (finalUrl is not null || finalStatusCode is not null))
        {
            facts = new RedirectTargetIndexabilityFacts(finalStatusCode, finalUrl);
        }

        IReadOnlyList<RedirectInappropriatenessReason> reasons = RedirectAppropriatenessEvaluator.Evaluate(ordered, checks, facts);
        foreach (var caseGroup in reasons.GroupBy(r => r.Check).OrderBy(g => g.Key))
        {
            var caseReasons = caseGroup.ToList();
            var triggering = caseReasons[0];
            findings.Add(Finding(auditId, RedirectRuleKeys.ForCase(caseGroup.Key), FindingSeverity.Warning, urlId, JsonSerializer.Serialize(new
            {
                url = originalUrl,
                urlId,
                @case = RedirectRuleKeys.CaseName(caseGroup.Key),
                hopCount = ordered.Count,
                chain = chainJson,
                finalUrl = finalUrl ?? ordered[^1].ToUrl,
                finalStatusCode,
                triggeringHop = new
                {
                    hopOrdinal = triggering.HopOrdinal,
                    fromUrl = triggering.FromUrl,
                    toUrl = triggering.ToUrl,
                    statusCode = triggering.StatusCode,
                },
                reasons = caseReasons.Select(r => new
                {
                    hopOrdinal = r.HopOrdinal,
                    check = RedirectRuleKeys.CaseName(r.Check),
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
    /// Emits the violation finding for a chain that failed mid-fetch
    /// (loop or chain-cap exceeded).
    /// </summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="urlId">Source url_records identifier.</param>
    /// <param name="originalUrl">Source canonical URL.</param>
    /// <param name="violation">The chain violation.</param>
    /// <param name="partialChain">Hops recorded before the failure.</param>
    /// <returns>The violation finding (Error).</returns>
    public FindingDto AnalyzeViolation(
        long auditId,
        long urlId,
        string originalUrl,
        RedirectChainException violation,
        IReadOnlyList<RedirectHop> partialChain)
    {
        ArgumentNullException.ThrowIfNull(violation);
        ArgumentNullException.ThrowIfNull(partialChain);
        var ruleKey = violation.Reason == RedirectChainViolation.Loop ? RedirectRuleKeys.Loop : RedirectRuleKeys.Chain;
        return Finding(auditId, ruleKey, FindingSeverity.Error, urlId, JsonSerializer.Serialize(new
        {
            url = originalUrl,
            urlId,
            violation = violation.Reason == RedirectChainViolation.Loop ? "loop" : "chain-cap-exceeded",
            hopCount = partialChain.Count,
            chain = JsonSerializer.Deserialize<JsonElement>(RedirectChainJson.SerializeHops(partialChain.OrderBy(h => h.Ordinal).ToList())),
            message = violation.Message,
        }, EvidenceJson));
    }

    /// <summary>
    /// Emits the redirect finding for a fetch that failed with a redirect
    /// failure kind (loop / chain exceeded surfaced as fetch failures).
    /// </summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="urlId">Source url_records identifier.</param>
    /// <param name="originalUrl">Source canonical URL.</param>
    /// <param name="failure">The fetch failure.</param>
    /// <param name="partialChain">Hops recorded before the failure.</param>
    /// <returns>The finding, or null for non-redirect failures.</returns>
    public FindingDto? AnalyzeFetchFailure(
        long auditId,
        long urlId,
        string originalUrl,
        FetchFailedException failure,
        IReadOnlyList<RedirectHop> partialChain)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(partialChain);
        RedirectChainViolation? reason = failure.Kind switch
        {
            FetchFailureKind.RedirectLoop => RedirectChainViolation.Loop,
            FetchFailureKind.RedirectChainExceeded => RedirectChainViolation.ChainCapExceeded,
            _ => null,
        };
        if (reason is null)
        {
            return null;
        }

        var ordered = partialChain.OrderBy(h => h.Ordinal).ToList();
        return Finding(auditId, reason == RedirectChainViolation.Loop ? RedirectRuleKeys.Loop : RedirectRuleKeys.Chain,
            FindingSeverity.Error, urlId, JsonSerializer.Serialize(new
            {
                url = originalUrl,
                urlId,
                violation = reason == RedirectChainViolation.Loop ? "loop" : "chain-cap-exceeded",
                hopCount = ordered.Count,
                chain = JsonSerializer.Deserialize<JsonElement>(RedirectChainJson.SerializeHops(ordered)),
            }, EvidenceJson));
    }

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
