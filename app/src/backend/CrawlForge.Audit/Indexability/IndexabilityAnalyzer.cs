using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Indexability;

/// <summary>
/// Indexability analyzer (FS-CRAWL-001 §7.6): status classification per the
/// crawl-status taxonomy, robots state, meta/header robots directives, and
/// canonical chain validation. Soft-404 flagging (criterion #28) belongs to
/// the dedicated <see cref="ISoft404Detector"/> pipeline step — the sole
/// emission path guaranteeing one <c>indexability.soft-404</c> finding per URL
/// (WP-09-05 dedup); this analyzer emits soft-404 only under the explicit
/// <see cref="IndexabilityAnalyzerOptions.EmitSoft404Findings"/> opt-in for
/// standalone compositions.
/// </summary>
/// <remarks>
/// <para>
/// Every rule is a pure function of the stored <see cref="PageAuditContext"/>
/// (crawl facts, fetch headers, parsed document, scoped text): no network and
/// no database reads, so all rules re-run offline from stored artifacts on
/// re-audit (FS-CRAWL-001 §9.6). Header/HTML directive <em>conflicts</em> are
/// the separate <see cref="IHeaderHtmlEquivalencyAnalyzer"/> surface (§15.4).
/// </para>
/// <para>
/// Severity policy: hard status failures are Errors; robots exclusion and
/// canonical defects are Warnings; intentional directives (noindex/nofollow)
/// are Notices; a clean indexable page records a Pass annotation.
/// </para>
/// </remarks>
public sealed class IndexabilityAnalyzer : IIndexabilityAnalyzer
{
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web);

    private readonly ISoft404Detector? _soft404Detector;
    private readonly IndexabilityAnalyzerOptions _options;

    /// <summary>Creates an analyzer with an optional soft-404 detector.</summary>
    /// <param name="soft404Detector">
    /// Optional detector used for soft-404 evaluation (criterion #28). Finding
    /// EMISSION is controlled by <paramref name="options"/>: by default the
    /// pipeline's dedicated <c>ISoft404Detector</c> step is the sole emitter,
    /// so this analyzer does not emit soft-404 findings (WP-09-05 dedup —
    /// probe-2 measured two findings per URL when both paths emitted).
    /// </param>
    /// <param name="options">
    /// Composition options; null uses <see cref="IndexabilityAnalyzerOptions.Default"/>
    /// (soft-404 emission delegated to the pipeline's dedicated detector step).
    /// </param>
    public IndexabilityAnalyzer(ISoft404Detector? soft404Detector = null, IndexabilityAnalyzerOptions? options = null)
    {
        _soft404Detector = soft404Detector;
        _options = options ?? IndexabilityAnalyzerOptions.Default;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var findings = new List<FindingDraft>();
        long urlId = context.Url.Id;

        // 1. Status classification per the rule taxonomy.
        FindingDraft? statusFinding = ClassifyStatus(context);
        if (statusFinding is not null)
        {
            findings.Add(statusFinding);
        }

        // 2. Robots exclusion state (blocks crawling, not indexing: Warning).
        if (context.Url.RobotsState == RobotsState.Disallowed)
        {
            findings.Add(Finding(
                IndexabilityRuleKeys.RobotsBlocked,
                FindingSeverity.Warning,
                urlId,
                new { robotsState = context.Url.RobotsState.ToString(), url = context.Url.CanonicalUrl }));
        }

        if (context.Url.StatusClass != CrawlStatusClass.Success)
        {
            // Directive and canonical checks apply to fetched success pages.
            return findings;
        }

        // 3. Robots directives from HTML meta and X-Robots-Tag headers.
        HtmlDirectives html = HtmlDirectiveExtractor.Extract(context.Document);
        ParsedHeaderDirectives headers = HeaderDirectiveParser.Parse(context.Fetch?.Headers);
        bool noindex = false;

        foreach (HtmlRobotsDirective scope in html.Robots)
        {
            noindex |= AddDirectiveFindings(findings, urlId, "meta-robots", scope.Agent, scope.Directives, scope.RawContent);
        }

        foreach (RobotsHeaderDirective scope in headers.Robots)
        {
            noindex |= AddDirectiveFindings(findings, urlId, "x-robots-tag", scope.Agent, scope.Directives, scope.RawValue);
        }

        // 4. Canonical chain validation (HTML source preferred, header fallback).
        if (!noindex)
        {
            AddCanonicalFindings(findings, context, html, headers);
        }

        // 5. Soft-404 flagging, distinct from hard HTTP errors. Emission is
        // OPT-IN (WP-09-05 dedup): the audit pipeline's dedicated
        // ISoft404Detector step is the sole emission path, so by default this
        // analyzer neither runs nor emits soft-404 — exactly one finding per
        // URL regardless of how the detector is composed.
        if (_soft404Detector is not null && _options.EmitSoft404Findings)
        {
            Soft404Verdict verdict = await _soft404Detector.DetectAsync(context, cancellationToken).ConfigureAwait(false);
            if (verdict.IsSoft404)
            {
                findings.Add(Finding(
                    IndexabilityRuleKeys.Soft404,
                    FindingSeverity.Warning,
                    urlId,
                    new { confidence = verdict.Confidence, evidence = verdict.EvidenceJson }));
            }
        }

        // 6. Pass annotation for a clean indexable page.
        if (!noindex && context.Url.RobotsState != RobotsState.Disallowed)
        {
            findings.Add(Finding(
                IndexabilityRuleKeys.Indexable,
                FindingSeverity.Pass,
                urlId,
                new { url = context.Url.CanonicalUrl, finalStatusCode = context.Url.FinalStatusCode }));
        }

        return findings;
    }

    private static FindingDraft? ClassifyStatus(PageAuditContext context)
    {
        CrawlStatusClass statusClass = context.Url.StatusClass;
        return statusClass switch
        {
            CrawlStatusClass.Success => null,
            CrawlStatusClass.ClientError or CrawlStatusClass.ServerError => Finding(
                IndexabilityRuleKeys.NonIndexableStatus,
                FindingSeverity.Error,
                context.Url.Id,
                new { statusClass = statusClass.ToString(), finalStatusCode = context.Url.FinalStatusCode }),
            CrawlStatusClass.Blocked => Finding(
                IndexabilityRuleKeys.NonIndexableStatus,
                FindingSeverity.Error,
                context.Url.Id,
                new
                {
                    statusClass = statusClass.ToString(),
                    finalStatusCode = context.Url.FinalStatusCode,
                    blockReason = context.Url.BlockReason,
                    wafProvider = context.Url.WafProvider?.ToString(),
                    challengeClass = context.Url.ChallengeClass?.ToString(),
                }),
            CrawlStatusClass.FetchError => Finding(
                IndexabilityRuleKeys.NonIndexableStatus,
                FindingSeverity.Error,
                context.Url.Id,
                new { statusClass = statusClass.ToString(), finalStatusCode = context.Url.FinalStatusCode }),
            CrawlStatusClass.Redirect => Finding(
                IndexabilityRuleKeys.NonIndexableStatus,
                FindingSeverity.Warning,
                context.Url.Id,
                new { statusClass = statusClass.ToString(), redirectTarget = context.Url.RedirectTargetUrl }),
            _ => Finding(
                IndexabilityRuleKeys.NonIndexableStatus,
                FindingSeverity.Notice,
                context.Url.Id,
                new { statusClass = statusClass.ToString() }),
        };
    }

    private static bool AddDirectiveFindings(
        List<FindingDraft> findings,
        long urlId,
        string source,
        string agent,
        IReadOnlyList<string> directives,
        string raw)
    {
        bool noindex = directives.Contains("noindex") || directives.Contains("none");
        bool nofollow = directives.Contains("nofollow") || directives.Contains("none");

        if (noindex)
        {
            findings.Add(Finding(
                IndexabilityRuleKeys.Noindex,
                FindingSeverity.Notice,
                urlId,
                new { source, agent, directives, raw }));
        }

        if (nofollow)
        {
            findings.Add(Finding(
                IndexabilityRuleKeys.Nofollow,
                FindingSeverity.Notice,
                urlId,
                new { source, agent, directives, raw }));
        }

        return noindex;
    }

    private static void AddCanonicalFindings(
        List<FindingDraft> findings,
        PageAuditContext context,
        HtmlDirectives html,
        ParsedHeaderDirectives headers)
    {
        long urlId = context.Url.Id;
        string pageUrl = context.Fetch?.FinalUrl ?? context.Url.CanonicalUrl;

        if (html.Canonicals.Count > 1)
        {
            findings.Add(Finding(
                IndexabilityRuleKeys.CanonicalMultiple,
                FindingSeverity.Error,
                urlId,
                new { hrefs = html.Canonicals.Select(c => c.Href).ToList(), pageUrl }));
            return;
        }

        string? canonicalHref = html.Canonicals.Count == 1
            ? html.Canonicals[0].Href
            : headers.Links.FirstOrDefault(l => l.Relation == "canonical")?.Target;
        string source = html.Canonicals.Count == 1 ? "html" : "header";

        if (canonicalHref is null)
        {
            findings.Add(Finding(
                IndexabilityRuleKeys.CanonicalMissing,
                FindingSeverity.Warning,
                urlId,
                new { pageUrl }));
            return;
        }

        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? baseUri)
            || !Uri.TryCreate(baseUri, canonicalHref, out Uri? target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            findings.Add(Finding(
                IndexabilityRuleKeys.CanonicalInvalid,
                FindingSeverity.Warning,
                urlId,
                new { canonicalHref, source, pageUrl }));
            return;
        }

        string normalizedTarget = Normalize(target.AbsoluteUri);
        string normalizedPage = Normalize(baseUri.AbsoluteUri);

        if (string.Equals(normalizedTarget, normalizedPage, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding(
                IndexabilityRuleKeys.CanonicalSelfReference,
                FindingSeverity.Pass,
                urlId,
                new { canonicalTarget = target.AbsoluteUri, source, pageUrl }));
            return;
        }

        if (!string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding(
                IndexabilityRuleKeys.CanonicalCrossHost,
                FindingSeverity.Warning,
                urlId,
                new { canonicalTarget = target.AbsoluteUri, source, pageUrl, targetHost = target.Host }));
            return;
        }

        findings.Add(Finding(
            IndexabilityRuleKeys.CanonicalMismatch,
            FindingSeverity.Warning,
            urlId,
            new { canonicalTarget = target.AbsoluteUri, source, pageUrl }));
    }

    private static string Normalize(string url) => url.TrimEnd('/');

    private static FindingDraft Finding(string ruleKey, FindingSeverity severity, long urlId, object evidence) =>
        new(ruleKey, AuditCategory.Indexability, severity, urlId, JsonSerializer.Serialize(evidence, EvidenceOptions), null);
}