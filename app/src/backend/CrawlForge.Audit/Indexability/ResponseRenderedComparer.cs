using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrawlForge.Audit.Indexability.RenderedComparison;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Indexability;

/// <summary>
/// Response-vs-rendered comparer (FS-CRAWL-001 §7.18, CR-009; WP-10-02 replaces
/// the WP-03-02 stub behind the locked <see cref="IResponseRenderedComparer"/>).
/// When a rendered document is present, compares status, title, metadata,
/// canonical, meta-robots, hreflang, noindex, link sets, visible text, structured
/// data and resource sets between the raw HTTP response document and the
/// post-render document. Differences surface as field-level
/// <see cref="ContentDifference"/> records (side-by-side diff surface) and as
/// findings using the locked <see cref="FindingRuleKeys"/> render.* vocabulary.
/// <para>
/// Severities (§7.18, CR-009): title / canonical / meta-robots / hreflang
/// differences are high severity (<see cref="FindingSeverity.Error"/> — the top
/// of the locked severity vocabulary). A noindex present in only one capture
/// (rendered-only OR raw-only) is CRITICAL, emitted as
/// <see cref="FindingSeverity.Error"/> with dual evidence (raw + rendered).
/// General metadata (description/Open Graph), status, text, link,
/// structured-data and resource deltas keep their Warning/Notice severities.
/// A service-worker registration is reported as informational
/// (<see cref="FindingRuleKeys.RenderServiceWorkerRegistered"/>); renders bypass
/// service workers (§7.19).
/// </para>
/// </summary>
/// <remarks>
/// Pure function of the stored documents: re-runs offline from stored artifacts
/// on re-audit (§9.6). Evidence values are bounded so persisted findings stay
/// small at scale (500k+ URLs, bounded memory). The audit pipeline invokes this
/// comparer whenever <see cref="PageAuditContext.RenderedDocument"/> is present;
/// the capture-mode coordinator (Crawler Capture) supplies that document from
/// the persisted RenderedDom artifact.
/// </remarks>
public sealed class ResponseRenderedComparer : IResponseRenderedComparer
{
    private const int MaxValueChars = 2000;
    private const int MaxLinkEntries = 200;

    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public Task<ResponseRenderedComparison> CompareAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var differences = new List<ContentDifference>();
        var findings = new List<FindingDraft>();

        if (context.Document is null || context.RenderedDocument is null)
        {
            return Task.FromResult(new ResponseRenderedComparison(differences, findings));
        }

        DomDocument raw = context.Document;
        DomDocument rendered = context.RenderedDocument;
        long urlId = context.Url.Id;

        // Status: fetch status vs the URL record's final status (rendering may
        // re-navigate; the record tracks the final outcome).
        if (context.Fetch is not null)
        {
            int responseStatus = context.Fetch.StatusCode;
            int renderedStatus = context.Url.FinalStatusCode ?? responseStatus;
            if (responseStatus != renderedStatus)
            {
                AddDelta(differences, findings, urlId, "status",
                    responseStatus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    renderedStatus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    FindingRuleKeys.RenderStatusDelta, FindingSeverity.Warning, AuditCategory.Indexability);
            }
        }

        // §7.18 high-severity directive comparisons (CR-009): title, canonical,
        // meta robots, hreflang. Error is the top of the locked severity vocabulary.
        CompareField(differences, findings, urlId, "title",
            DomTraversal.Title(raw) ?? string.Empty,
            DomTraversal.Title(rendered) ?? string.Empty,
            FindingRuleKeys.RenderTitleDelta, FindingSeverity.Error, AuditCategory.Indexability);

        CompareField(differences, findings, urlId, "canonical",
            RenderedDocumentInspection.CanonicalSignature(raw),
            RenderedDocumentInspection.CanonicalSignature(rendered),
            FindingRuleKeys.RenderCanonicalDelta, FindingSeverity.Error, AuditCategory.Indexability);

        CompareField(differences, findings, urlId, "meta-robots",
            RenderedDocumentInspection.MetaRobotsSignature(raw),
            RenderedDocumentInspection.MetaRobotsSignature(rendered),
            FindingRuleKeys.RenderMetaRobotsDelta, FindingSeverity.Error, AuditCategory.Indexability);

        // hreflang is metadata altered by script (§7.18 high severity). No
        // dedicated render.hreflang-delta key is locked yet; the delta rides the
        // locked render.metadata-delta key with field "hreflang" (a dedicated key
        // promotion is requested in the WP-10-02 wiring handoff).
        string rawHreflang = RenderedDocumentInspection.HreflangSignature(raw);
        string renderedHreflang = RenderedDocumentInspection.HreflangSignature(rendered);
        if (!string.Equals(rawHreflang, renderedHreflang, StringComparison.Ordinal))
        {
            AddDelta(differences, findings, urlId, "hreflang", rawHreflang, renderedHreflang,
                FindingRuleKeys.RenderMetadataDelta, FindingSeverity.Error, AuditCategory.Indexability);
        }

        // General metadata (description / Open Graph): Warning (not in the §7.18
        // high-severity directive list).
        CompareField(differences, findings, urlId, "metadata",
            MetadataSignature(raw), MetadataSignature(rendered),
            FindingRuleKeys.RenderMetadataDelta, FindingSeverity.Warning, AuditCategory.Indexability);

        // Aggregate directive difference for the side-by-side diff surface; the
        // specific canonical/meta-robots/hreflang findings above carry the signal.
        CompareField(differences, findings, urlId, "directives",
            DirectiveSignature(raw), DirectiveSignature(rendered),
            ruleKey: null, FindingSeverity.Notice, AuditCategory.Indexability);

        // §7.18 CRITICAL: a noindex present in only one capture, either direction,
        // with dual evidence (raw + rendered).
        CompareNoindex(differences, findings, urlId, raw, rendered, context.Fetch?.Headers);

        string rawText = DomTraversal.VisibleText(raw);
        string renderedText = DomTraversal.VisibleText(rendered);
        if (!string.Equals(rawText, renderedText, StringComparison.Ordinal))
        {
            int rawWords = WordCount(rawText);
            int renderedWords = WordCount(renderedText);
            double? fraction = rawWords == 0 ? null : Math.Abs(renderedWords - rawWords) / (double)rawWords;
            differences.Add(new ContentDifference("text", TextSignature(rawText), TextSignature(renderedText)));
            findings.Add(new FindingDraft(
                FindingRuleKeys.RenderTextDelta,
                AuditCategory.Content,
                FindingSeverity.Notice,
                urlId,
                JsonSerializer.Serialize(new
                {
                    field = "text",
                    responseValue = Bound(TextSignature(rawText)),
                    renderedValue = Bound(TextSignature(renderedText)),
                    rawWordCount = rawWords,
                    renderedWordCount = renderedWords,
                    // The harness reads the rendered totalWordCount; "words"
                    // mirrors it for the raw-vs-rendered difference report.
                    totalWordCount = renderedWords,
                    words = renderedWords,
                    wordCountDelta = renderedWords - rawWords,
                    wordCountChangeFraction = fraction,
                }, EvidenceOptions),
                null));
        }

        CompareLinks(differences, findings, urlId, raw, rendered);

        CompareField(differences, findings, urlId, "structured-data",
            StructuredDataSignature(raw), StructuredDataSignature(rendered),
            FindingRuleKeys.RenderStructuredDataDelta, FindingSeverity.Notice, AuditCategory.StructuredData);

        CompareField(differences, findings, urlId, "resources",
            ResourceSignature(raw), ResourceSignature(rendered),
            FindingRuleKeys.RenderResourcesDelta, FindingSeverity.Notice, AuditCategory.Performance);

        // §7.19 site-type rule: a registered service worker is informational;
        // every render bypasses it.
        ReportServiceWorker(findings, urlId, raw, rendered);

        return Task.FromResult(new ResponseRenderedComparison(differences, findings));
    }

    private static void CompareNoindex(
        List<ContentDifference> differences,
        List<FindingDraft> findings,
        long urlId,
        DomDocument raw,
        DomDocument rendered,
        IReadOnlyDictionary<string, string>? rawHeaders)
    {
        bool rawMetaNoindex = RenderedDocumentInspection.HasMetaNoindex(raw);
        bool rawHeaderNoindex = RenderedDocumentInspection.HasHeaderNoindex(rawHeaders);
        bool rawNoindex = rawMetaNoindex || rawHeaderNoindex;
        bool renderedNoindex = RenderedDocumentInspection.HasMetaNoindex(rendered);
        if (rawNoindex == renderedNoindex)
        {
            return;
        }

        string direction = renderedNoindex ? "rendered-only" : "raw-only";
        string rawDirectives = RenderedDocumentInspection.MetaRobotsSignature(raw);
        string renderedDirectives = RenderedDocumentInspection.MetaRobotsSignature(rendered);
        IReadOnlyList<string> rawContents = RenderedDocumentInspection.NoindexContents(raw);
        IReadOnlyList<string> renderedContents = RenderedDocumentInspection.NoindexContents(rendered);

        differences.Add(new ContentDifference("noindex", rawNoindex ? "noindex" : "index", renderedNoindex ? "noindex" : "index"));
        findings.Add(new FindingDraft(
            FindingRuleKeys.RenderNoindexDelta,
            AuditCategory.Indexability,
            FindingSeverity.Error,
            urlId,
            JsonSerializer.Serialize(new
            {
                field = "noindex",
                critical = true,
                direction,
                rawNoindex,
                renderedNoindex,
                rawMetaNoindex,
                rawHeaderNoindex,
                renderedMetaNoindex = renderedNoindex,
                // Dual evidence: both captures' directive state.
                rawDirectives = Bound(rawDirectives),
                renderedDirectives = Bound(renderedDirectives),
                rawNoindexContents = rawContents.Select(Bound).ToList(),
                renderedNoindexContents = renderedContents.Select(Bound).ToList(),
                responseValue = rawNoindex ? "noindex" : "index",
                renderedValue = renderedNoindex ? "noindex" : "index",
            }, EvidenceOptions),
            null));
    }

    private static void CompareLinks(
        List<ContentDifference> differences,
        List<FindingDraft> findings,
        long urlId,
        DomDocument raw,
        DomDocument rendered)
    {
        HashSet<string> rawHrefs = LinkHrefs(raw);
        HashSet<string> renderedHrefs = LinkHrefs(rendered);
        string rawSignature = JoinBounded(rawHrefs);
        string renderedSignature = JoinBounded(renderedHrefs);
        if (string.Equals(rawSignature, renderedSignature, StringComparison.Ordinal))
        {
            return;
        }

        List<string> renderedOnly = renderedHrefs.Except(rawHrefs).OrderBy(h => h, StringComparer.Ordinal).Take(MaxLinkEntries).ToList();
        List<string> rawOnly = rawHrefs.Except(renderedHrefs).OrderBy(h => h, StringComparer.Ordinal).Take(MaxLinkEntries).ToList();

        differences.Add(new ContentDifference("links", rawSignature, renderedSignature));
        findings.Add(new FindingDraft(
            FindingRuleKeys.RenderLinksDelta,
            AuditCategory.Linking,
            FindingSeverity.Notice,
            urlId,
            JsonSerializer.Serialize(new
            {
                field = "links",
                responseValue = Bound(rawSignature),
                renderedValue = Bound(renderedSignature),
                renderedOnlyLinks = renderedOnly,
                rawOnlyLinks = rawOnly,
                renderedOnlyCount = renderedHrefs.Except(rawHrefs).Count(),
                rawOnlyCount = rawHrefs.Except(renderedHrefs).Count(),
            }, EvidenceOptions),
            null));
    }

    private static void ReportServiceWorker(List<FindingDraft> findings, long urlId, DomDocument raw, DomDocument rendered)
    {
        ServiceWorkerFact? rawFact = RenderedDocumentInspection.DetectServiceWorker(raw);
        ServiceWorkerFact? renderedFact = RenderedDocumentInspection.DetectServiceWorker(rendered);
        if (rawFact is null && renderedFact is null)
        {
            return;
        }

        string observedIn = rawFact is not null && renderedFact is not null ? "both" : rawFact is not null ? "raw" : "rendered";
        string? scriptUrl = renderedFact?.ScriptUrl ?? rawFact?.ScriptUrl;
        findings.Add(new FindingDraft(
            FindingRuleKeys.RenderServiceWorkerRegistered,
            AuditCategory.Indexability,
            FindingSeverity.Notice,
            urlId,
            JsonSerializer.Serialize(new
            {
                registered = true,
                bypassed = true,
                scriptUrl,
                observedIn,
            }, EvidenceOptions),
            null));
    }

    private static void CompareField(
        List<ContentDifference> differences,
        List<FindingDraft> findings,
        long urlId,
        string field,
        string responseValue,
        string renderedValue,
        string? ruleKey,
        FindingSeverity severity,
        AuditCategory category)
    {
        if (string.Equals(responseValue, renderedValue, StringComparison.Ordinal))
        {
            return;
        }

        string boundedResponse = Bound(responseValue);
        string boundedRendered = Bound(renderedValue);
        differences.Add(new ContentDifference(field, boundedResponse, boundedRendered));
        if (ruleKey is not null)
        {
            findings.Add(new FindingDraft(
                ruleKey,
                category,
                severity,
                urlId,
                JsonSerializer.Serialize(new { field, responseValue = boundedResponse, renderedValue = boundedRendered }, EvidenceOptions),
                null));
        }
    }

    private static void AddDelta(
        List<ContentDifference> differences,
        List<FindingDraft> findings,
        long urlId,
        string field,
        string responseValue,
        string renderedValue,
        string ruleKey,
        FindingSeverity severity,
        AuditCategory category)
    {
        string boundedResponse = Bound(responseValue);
        string boundedRendered = Bound(renderedValue);
        differences.Add(new ContentDifference(field, boundedResponse, boundedRendered));
        findings.Add(new FindingDraft(
            ruleKey,
            category,
            severity,
            urlId,
            JsonSerializer.Serialize(new { field, responseValue = boundedResponse, renderedValue = boundedRendered }, EvidenceOptions),
            null));
    }

    private static string MetadataSignature(DomDocument document)
    {
        var parts = new List<string>();
        foreach (DomNode meta in DomTraversal.Elements(document, "meta"))
        {
            string? name = DomTraversal.Attribute(meta, "name") ?? DomTraversal.Attribute(meta, "property");
            string? content = DomTraversal.Attribute(meta, "content");
            if (name is null || content is null)
            {
                continue;
            }

            if (string.Equals(name, "description", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "og:title", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "og:description", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(name.ToLowerInvariant() + "=" + DomTraversal.CollapseWhitespace(content));
            }
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    private static string DirectiveSignature(DomDocument document)
    {
        HtmlDirectives directives = HtmlDirectiveExtractor.Extract(document);
        var parts = new List<string>();
        foreach (HtmlRobotsDirective robots in directives.Robots)
        {
            parts.Add("robots[" + robots.Agent + "]=" + string.Join(",", robots.Directives));
        }

        foreach (HtmlCanonicalLink canonical in directives.Canonicals)
        {
            parts.Add("canonical=" + canonical.Href);
        }

        foreach (HtmlHreflangAlternate alternate in directives.HreflangAlternates)
        {
            parts.Add("hreflang[" + alternate.Hreflang + "]=" + alternate.Href);
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    private static string TextSignature(string text)
    {
        int words = WordCount(text);
        return "words=" + words.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; " + Bound(text);
    }

    private static int WordCount(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static HashSet<string> LinkHrefs(DomDocument document)
    {
        var hrefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (DomNode anchor in DomTraversal.Elements(document, "a"))
        {
            string? href = DomTraversal.Attribute(anchor, "href");
            if (!string.IsNullOrWhiteSpace(href))
            {
                hrefs.Add(href.Trim());
            }
        }

        return hrefs;
    }

    private static string StructuredDataSignature(DomDocument document)
    {
        var blocks = new List<string>();
        foreach (DomNode script in DomTraversal.Elements(document, "script"))
        {
            string? type = DomTraversal.Attribute(script, "type");
            if (string.Equals(type, "application/ld+json", StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add(DomTraversal.CollapseWhitespace(script.TextContent));
            }
        }

        blocks.Sort(StringComparer.Ordinal);
        string joined = string.Join("\n", blocks);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
        return "blocks=" + blocks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; sha256=" + hash;
    }

    private static string ResourceSignature(DomDocument document)
    {
        var resources = new HashSet<string>(StringComparer.Ordinal);
        foreach (DomNode node in DomTraversal.EnumerateElements(document))
        {
            if (string.Equals(node.TagName, "script", StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.TagName, "img", StringComparison.OrdinalIgnoreCase))
            {
                string? src = DomTraversal.Attribute(node, "src");
                if (!string.IsNullOrWhiteSpace(src))
                {
                    resources.Add(node.TagName.ToLowerInvariant() + ":" + src.Trim());
                }
            }
            else if (string.Equals(node.TagName, "link", StringComparison.OrdinalIgnoreCase))
            {
                string? href = DomTraversal.Attribute(node, "href");
                if (!string.IsNullOrWhiteSpace(href))
                {
                    resources.Add("link:" + href.Trim());
                }
            }
        }

        return JoinBounded(resources);
    }

    private static string JoinBounded(HashSet<string> values)
    {
        List<string> sorted = values.OrderBy(v => v, StringComparer.Ordinal).Take(MaxLinkEntries).ToList();
        string joined = string.Join("\n", sorted);
        if (values.Count > MaxLinkEntries)
        {
            joined += "\n[+" + (values.Count - MaxLinkEntries).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more]";
        }

        return joined;
    }

    private static string Bound(string value) =>
        value.Length <= MaxValueChars ? value : value.Substring(0, MaxValueChars) + "…[truncated]";
}
