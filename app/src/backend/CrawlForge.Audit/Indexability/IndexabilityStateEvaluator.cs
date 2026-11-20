using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Indexability;

/// <summary>
/// Inputs of the CR-024 indexability state evaluation (04/01 §7.4 as amended
/// by CR-024, D-020): the crawl facts a URL-record write path already holds.
/// Directive lists are the parsed shapes produced by
/// <see cref="HtmlDirectiveExtractor"/> and <see cref="HeaderDirectiveParser"/>;
/// canonical hrefs are the DECLARED references (HTML <c>rel="canonical"</c>
/// first, then Link-header canonicals), resolved against <see cref="PageUrl"/>
/// by the evaluator.
/// </summary>
/// <param name="FinalStatusCode">Final HTTP status after redirects; null when never fetched.</param>
/// <param name="RobotsBlocked">Robots exclusion verdict (true when crawling is disallowed).</param>
/// <param name="MetaRobots">Parsed meta-robots scopes (agent + directives).</param>
/// <param name="HeaderRobots">Parsed X-Robots-Tag scopes (agent + directives).</param>
/// <param name="CanonicalHrefs">Declared canonical references in source order (HTML, then header).</param>
/// <param name="PageUrl">The page's final URL, the base for canonical resolution and the self-reference comparison.</param>
public sealed record IndexabilityStateInput(
    int? FinalStatusCode,
    bool RobotsBlocked,
    IReadOnlyList<HtmlRobotsDirective> MetaRobots,
    IReadOnlyList<RobotsHeaderDirective> HeaderRobots,
    IReadOnlyList<string> CanonicalHrefs,
    string PageUrl);

/// <summary>
/// CR-024 indexability verdict: <b>indexable means search-indexable</b>
/// (04/01 §7.4, D-020). Fetchability is a separate state carried by the crawl
/// status class; this verdict answers whether a search engine may index the
/// URL. <see cref="Flags"/> composes the locked <see cref="IndexabilityFlags"/>
/// bits. Amendment WP-10-A1 (D-021/CR-024) added the
/// <see cref="IndexabilityFlags.CanonicalElsewhere"/> bit (16): it is set when
/// the page's canonical points to a different URL. The remaining canonical
/// defects (multiple targets, invalid URL) stay carried by <see cref="Reasons"/>.
/// </summary>
/// <param name="IsIndexable">True only when final status is 200, robots allows crawling, no noindex directive exists in meta robots OR X-Robots-Tag, and the canonical is self-referencing or absent.</param>
/// <param name="Flags">Observed indexability flags (Noindex, Nofollow, RobotsBlocked, NonIndexableStatus, CanonicalElsewhere).</param>
/// <param name="Reasons">Stable reason codes for every disqualifying observation, in evaluation order; empty for a clean indexable URL.</param>
public sealed record IndexabilityState(
    bool IsIndexable,
    IndexabilityFlags Flags,
    IReadOnlyList<string> Reasons)
{
    /// <summary>Structural value equality: two verdicts are equal when the
    /// indexability bit, flags, and reason-code sequence all match (the reason
    /// list is compared element-wise, not by reference).</summary>
    /// <param name="other">The verdict to compare against.</param>
    /// <returns>True when the verdicts are structurally identical.</returns>
    public bool Equals(IndexabilityState? other) =>
        other is not null
        && IsIndexable == other.IsIndexable
        && Flags == other.Flags
        && Reasons.SequenceEqual(other.Reasons, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsIndexable);
        hash.Add(Flags);
        foreach (var reason in Reasons)
        {
            hash.Add(reason, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// CR-024 indexability state evaluator (04/01 §7.4 as amended by CR-024,
/// owner decision D-020). A URL is <b>search-indexable</b> if and only if:
/// <list type="bullet">
/// <item>its final HTTP status is exactly 200;</item>
/// <item>it is not blocked by robots.txt;</item>
/// <item>neither meta robots nor X-Robots-Tag carries <c>noindex</c> (or
/// <c>none</c>, which expands to noindex+nofollow) under ANY agent scope;</item>
/// <item>its canonical is self-referencing or absent — a canonical resolving
/// to another URL, multiple distinct canonicals, or an unresolvable canonical
/// all disqualify.</item>
/// </list>
/// Fetchability is a separate state (crawl status class); a fetchable page can
/// be non-indexable (the phase-08 noindex fixture defect, G-NOINDEX-FLAG-QUESTION).
/// </summary>
/// <remarks>
/// <para>
/// Pure and stateless: the same inputs always produce the same verdict, on
/// every thread, with no network and no database reads — safe for the engine's
/// concurrent URL-record write path (MaxConcurrency &gt; 1) and for offline
/// re-evaluation from stored artifacts (FS-CRAWL-001 §9.6).
/// </para>
/// <para>
/// Interpretation notes (documented choices where CR-024 is silent):
/// (1) <c>nofollow</c> sets the Nofollow flag but does NOT disqualify —
/// it restricts link following, not indexing. (2) Agent-scoped noindex
/// (e.g. <c>googlebot: noindex</c>) disqualifies: the URL is not indexable by
/// that search engine, so it is not search-indexable. (3) Multiple canonical
/// references that resolve to the SAME normalized URL count as one (HTML +
/// header agreement is a Pass per §15.4); distinct targets are an unresolved
/// conflict and disqualify — the evaluator never auto-resolves conflicts
/// (§15.4). (4) An unresolvable/non-http(s) canonical disqualifies: it is
/// neither self-referencing nor absent. (5) Canonical comparison normalizes
/// trailing slashes and ignores case, matching
/// <see cref="IndexabilityAnalyzer"/>'s canonical checks. (6) When a rendered
/// document exists, <see cref="FromContext"/> reads directives from it
/// (rendered-first, CR-009 philosophy; §7.18 treats rendered-only noindex as
/// critical). (7) WP-10-A1/CR-024/D-021: a canonical resolving to a different
/// URL additionally sets the locked <see cref="IndexabilityFlags.CanonicalElsewhere"/>
/// bit (16); conflicting canonical targets set it when at least one target is
/// elsewhere. The bit never changes the IsIndexable judgment — it carries the
/// observation on the URL record's flags.
/// </para>
/// </remarks>
public static class IndexabilityStateEvaluator
{
    /// <summary>Reason code: final status is not exactly 200 (or unknown).</summary>
    public const string ReasonFinalStatusNot200 = "final-status-not-200";

    /// <summary>Reason code: robots exclusion blocks crawling.</summary>
    public const string ReasonRobotsBlocked = "robots-blocked";

    /// <summary>Reason code: a meta-robots scope carries noindex/none.</summary>
    public const string ReasonNoindexMetaRobots = "noindex-meta-robots";

    /// <summary>Reason code: an X-Robots-Tag scope carries noindex/none.</summary>
    public const string ReasonNoindexXRobotsTag = "noindex-x-robots-tag";

    /// <summary>Reason code: the canonical resolves to a different URL.</summary>
    public const string ReasonCanonicalToOtherUrl = "canonical-to-other-url";

    /// <summary>Reason code: multiple distinct canonical targets (unresolved conflict).</summary>
    public const string ReasonCanonicalMultiple = "canonical-multiple";

    /// <summary>Reason code: a canonical reference is unresolvable or non-http(s).</summary>
    public const string ReasonCanonicalInvalid = "canonical-invalid";

    /// <summary>Evaluates the search-indexability state from explicit inputs.</summary>
    /// <param name="input">Crawl facts and parsed directives.</param>
    /// <returns>The verdict, flags, and reason codes.</returns>
    /// <exception cref="ArgumentNullException">The input is null.</exception>
    public static IndexabilityState Evaluate(IndexabilityStateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var flags = IndexabilityFlags.None;
        var reasons = new List<string>();

        // 1. Final status must be exactly 200 (CR-024: "final status 200").
        if (input.FinalStatusCode != 200)
        {
            flags |= IndexabilityFlags.NonIndexableStatus;
            reasons.Add(ReasonFinalStatusNot200);
        }

        // 2. Robots exclusion blocks crawling and therefore indexing.
        if (input.RobotsBlocked)
        {
            flags |= IndexabilityFlags.RobotsBlocked;
            reasons.Add(ReasonRobotsBlocked);
        }

        // 3. noindex in meta robots OR X-Robots-Tag, under any agent scope.
        if (HasNoindex(input.MetaRobots.Select(scope => scope.Directives)))
        {
            flags |= IndexabilityFlags.Noindex;
            reasons.Add(ReasonNoindexMetaRobots);
        }

        if (HasNoindex(input.HeaderRobots.Select(scope => scope.Directives)))
        {
            flags |= IndexabilityFlags.Noindex;
            reasons.Add(ReasonNoindexXRobotsTag);
        }

        if (HasNofollow(input.MetaRobots.Select(scope => scope.Directives))
            || HasNofollow(input.HeaderRobots.Select(scope => scope.Directives)))
        {
            flags |= IndexabilityFlags.Nofollow;
        }

        // 4. Canonical must be self-referencing or absent.
        var canonicalOk = EvaluateCanonical(input, reasons, ref flags);

        var isIndexable = input.FinalStatusCode == 200
            && !input.RobotsBlocked
            && (flags & IndexabilityFlags.Noindex) == 0
            && canonicalOk;

        return new IndexabilityState(isIndexable, flags, reasons);
    }

    /// <summary>
    /// Builds evaluation inputs from a page audit context. Directives come
    /// from the rendered document when one exists (rendered-first, CR-009),
    /// otherwise the response document; headers come from the stored fetch.
    /// Canonical references are collected HTML-first, then Link-header.
    /// </summary>
    /// <param name="context">Page audit context.</param>
    /// <returns>The evaluation inputs.</returns>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    public static IndexabilityStateInput FromContext(PageAuditContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var document = context.RenderedDocument ?? context.Document;
        HtmlDirectives html = HtmlDirectiveExtractor.Extract(document);
        ParsedHeaderDirectives headers = HeaderDirectiveParser.Parse(context.Fetch?.Headers);

        var canonicals = new List<string>(html.Canonicals.Count + headers.Links.Count);
        canonicals.AddRange(html.Canonicals.Select(link => link.Href));
        canonicals.AddRange(headers.Links
            .Where(link => string.Equals(link.Relation, "canonical", StringComparison.OrdinalIgnoreCase))
            .Select(link => link.Target));

        return new IndexabilityStateInput(
            FinalStatusCode: context.Url.FinalStatusCode ?? context.Fetch?.StatusCode,
            RobotsBlocked: context.Url.RobotsState == RobotsState.Disallowed,
            MetaRobots: html.Robots,
            HeaderRobots: headers.Robots,
            CanonicalHrefs: canonicals,
            PageUrl: context.Fetch?.FinalUrl ?? context.Url.CanonicalUrl);
    }

    /// <summary>Evaluates a page audit context (convenience over <see cref="FromContext"/>).</summary>
    /// <param name="context">Page audit context.</param>
    /// <returns>The verdict, flags, and reason codes.</returns>
    public static IndexabilityState EvaluateContext(PageAuditContext context) =>
        Evaluate(FromContext(context));

    /// <summary>
    /// Engine write-path convenience (WP-09-11 call site:
    /// <c>AuditExecutionEngine.NewUrlRecord</c>): evaluates from the raw fetch
    /// result BEFORE the analysis-phase parse. When <paramref name="parser"/>
    /// is supplied and the response is HTML, the body is parsed once to extract
    /// meta robots and canonical directives; without a parser only status,
    /// robots, and X-Robots-Tag are observable (callers that hold a parsed
    /// document should prefer <see cref="FromContext"/>).
    /// </summary>
    /// <param name="pageUrl">The URL record's canonical URL (base for canonical resolution).</param>
    /// <param name="fetch">Fetch result, or null when the URL was never fetched.</param>
    /// <param name="robots">Robots evaluation state of the URL.</param>
    /// <param name="parser">Optional tolerant parser for the response body.</param>
    /// <returns>The verdict, flags, and reason codes.</returns>
    public static IndexabilityState EvaluateFetch(
        string pageUrl,
        HttpFetchResult? fetch,
        RobotsState robots,
        ITolerantDomParser? parser = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);

        if (fetch is null)
        {
            return Evaluate(new IndexabilityStateInput(
                FinalStatusCode: null,
                RobotsBlocked: robots == RobotsState.Disallowed,
                MetaRobots: [],
                HeaderRobots: [],
                CanonicalHrefs: [],
                PageUrl: pageUrl));
        }

        ParsedHeaderDirectives headers = HeaderDirectiveParser.Parse(fetch.Headers);
        var metaRobots = Array.Empty<HtmlRobotsDirective>();
        var canonicals = new List<string>();

        if (parser is not null && fetch.Body.Length > 0 && IsHtml(fetch.Headers))
        {
            var html = System.Text.Encoding.UTF8.GetString(fetch.Body.Span);
            DomDocument document = parser.Parse(html, fetch.FinalUrl);
            HtmlDirectives extracted = HtmlDirectiveExtractor.Extract(document);
            metaRobots = extracted.Robots.ToArray();
            canonicals.AddRange(extracted.Canonicals.Select(link => link.Href));
        }

        canonicals.AddRange(headers.Links
            .Where(link => string.Equals(link.Relation, "canonical", StringComparison.OrdinalIgnoreCase))
            .Select(link => link.Target));

        return Evaluate(new IndexabilityStateInput(
            FinalStatusCode: fetch.StatusCode,
            RobotsBlocked: robots == RobotsState.Disallowed,
            MetaRobots: metaRobots,
            HeaderRobots: headers.Robots,
            CanonicalHrefs: canonicals,
            PageUrl: string.IsNullOrWhiteSpace(fetch.FinalUrl) ? pageUrl : fetch.FinalUrl));
    }

    private static bool HasNoindex(IEnumerable<IReadOnlyList<string>> directiveScopes) =>
        directiveScopes.Any(directives => directives.Any(IsNoindexToken));

    private static bool HasNofollow(IEnumerable<IReadOnlyList<string>> directiveScopes) =>
        directiveScopes.Any(directives => directives.Any(token =>
            string.Equals(token, "nofollow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "none", StringComparison.OrdinalIgnoreCase)));

    private static bool IsNoindexToken(string token) =>
        string.Equals(token, "noindex", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "none", StringComparison.OrdinalIgnoreCase);

    private static bool EvaluateCanonical(IndexabilityStateInput input, List<string> reasons, ref IndexabilityFlags flags)
    {
        if (input.CanonicalHrefs.Count == 0)
        {
            // Absent canonical is indexable (CR-024).
            return true;
        }

        if (!Uri.TryCreate(input.PageUrl, UriKind.Absolute, out Uri? pageUri)
            || (pageUri.Scheme != Uri.UriSchemeHttp && pageUri.Scheme != Uri.UriSchemeHttps))
        {
            // Without an absolute http(s) page URL no canonical can be verified
            // as self-referencing; the declared canonical is not absent.
            reasons.Add(ReasonCanonicalInvalid);
            return false;
        }

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var href in input.CanonicalHrefs)
        {
            // An empty href resolves to the page URL itself (RFC 3986 §5.3),
            // i.e. a self-reference; unresolvable/non-http(s) targets are defects.
            if (!Uri.TryCreate(pageUri, href.Trim(), out Uri? target)
                || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            {
                reasons.Add(ReasonCanonicalInvalid);
                return false;
            }

            targets.Add(Normalize(target.AbsoluteUri));
        }

        if (targets.Count > 1)
        {
            // Distinct canonical targets are an unresolved conflict (§15.4:
            // never auto-resolve); the URL is not reliably search-indexable.
            reasons.Add(ReasonCanonicalMultiple);
            // CR-024/D-021: at least one declared canonical consolidates
            // indexing away from this page (a self-target among conflicting
            // targets does not neutralize the elsewhere targets).
            if (targets.Any(target => !string.Equals(target, Normalize(pageUri.AbsoluteUri), StringComparison.OrdinalIgnoreCase)))
            {
                flags |= IndexabilityFlags.CanonicalElsewhere;
            }

            return false;
        }

        var canonical = targets.First();
        if (!string.Equals(canonical, Normalize(pageUri.AbsoluteUri), StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(ReasonCanonicalToOtherUrl);
            // CR-024/D-021 (WP-10-A1): the canonical points to a different URL
            // — the page stays crawlable but is not search-indexable; the
            // locked CanonicalElsewhere bit (16) carries the observation.
            flags |= IndexabilityFlags.CanonicalElsewhere;
            return false;
        }

        return true;
    }

    private static string Normalize(string url) => url.TrimEnd('/');

    private static bool IsHtml(IReadOnlyDictionary<string, string> headers)
    {
        foreach (KeyValuePair<string, string> header in headers)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                return header.Value.Contains("text/html", StringComparison.OrdinalIgnoreCase);
            }
        }

        // No content type declared: treat as HTML (matches the engine's IsHtml).
        return true;
    }
}
