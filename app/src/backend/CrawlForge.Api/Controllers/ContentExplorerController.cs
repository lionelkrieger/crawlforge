using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Content explorers (FS-CRAWL-002 §7.9/§7.15/§7.16/§7.17 and §10.7): content
/// comparison across two audits, custom extraction grid and summary, custom
/// search matches, JavaScript snippet outputs, and technology detections. All
/// reads are local projections of persisted rows.
/// </summary>
[Route("api")]
public sealed class ContentExplorerController : ApiControllerBase
{
    private readonly IReadRepository<AuditDto> _audits;
    private readonly IReadRepository<UrlRecordDto> _urls;
    private readonly IReadRepository<UrlElementDto> _elements;
    private readonly IReadRepository<ExtractedContentDto> _extractions;
    private readonly IReadRepository<ContentSearchResultDto> _searches;
    private readonly IReadRepository<SnippetResultDto> _snippets;
    private readonly IReadRepository<TechnologyDetectionDto> _technologies;

    /// <summary>Creates the controller.</summary>
    /// <param name="audits">Audit read repository.</param>
    /// <param name="urls">URL read repository.</param>
    /// <param name="elements">Element read repository.</param>
    /// <param name="extractions">Extraction read repository.</param>
    /// <param name="searches">Search read repository.</param>
    /// <param name="snippets">Snippet read repository.</param>
    /// <param name="technologies">Technology read repository.</param>
    public ContentExplorerController(
        IReadRepository<AuditDto> audits,
        IReadRepository<UrlRecordDto> urls,
        IReadRepository<UrlElementDto> elements,
        IReadRepository<ExtractedContentDto> extractions,
        IReadRepository<ContentSearchResultDto> searches,
        IReadRepository<SnippetResultDto> snippets,
        IReadRepository<TechnologyDetectionDto> technologies)
    {
        _audits = audits;
        _urls = urls;
        _elements = elements;
        _extractions = extractions;
        _searches = searches;
        _snippets = snippets;
        _technologies = technologies;
    }

    /// <summary>Compares one page's content across two audits.</summary>
    /// <param name="auditIdA">First audit identifier.</param>
    /// <param name="auditIdB">Second audit identifier.</param>
    /// <param name="canonicalUrl">Canonical URL to compare.</param>
    /// <param name="hostMapping">Whether to align by path across hosts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The content comparison.</returns>
    [HttpGet("audits/compare/content")]
    public async Task<IActionResult> CompareContent(
        [FromQuery] string? auditIdA = null,
        [FromQuery] string? auditIdB = null,
        [FromQuery] string? canonicalUrl = null,
        [FromQuery] bool hostMapping = false,
        CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(auditIdA, out var idA) || !long.TryParse(auditIdB, out var idB) || string.IsNullOrWhiteSpace(canonicalUrl))
        {
            return ValidationError("auditIdA, auditIdB, and canonicalUrl are required.", [new ApiFieldError("canonicalUrl", "Provide both audit identifiers and a canonical URL.")]);
        }

        if (await _audits.GetByIdAsync(idA, cancellationToken) is null || await _audits.GetByIdAsync(idB, cancellationToken) is null)
        {
            return NotFoundError("One or both audits were not found.");
        }

        var urlA = await FindUrlAsync(idA, canonicalUrl, hostMapping, cancellationToken);
        var urlB = await FindUrlAsync(idB, canonicalUrl, hostMapping, cancellationToken);
        var headA = urlA is null ? new JsonObject() : await LoadHeadAsync(urlA.Id, cancellationToken);
        var headB = urlB is null ? new JsonObject() : await LoadHeadAsync(urlB.Id, cancellationToken);
        var headingsA = urlA is null ? [] : await LoadHeadingsAsync(urlA.Id, cancellationToken);
        var headingsB = urlB is null ? [] : await LoadHeadingsAsync(urlB.Id, cancellationToken);
        var textA = urlA is null ? null : await LoadWordCountAsync(urlA.Id, cancellationToken);
        var textB = urlB is null ? null : await LoadWordCountAsync(urlB.Id, cancellationToken);

        var title = Diff(headA["title"]?.GetValue<string>(), headB["title"]?.GetValue<string>());
        var headings = new List<ContentDiffWire>();
        for (var i = 0; i < Math.Max(headingsA.Count, headingsB.Count); i++)
        {
            headings.Add(Diff(i < headingsA.Count ? headingsA[i] : null, i < headingsB.Count ? headingsB[i] : null));
        }

        var metadataChanges = new List<MetadataChangeWire>();
        foreach (var field in new[] { "metaDescription", "metaRobots", "viewport", "charset", "canonicalUrl" })
        {
            var before = headA[field]?.GetValue<string>();
            var after = headB[field]?.GetValue<string>();
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                metadataChanges.Add(new MetadataChangeWire(field, before, after));
            }
        }

        return OkEnvelope(new ContentCompareResponseWire(
            canonicalUrl,
            title,
            headings,
            Diff(textA?.ToString(), textB?.ToString()),
            metadataChanges));
    }

    /// <summary>Lists custom extraction rows (dynamic columns).</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="ruleKey">Rule filter.</param>
    /// <param name="urlId">URL filter.</param>
    /// <param name="columns">Column selection.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The extraction grid.</returns>
    [HttpGet("audits/{id:long}/extracted-content")]
    public async Task<IActionResult> ExtractedContent(
        long id,
        [FromQuery] string? ruleKey = null,
        [FromQuery] string? urlId = null,
        [FromQuery] string? columns = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var filters = new Dictionary<string, string> { ["auditId"] = id.ToString() };
        if (ruleKey is not null)
        {
            filters["ruleName"] = ruleKey;
        }

        if (urlId is not null)
        {
            filters["urlId"] = urlId;
        }

        var rows = await _extractions.ListAsync(new ListQuery(0, 10_000, filters), cancellationToken);
        var urlMap = await LoadUrlMapAsync(id, cancellationToken);
        var selectedColumns = columns is null ? null : columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

        var grouped = rows.Items
            .GroupBy(r => (r.UrlId, r.RuleName))
            .Select(g =>
            {
                var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var value in g.OrderBy(v => v.Ordinal))
                {
                    var column = value.Ordinal.ToString();
                    if (selectedColumns is null || selectedColumns.Contains(column))
                    {
                        values[column] = ParseValue(value.TypedValueJson);
                    }
                }

                return new ExtractedContentRowWire(g.Key.UrlId.ToString(), urlMap.GetValueOrDefault(g.Key.UrlId, string.Empty), g.Key.RuleName, values);
            })
            .OrderBy(r => r.UrlId, StringComparer.Ordinal)
            .ThenBy(r => r.RuleKey, StringComparer.Ordinal)
            .ToList();

        var effectiveColumns = grouped.SelectMany(r => r.Values.Keys).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var page = grouped.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        return OkEnvelope(new ExtractedContentResponseWire(effectiveColumns, page), new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), grouped.Count));
    }

    /// <summary>Returns per-rule extraction summaries.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The extraction summary.</returns>
    [HttpGet("audits/{id:long}/extracted-content/summary")]
    public async Task<IActionResult> ExtractionSummary(long id, CancellationToken cancellationToken)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var rows = await _extractions.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken);
        var rules = rows.Items
            .GroupBy(r => r.RuleName, StringComparer.Ordinal)
            .Select(g => new ExtractionRuleSummaryWire(
                g.Key,
                g.Select(r => r.UrlId).Distinct().Count(),
                g.Count(r => !r.IsInvalid),
                g.Count(r => r.IsInvalid),
                g.Count(r => r.IsTruncated)))
            .OrderBy(r => r.RuleKey, StringComparer.Ordinal)
            .ToList();
        return OkEnvelope(new ExtractionSummaryResponseWire(rules));
    }

    /// <summary>Lists custom search matches with a per-item overview.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="item">Search item filter.</param>
    /// <param name="urlId">URL filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The search results.</returns>
    [HttpGet("audits/{id:long}/search-results")]
    public async Task<IActionResult> SearchResults(
        long id,
        [FromQuery] string? item = null,
        [FromQuery] string? urlId = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var filters = new Dictionary<string, string> { ["auditId"] = id.ToString() };
        if (item is not null)
        {
            filters["searchItemKey"] = item;
        }

        if (urlId is not null)
        {
            filters["urlId"] = urlId;
        }

        var rows = await _searches.ListAsync(new ListQuery(0, 10_000, filters), cancellationToken);
        var urlMap = await LoadUrlMapAsync(id, cancellationToken);
        var overview = rows.Items
            .GroupBy(r => r.SearchItemKey, StringComparer.Ordinal)
            .Select(g => new SearchItemOverviewWire(g.Key, g.Sum(r => (long)r.MatchCount), g.Select(r => r.UrlId).Distinct().Count()))
            .OrderBy(o => o.Item, StringComparer.Ordinal)
            .ToList();
        var matches = rows.Items
            .Select(r => new SearchMatchWire(
                r.UrlId.ToString(),
                urlMap.GetValueOrDefault(r.UrlId, string.Empty),
                r.SearchItemKey,
                ProjectWireMapper.ParseStringList(r.SnippetsJson).FirstOrDefault(),
                r.MatchCount))
            .OrderBy(m => m.Item, StringComparer.Ordinal)
            .ThenBy(m => m.UrlId, StringComparer.Ordinal)
            .ToList();
        var page = matches.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        return OkEnvelope(new SearchResultsResponseWire(overview, page), new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), matches.Count));
    }

    /// <summary>Lists JavaScript snippet output rows (dynamic columns).</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="snippetKey">Snippet filter.</param>
    /// <param name="urlId">URL filter.</param>
    /// <param name="columns">Column selection.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snippet grid.</returns>
    [HttpGet("audits/{id:long}/snippet-results")]
    public async Task<IActionResult> SnippetResults(
        long id,
        [FromQuery] string? snippetKey = null,
        [FromQuery] string? urlId = null,
        [FromQuery] string? columns = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var filters = new Dictionary<string, string> { ["auditId"] = id.ToString() };
        if (snippetKey is not null)
        {
            filters["snippetKey"] = snippetKey;
        }

        if (urlId is not null)
        {
            filters["urlId"] = urlId;
        }

        var rows = await _snippets.ListAsync(new ListQuery(0, 10_000, filters), cancellationToken);
        var urlMap = await LoadUrlMapAsync(id, cancellationToken);
        var selectedColumns = columns is null ? null : columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

        var projected = rows.Items
            .Select(r =>
            {
                var values = ParseSnippetValues(r.RowsJson);
                if (selectedColumns is not null)
                {
                    values = values.Where(kv => selectedColumns.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                }

                return new SnippetResultRowWire(r.UrlId.ToString(), urlMap.GetValueOrDefault(r.UrlId, string.Empty), r.SnippetKey, values);
            })
            .OrderBy(r => r.UrlId, StringComparer.Ordinal)
            .ThenBy(r => r.SnippetKey, StringComparer.Ordinal)
            .ToList();

        var effectiveColumns = projected.SelectMany(r => r.Values.Keys).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var page = projected.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        return OkEnvelope(new SnippetResultsResponseWire(effectiveColumns, page), new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), projected.Count));
    }

    /// <summary>Lists technology detections.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="category">Category filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged technology detections.</returns>
    [HttpGet("audits/{id:long}/technologies")]
    public async Task<IActionResult> Technologies(
        long id,
        [FromQuery] string? category = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var filters = new Dictionary<string, string> { ["auditId"] = id.ToString() };
        if (category is not null)
        {
            filters["category"] = category;
        }

        var rows = await _technologies.ListAsync(new ListQuery(0, 10_000, filters), cancellationToken);
        var items = rows.Items
            .Select(t => new TechnologyDetectionWire(t.TechnologyKey, t.Category, t.Confidence, null, t.UrlId.ToString(), null))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        var page = items.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        return OkEnvelope(page, new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), rows.TotalCount));
    }

    private async Task<UrlRecordDto?> FindUrlAsync(long auditId, string canonicalUrl, bool hostMapping, CancellationToken cancellationToken)
    {
        var urls = await _urls.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }), cancellationToken);
        var exact = urls.Items.FirstOrDefault(u => string.Equals(u.CanonicalUrl, canonicalUrl, StringComparison.OrdinalIgnoreCase));
        if (exact is not null || !hostMapping || !Uri.TryCreate(canonicalUrl, UriKind.Absolute, out var uri))
        {
            return exact;
        }

        return urls.Items.FirstOrDefault(u => string.Equals(u.Path, uri.AbsolutePath, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<JsonObject> LoadHeadAsync(long urlId, CancellationToken cancellationToken)
    {
        var head = await _elements.ListAsync(new ListQuery(0, 1, new Dictionary<string, string>
        {
            ["urlId"] = urlId.ToString(),
            ["elementGroup"] = "document_head",
        }), cancellationToken);
        return head.Items.Count > 0 ? ProjectWireMapper.ParseObject(head.Items[0].PayloadJson) : new JsonObject();
    }

    private async Task<IReadOnlyList<string>> LoadHeadingsAsync(long urlId, CancellationToken cancellationToken)
    {
        var headings = await _elements.ListAsync(new ListQuery(0, 1, new Dictionary<string, string>
        {
            ["urlId"] = urlId.ToString(),
            ["elementGroup"] = "headings",
        }), cancellationToken);
        if (headings.Items.Count == 0)
        {
            return [];
        }

        var outline = ProjectWireMapper.ParseObject(headings.Items[0].PayloadJson)["outline"] as JsonArray;
        return outline?.Select(h => h?["text"]?.GetValue<string>() ?? string.Empty).ToList() ?? [];
    }

    private async Task<long?> LoadWordCountAsync(long urlId, CancellationToken cancellationToken)
    {
        var stats = await _elements.ListAsync(new ListQuery(0, 1, new Dictionary<string, string>
        {
            ["urlId"] = urlId.ToString(),
            ["elementGroup"] = "text_statistics",
        }), cancellationToken);
        return stats.Items.Count > 0 ? ProjectWireMapper.ParseObject(stats.Items[0].PayloadJson)["visibleWordCount"]?.GetValue<long>() : null;
    }

    private async Task<IReadOnlyDictionary<long, string>> LoadUrlMapAsync(long auditId, CancellationToken cancellationToken)
    {
        var urls = await _urls.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }), cancellationToken);
        return urls.Items.ToDictionary(u => u.Id, u => u.CanonicalUrl);
    }

    private static ContentDiffWire Diff(string? before, string? after) =>
        new(before, after, !string.Equals(before, after, StringComparison.Ordinal));

    private static JsonElement ParseValue(string? typedValueJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(typedValueJson) ? "null" : typedValueJson);
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseSnippetValues(string? rowsJson)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(rowsJson))
        {
            return values;
        }

        using var document = JsonDocument.Parse(rowsJson);
        var root = document.RootElement;
        var firstRow = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
        if (firstRow.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in firstRow.EnumerateObject())
            {
                values[property.Name] = property.Value.Clone();
            }
        }

        return values;
    }
}
