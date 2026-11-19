using System.Globalization;
using System.Text.Json;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Cross-URL duplicate-metadata aggregation pass (FS-CRAWL-001 §7.4
/// content-quality row: "missing, duplicate, or out-of-range-length titles and
/// descriptions"; §7.6: aggregation passes run once over indexed views;
/// CAP-B-040 duplicate titles, CAP-B-042 duplicate descriptions; fixture trap
/// "duplicate-titles"). Groups ALL URLs sharing an identical title or meta
/// description into ONE site-wide finding per duplicated value.
/// </summary>
/// <remarks>
/// <para>
/// D14 site-pass pattern (like <c>NGramRuleAnalyzer.RunSitePassAsync</c>): the
/// engine invokes <see cref="AnalyzeSiteAsync"/> once in the analysis phase
/// after the per-URL passes; findings carry <c>UrlId = null</c> and list every
/// affected URL in evidence (<c>urls[].urlId</c>/<c>urls[].url</c>). Reads
/// STORED facts only — <c>url_records</c> plus the <c>document_head</c> element
/// inventory written by the pipeline — never re-fetches (§7.2, §9.6), so a
/// re-audit recomputes identical groups offline.
/// </para>
/// <para>
/// Comparison basis: whitespace-normalized, case-SENSITIVE text equality (an
/// exact duplicate is the same string; case variants are different titles).
/// Empty titles/descriptions are skipped — missing metadata is a separate
/// per-URL rule family. Successfully fetched pages participate regardless of
/// indexability: duplicate metadata is a defect even on noindex pages.
/// </para>
/// <para>
/// Scale invariance (§7.6): paged enumeration (500 rows/page), one indexed
/// <c>document_head</c> lookup per URL, and only the grouping facts (urlId,
/// URL, normalized title/description) are held in memory — never page bodies.
/// Deterministic output: groups ordered by value (ordinal), members by urlId.
/// Thread-safe: no mutable instance state.
/// </para>
/// </remarks>
public sealed class DuplicateMetadataAnalyzer
{
    private const int PageSize = 500;
    private const string DocumentHeadGroup = "document_head";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IReadRepository<UrlRecordDto> _urls;
    private readonly IReadRepository<UrlElementDto> _elements;

    /// <summary>Creates the site pass over the locked read repositories.</summary>
    /// <param name="urls">URL-record index (audit-scoped enumeration).</param>
    /// <param name="elements">Element inventory (document_head facts per URL).</param>
    public DuplicateMetadataAnalyzer(IReadRepository<UrlRecordDto> urls, IReadRepository<UrlElementDto> elements)
    {
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(elements);
        _urls = urls;
        _elements = elements;
    }

    /// <summary>
    /// Runs the duplicate-metadata aggregation over one audit's stored facts.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One <c>content.duplicate-title</c> finding per duplicated title and one
    /// <c>content.duplicate-description</c> finding per duplicated description,
    /// each grouping ALL affected URLs; empty when nothing is duplicated.
    /// </returns>
    public async Task<IReadOnlyList<FindingDraft>> AnalyzeSiteAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var titleGroups = new Dictionary<string, List<UrlMember>>(StringComparer.Ordinal);
        var descriptionGroups = new Dictionary<string, List<UrlMember>>(StringComparer.Ordinal);

        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _urls.ListAsync(new ListQuery(
                Offset: offset,
                Limit: PageSize,
                Filters: new Dictionary<string, string>
                {
                    ["auditId"] = auditId.ToString(CultureInfo.InvariantCulture),
                },
                SortBy: "id"), cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (var url in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (url.StatusClass != CrawlStatusClass.Success)
                {
                    continue;
                }

                var (title, description) = await ReadHeadFactsAsync(url.Id, cancellationToken).ConfigureAwait(false);
                var member = new UrlMember(url.Id, url.CanonicalUrl);

                if (title is not null)
                {
                    AddMember(titleGroups, title, member);
                }

                if (description is not null)
                {
                    AddMember(descriptionGroups, description, member);
                }
            }

            offset += page.Items.Count;
            if (offset >= page.TotalCount)
            {
                break;
            }
        }

        var findings = new List<FindingDraft>();
        foreach (var (title, members) in titleGroups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (members.Count >= 2)
            {
                findings.Add(BuildFinding(ContentQualityRuleKeys.DuplicateTitle, "title", title, members));
            }
        }

        foreach (var (description, members) in descriptionGroups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (members.Count >= 2)
            {
                findings.Add(BuildFinding(ContentQualityRuleKeys.DuplicateDescription, "description", description, members));
            }
        }

        return findings;
    }

    private async Task<(string? Title, string? Description)> ReadHeadFactsAsync(long urlId, CancellationToken cancellationToken)
    {
        var page = await _elements.ListAsync(new ListQuery(
            Offset: 0,
            Limit: 1,
            Filters: new Dictionary<string, string>
            {
                ["urlId"] = urlId.ToString(CultureInfo.InvariantCulture),
                ["elementGroup"] = DocumentHeadGroup,
            }), cancellationToken).ConfigureAwait(false);
        var head = page.Items.FirstOrDefault();
        if (head is null)
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(head.PayloadJson);
            var root = document.RootElement;
            // Wire shape of ElementInventoryExtractor.ExtractHead (camelCase):
            // { title, metaDescription, metaRobots, viewport, charset,
            //   canonicalUrl, alternates, openGraph, twitterCard, favicons }.
            var title = root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                ? Normalize(titleElement.GetString())
                : null;
            var description = root.TryGetProperty("metaDescription", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String
                ? Normalize(descriptionElement.GetString())
                : null;
            return (title, description);
        }
        catch (JsonException)
        {
            // Tolerant: a malformed inventory payload contributes no facts.
            return (null, null);
        }
    }

    private static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    private static void AddMember(Dictionary<string, List<UrlMember>> groups, string key, UrlMember member)
    {
        if (!groups.TryGetValue(key, out var members))
        {
            members = [];
            groups[key] = members;
        }

        members.Add(member);
    }

    private static FindingDraft BuildFinding(string ruleKey, string valueField, string value, List<UrlMember> members)
    {
        var ordered = members.OrderBy(m => m.UrlId).ToArray();
        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [valueField] = value,
            ["urlCount"] = ordered.Length,
            ["urls"] = ordered.Select(m => new { urlId = m.UrlId, url = m.Url }).ToArray(),
        };
        return new FindingDraft(
            ruleKey,
            AuditCategory.Content,
            FindingSeverity.Warning,
            UrlId: null,
            JsonSerializer.Serialize(evidence, JsonOptions),
            RecommendationKeys.ForRule(ruleKey));
    }

    private sealed record UrlMember(long UrlId, string Url);
}
