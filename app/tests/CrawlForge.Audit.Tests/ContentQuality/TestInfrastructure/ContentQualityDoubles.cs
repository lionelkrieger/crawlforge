using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>Builds contract DOM trees for fixtures without a real parser.</summary>
internal static class DomBuilder
{
    public static DomNode Element(string tag, IReadOnlyList<DomNode>? children = null, string text = "", IReadOnlyDictionary<string, string>? attributes = null) =>
        new(tag, attributes ?? new Dictionary<string, string>(), children ?? [], text);

    public static DomDocument Document(string url, DomNode root) => new(url, root, []);

    /// <summary>A minimal page: html[lang] → head(title, meta description) + body paragraphs.</summary>
    public static DomDocument Page(string? title, string? description, string bodyText, string? lang = "en")
    {
        var headChildren = new List<DomNode>();
        if (title is not null)
        {
            headChildren.Add(Element("title", text: title));
        }

        if (description is not null)
        {
            headChildren.Add(Element("meta", attributes: new Dictionary<string, string>
            {
                ["name"] = "description",
                ["content"] = description,
            }));
        }

        var htmlAttributes = lang is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["lang"] = lang };

        var root = Element("html",
        [
            Element("head", headChildren),
            Element("body", [Element("p", text: bodyText)]),
        ], attributes: htmlAttributes);

        return Document("https://example.com/page", root);
    }
}

/// <summary>Tolerant-parser double mapping registered HTML to fixture documents.</summary>
internal sealed class ScriptedDomParser : ITolerantDomParser
{
    private readonly Dictionary<string, DomDocument> _documents = new(StringComparer.Ordinal);

    public ScriptedDomParser Register(string html, DomDocument document)
    {
        _documents[html] = document;
        return this;
    }

    public DomDocument Parse(string html, string url) =>
        _documents.TryGetValue(html, out var document)
            ? document
            : DomBuilder.Document(url, DomBuilder.Element("html", [DomBuilder.Element("body", text: html)]));
}

/// <summary>In-memory content store double (locked IContentStore contract).</summary>
internal sealed class InMemoryContentStore : IContentStore
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    private long _nextId = 1;

    public int OpenReadCount { get; private set; }

    public Task<ContentArtifactDto> PutAsync(long auditId, long urlId, ContentArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var key = $"blob-{_nextId}";
        _blobs[key] = buffer.ToArray();
        var dto = new ContentArtifactDto(_nextId++, auditId, urlId, kind, key, buffer.Length, "hash");
        return Task.FromResult(dto);
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        OpenReadCount++;
        return Task.FromResult<Stream?>(_blobs.TryGetValue(storageKey, out var bytes) ? new MemoryStream(bytes) : null);
    }

    public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_blobs.Remove(storageKey));
}

/// <summary>Artifact-index repository double honoring auditId/kind filters and urlId sort.</summary>
internal sealed class InMemoryArtifactRepository : IReadRepository<ContentArtifactDto>
{
    private readonly List<ContentArtifactDto> _items = [];

    public InMemoryArtifactRepository Add(ContentArtifactDto artifact)
    {
        _items.Add(artifact);
        return this;
    }

    public Task<ContentArtifactDto?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.FirstOrDefault(i => i.Id == id));

    public Task<PagedResult<ContentArtifactDto>> ListAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        IEnumerable<ContentArtifactDto> filtered = _items;
        if (query.Filters is not null)
        {
            foreach (var (field, value) in query.Filters)
            {
                filtered = field switch
                {
                    "auditId" => filtered.Where(i => i.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) == value),
                    "kind" => filtered.Where(i => ((int)i.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture) == value),
                    "urlId" => filtered.Where(i => i.UrlId.ToString(System.Globalization.CultureInfo.InvariantCulture) == value),
                    _ => filtered,
                };
            }
        }

        if (string.Equals(query.SortBy, "urlId", StringComparison.Ordinal))
        {
            filtered = query.SortDescending
                ? filtered.OrderByDescending(i => i.UrlId)
                : filtered.OrderBy(i => i.UrlId);
        }

        var materialized = filtered.ToList();
        var page = materialized.Skip(query.Offset).Take(query.Limit).ToList();
        return Task.FromResult(new PagedResult<ContentArtifactDto>(page, materialized.Count, query.Offset, query.Limit));
    }
}

/// <summary>Scope-resolver double returning scripted scoped text and recording calls.</summary>
internal sealed class RecordingScopeResolver : IContentScopeResolver
{
    private readonly ScopedVisibleText _result;

    public RecordingScopeResolver(ScopedVisibleText result) => _result = result;

    public List<(DomDocument Document, ContentScopeDefinition? Scope)> Calls { get; } = [];

    public ScopedVisibleText Resolve(DomDocument document, ContentScopeDefinition? scope)
    {
        Calls.Add((document, scope));
        return _result;
    }
}

/// <summary>Builds PageAuditContext fixtures over the locked contract shapes.</summary>
internal static class ContextFactory
{
    public static UrlRecordDto Url(long id = 7, long auditId = 1) =>
        new(id, auditId, "https://example.com/page", "example.com", "/page", 0,
            DiscoverySource.Seed, CrawlStatusClass.Success, 200, null, "text/html", 1024, null,
            RobotsState.Allowed, IndexabilityFlags.None, true);

    public static CrawlConfigurationDto Configuration(ContentScopeDefinition? scope = null, SerpBudgetSettings? budgets = null) =>
        new(1, 1, "test", "[\"https://example.com\"]", null, null, null, RenderMode.None,
            null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        {
            ContentScope = scope,
            SerpBudgets = budgets,
        };

    public static PageAuditContext Create(
        DomDocument? document,
        string? html = null,
        ContentScopeDefinition? scope = null,
        ScopedVisibleText? scopedText = null,
        SerpBudgetSettings? budgets = null,
        long auditId = 1,
        long urlId = 7)
    {
        var fetch = html is null
            ? null
            : new HttpFetchResult("https://example.com/page", 200, new Dictionary<string, string>(), System.Text.Encoding.UTF8.GetBytes(html), TimeSpan.Zero, TimeSpan.Zero);
        return new PageAuditContext(auditId, Url(urlId, auditId), Configuration(scope, budgets), fetch, document, null, scopedText, null);
    }
}
