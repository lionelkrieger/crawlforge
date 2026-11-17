using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Indexability;

/// <summary>
/// Response-vs-rendered comparer (FS-CRAWL-001 §7.18): when rendering ran,
/// compares status, title and metadata, directives, link sets, visible text,
/// structured data, and resource sets between the raw HTTP response document
/// and the post-render document. Differences surface as field-level
/// <see cref="ContentDifference"/> records (side-by-side diff surface) and as
/// findings (content/links present only after rendering, metadata altered by
/// script).
/// </summary>
/// <remarks>
/// Pure function of the stored documents: re-runs offline from stored
/// artifacts on re-audit. Evidence values are bounded so persisted findings
/// stay small at scale (500k+ URLs, bounded memory).
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
                    IndexabilityRuleKeys.RenderStatusDelta, FindingSeverity.Warning, AuditCategory.Indexability);
            }
        }

        CompareField(differences, findings, urlId, "title",
            DomTraversal.Title(raw) ?? string.Empty,
            DomTraversal.Title(rendered) ?? string.Empty,
            IndexabilityRuleKeys.RenderTitleDelta, FindingSeverity.Warning, AuditCategory.Indexability);

        CompareField(differences, findings, urlId, "metadata",
            MetadataSignature(raw), MetadataSignature(rendered),
            IndexabilityRuleKeys.RenderMetadataDelta, FindingSeverity.Warning, AuditCategory.Indexability);

        CompareField(differences, findings, urlId, "directives",
            DirectiveSignature(raw), DirectiveSignature(rendered),
            IndexabilityRuleKeys.RenderMetadataDelta, FindingSeverity.Warning, AuditCategory.Indexability);

        string rawText = DomTraversal.VisibleText(raw);
        string renderedText = DomTraversal.VisibleText(rendered);
        if (!string.Equals(rawText, renderedText, StringComparison.Ordinal))
        {
            AddDelta(differences, findings, urlId, "text",
                TextSignature(rawText), TextSignature(renderedText),
                IndexabilityRuleKeys.RenderTextDelta, FindingSeverity.Notice, AuditCategory.Content);
        }

        CompareField(differences, findings, urlId, "links",
            LinkSignature(raw), LinkSignature(rendered),
            IndexabilityRuleKeys.RenderLinksDelta, FindingSeverity.Notice, AuditCategory.Linking);

        CompareField(differences, findings, urlId, "structured-data",
            StructuredDataSignature(raw), StructuredDataSignature(rendered),
            IndexabilityRuleKeys.RenderStructuredDataDelta, FindingSeverity.Notice, AuditCategory.StructuredData);

        CompareField(differences, findings, urlId, "resources",
            ResourceSignature(raw), ResourceSignature(rendered),
            IndexabilityRuleKeys.RenderResourcesDelta, FindingSeverity.Notice, AuditCategory.Performance);

        return Task.FromResult(new ResponseRenderedComparison(differences, findings));
    }

    private static void CompareField(
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
        if (!string.Equals(responseValue, renderedValue, StringComparison.Ordinal))
        {
            AddDelta(differences, findings, urlId, field, responseValue, renderedValue, ruleKey, severity, category);
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

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    private static string TextSignature(string text)
    {
        int words = string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return "words=" + words.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; " + Bound(text);
    }

    private static string LinkSignature(DomDocument document)
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

        return JoinBounded(hrefs);
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