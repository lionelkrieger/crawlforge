using System.Data.Common;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Sqlite;

namespace CrawlForge.Intelligence.Tests.NGrams;

/// <summary>
/// Real-stack fixture: SqliteDbProvider + SchemaMigrator over a temporary
/// database file, with seeding helpers for the audit → URL → link-edge →
/// artifact → finding chain. No mocks of the persistence layer.
/// </summary>
public sealed class NGramTestStack : IDisposable
{
    private readonly string _databasePath;

    private NGramTestStack(SqliteDbProvider provider, string databasePath)
    {
        Provider = provider;
        _databasePath = databasePath;
    }

    /// <summary>The real SQLite provider.</summary>
    public SqliteDbProvider Provider { get; }

    /// <summary>Creates a migrated temporary database.</summary>
    /// <returns>The initialized stack.</returns>
    public static async Task<NGramTestStack> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "cf-ngrams-" + Guid.NewGuid().ToString("N") + ".db");
        var provider = new SqliteDbProvider(new SqliteProviderOptions { DatabasePath = path });
        var migrator = new SchemaMigrator(provider);
        await migrator.MigrateAsync();
        return new NGramTestStack(provider, path);
    }

    /// <summary>Seeds project → configuration → audit and returns the audit id.</summary>
    /// <returns>The new audit identifier.</returns>
    public async Task<long> SeedAuditAsync()
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var projectId = await InsertAsync(
            "INSERT INTO `projects` (`name`, `created_at`, `updated_at`, `is_deleted`) VALUES ('ngrams', @now, @now, 0)",
            ("@now", now));
        var configurationId = await InsertAsync(
            "INSERT INTO `crawl_configurations` (`project_id`, `name`, `seeds_json`, `rendering_mode`, `created_at`, `updated_at`) " +
            "VALUES (@projectId, 'cfg', '[]', 0, @now, @now)",
            ("@projectId", projectId), ("@now", now));
        return await InsertAsync(
            "INSERT INTO `audits` (`project_id`, `configuration_id`, `configuration_snapshot_json`, `status`, `phase`, `counters_json`, `engine_version`, `is_deleted`) " +
            "VALUES (@projectId, @configurationId, '{}', 1, 0, '{}', 'test', 0)",
            ("@projectId", projectId), ("@configurationId", configurationId));
    }

    /// <summary>Seeds a URL record.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="url">Canonical URL.</param>
    /// <param name="indexable">Indexability flag.</param>
    /// <param name="statusClass">Crawl status class.</param>
    /// <param name="contentType">Content type, when known.</param>
    /// <returns>The new URL identifier.</returns>
    public async Task<long> SeedUrlAsync(
        long auditId, string url, bool indexable = true, CrawlStatusClass statusClass = CrawlStatusClass.Success, string? contentType = "text/html")
    {
        var uri = new Uri(url);
        return await InsertAsync(
            "INSERT INTO `url_records` (`audit_id`, `canonical_url`, `host`, `path`, `depth`, `discovery_source`, `status_class`, `content_type`, `robots_state`, `indexability_flags`, `is_indexable`) " +
            "VALUES (@auditId, @url, @host, @path, 0, 0, @statusClass, @contentType, 1, 0, @indexable)",
            ("@auditId", auditId), ("@url", url), ("@host", uri.Host), ("@path", uri.AbsolutePath),
            ("@statusClass", (int)statusClass), ("@contentType", (object?)contentType ?? DBNull.Value), ("@indexable", indexable ? 1 : 0));
    }

    /// <summary>Seeds an internal link edge with anchor text.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="sourceUrlId">Source URL.</param>
    /// <param name="targetUrlId">Target URL.</param>
    /// <param name="anchorText">Recorded anchor text.</param>
    /// <returns>The new edge identifier.</returns>
    public Task<long> SeedLinkEdgeAsync(long auditId, long sourceUrlId, long targetUrlId, string anchorText) =>
        InsertAsync(
            "INSERT INTO `link_edges` (`audit_id`, `source_url_id`, `canonical_target_url`, `target_url_id`, `anchor_text`, `relation_flags`, `is_internal`) " +
            "VALUES (@auditId, @source, '', @target, @anchor, 0, 1)",
            ("@auditId", auditId), ("@source", sourceUrlId), ("@target", targetUrlId), ("@anchor", anchorText));

    /// <summary>Seeds a body artifact index row.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="urlId">Owning URL.</param>
    /// <param name="storageKey">Storage key.</param>
    /// <returns>The new artifact identifier.</returns>
    public Task<long> SeedArtifactAsync(long auditId, long urlId, string storageKey) =>
        InsertAsync(
            "INSERT INTO `content_artifacts` (`audit_id`, `url_id`, `kind`, `storage_key`, `size_bytes`, `content_hash`) " +
            "VALUES (@auditId, @urlId, 0, @key, 0, '')",
            ("@auditId", auditId), ("@urlId", urlId), ("@key", storageKey));

    /// <summary>Seeds a finding row (for read-model tests).</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="ruleKey">Finding rule key.</param>
    /// <param name="evidenceJson">Evidence payload.</param>
    /// <returns>The new finding identifier.</returns>
    public Task<long> SeedFindingAsync(long auditId, string ruleKey, string evidenceJson) =>
        InsertAsync(
            "INSERT INTO `findings` (`audit_id`, `rule_key`, `category`, `severity`, `url_id`, `evidence_json`, `recommendation_key`, `is_suppressed`) " +
            "VALUES (@auditId, @ruleKey, 1, 1, NULL, @evidence, NULL, 0)",
            ("@auditId", auditId), ("@ruleKey", ruleKey), ("@evidence", evidenceJson));

    /// <summary>Seeds a user stop word.</summary>
    /// <param name="language">Language tag.</param>
    /// <param name="word">Stop word.</param>
    /// <returns>The new row identifier.</returns>
    public Task<long> SeedUserStopwordAsync(string language, string word) =>
        InsertAsync(
            "INSERT INTO `user_stopwords` (`language`, `word`, `added_at`) VALUES (@language, @word, @now)",
            ("@language", language), ("@word", word), ("@now", DateTimeOffset.UtcNow.ToString("O")));

    /// <summary>Runs a scalar query for assertions.</summary>
    /// <param name="sql">SQL text.</param>
    /// <param name="parameters">Named parameters.</param>
    /// <returns>The scalar result.</returns>
    public async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = await Provider.OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteScalarAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Provider.Dispose();
        // Microsoft.Data.Sqlite pools connections; release pooled handles so
        // the temporary database file can be deleted.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Temporary fixture file; the OS reclaims it. Never fail a test on cleanup.
            }
        }
    }

    private async Task<long> InsertAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = await Provider.OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql + "; SELECT last_insert_rowid();";
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
}

/// <summary>Contract DOM builder helpers.</summary>
public static class DomBuilder
{
    /// <summary>Creates an element node.</summary>
    /// <param name="tag">Tag name.</param>
    /// <param name="text">Direct text content.</param>
    /// <param name="children">Child nodes.</param>
    /// <param name="attributes">Attributes, when present.</param>
    /// <returns>The node.</returns>
    public static DomNode Element(string tag, string text = "", DomNode[]? children = null, Dictionary<string, string>? attributes = null) =>
        new(tag, attributes ?? [], children ?? [], text);

    /// <summary>Creates a document with an html root.</summary>
    /// <param name="url">Source URL.</param>
    /// <param name="children">Root children.</param>
    /// <param name="lang">html lang attribute, when present.</param>
    /// <returns>The document.</returns>
    public static DomDocument Document(string url, DomNode[] children, string? lang = null)
    {
        var attributes = lang is null ? [] : new Dictionary<string, string> { ["lang"] = lang };
        return new DomDocument(url, new DomNode("html", attributes, children, string.Empty), []);
    }

    /// <summary>Builds a simple page: head with title, body with headings and paragraphs.</summary>
    /// <param name="url">Source URL.</param>
    /// <param name="title">Title text.</param>
    /// <param name="h1">H1 text, when present.</param>
    /// <param name="bodyText">Body paragraph text.</param>
    /// <param name="lang">Declared language.</param>
    /// <param name="canonicalHref">rel=canonical href, when present.</param>
    /// <param name="extraHeadings">Additional H2/H3 nodes.</param>
    /// <returns>The document.</returns>
    public static DomDocument Page(
        string url, string title, string? h1, string bodyText, string? lang = "en", string? canonicalHref = null, DomNode[]? extraHeadings = null)
    {
        var headChildren = new List<DomNode> { Element("title", title) };
        if (canonicalHref is not null)
        {
            headChildren.Add(Element("link", attributes: new Dictionary<string, string> { ["rel"] = "canonical", ["href"] = canonicalHref }));
        }

        var bodyChildren = new List<DomNode>();
        if (h1 is not null)
        {
            bodyChildren.Add(Element("h1", h1));
        }

        if (extraHeadings is not null)
        {
            bodyChildren.AddRange(extraHeadings);
        }

        bodyChildren.Add(Element("p", bodyText));
        var html = Document(url, [Element("head", children: headChildren.ToArray()), Element("body", children: bodyChildren.ToArray())], lang);
        return html;
    }
}

/// <summary>
/// Tolerant-parser double: returns documents registered by HTML content or
/// URL; unregistered input yields an empty document.
/// </summary>
public sealed class ScriptedDomParser : ITolerantDomParser
{
    private readonly Dictionary<string, DomDocument> _byHtml = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DomDocument> _byUrl = new(StringComparer.Ordinal);

    /// <summary>Registers a document for an exact HTML string.</summary>
    /// <param name="html">Stored HTML.</param>
    /// <param name="document">Document to return.</param>
    public void RegisterHtml(string html, DomDocument document) => _byHtml[html] = document;

    /// <summary>Registers a document for a URL.</summary>
    /// <param name="url">Source URL.</param>
    /// <param name="document">Document to return.</param>
    public void RegisterUrl(string url, DomDocument document) => _byUrl[url] = document;

    /// <inheritdoc />
    public DomDocument Parse(string html, string url)
    {
        if (_byHtml.TryGetValue(html, out var byHtml))
        {
            return byHtml;
        }

        return _byUrl.TryGetValue(url, out var byUrl) ? byUrl : DomBuilder.Document(url, []);
    }
}

/// <summary>Scope-resolver double returning scripted scoped text and recording calls.</summary>
public sealed class RecordingScopeResolver : IContentScopeResolver
{
    private readonly string _text;

    /// <summary>Creates the resolver double.</summary>
    /// <param name="text">Scoped text to return.</param>
    public RecordingScopeResolver(string text) => _text = text;

    /// <summary>Recorded resolve calls.</summary>
    public List<ContentScopeDefinition?> Calls { get; } = [];

    /// <inheritdoc />
    public ScopedVisibleText Resolve(DomDocument document, ContentScopeDefinition? scope)
    {
        Calls.Add(scope);
        return new ScopedVisibleText(_text, _text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, []);
    }
}

/// <summary>In-memory content store double.</summary>
public sealed class InMemoryContentStore : IContentStore
{
    private readonly Dictionary<string, byte[]> _artifacts = new(StringComparer.Ordinal);

    /// <summary>Stores content under a key.</summary>
    /// <param name="storageKey">Storage key.</param>
    /// <param name="content">UTF-8 content.</param>
    public void Put(string storageKey, string content) => _artifacts[storageKey] = System.Text.Encoding.UTF8.GetBytes(content);

    /// <inheritdoc />
    public Task<ContentArtifactDto> PutAsync(long auditId, long urlId, ContentArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
    {
        using var memory = new MemoryStream();
        content.CopyTo(memory);
        var key = $"{auditId}/{urlId}/{kind}";
        _artifacts[key] = memory.ToArray();
        return Task.FromResult(new ContentArtifactDto(0, auditId, urlId, kind, key, memory.Length, string.Empty));
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(_artifacts.TryGetValue(storageKey, out var bytes) ? new MemoryStream(bytes) : null);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_artifacts.Remove(storageKey));
}

/// <summary>PageAuditContext factory for n-gram tests.</summary>
public static class ContextFactory
{
    /// <summary>Creates a URL record DTO.</summary>
    /// <param name="id">URL identifier.</param>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="url">Canonical URL.</param>
    /// <param name="indexable">Indexability.</param>
    /// <param name="statusClass">Status class.</param>
    /// <param name="contentType">Content type.</param>
    /// <returns>The DTO.</returns>
    public static UrlRecordDto Url(
        long id, long auditId, string url, bool indexable = true, CrawlStatusClass statusClass = CrawlStatusClass.Success, string? contentType = "text/html")
    {
        var uri = new Uri(url);
        return new UrlRecordDto(
            id, auditId, url, uri.Host, uri.AbsolutePath, 0, DiscoverySource.Seed, statusClass, 200, null,
            contentType, null, null, RobotsState.Allowed, IndexabilityFlags.None, indexable);
    }

    /// <summary>Creates a minimal configuration DTO.</summary>
    /// <param name="scope">Optional content scope.</param>
    /// <returns>The DTO.</returns>
    public static CrawlConfigurationDto Configuration(ContentScopeDefinition? scope = null) =>
        new(1, 1, "cfg", "[]", null, null, null, RenderMode.None, null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            ContentScope = scope,
        };

    /// <summary>Creates a page audit context.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="url">URL record.</param>
    /// <param name="document">Parsed document.</param>
    /// <param name="scopedText">Resolved scoped text, when present.</param>
    /// <param name="configuration">Configuration; null uses a default.</param>
    /// <returns>The context.</returns>
    public static PageAuditContext Create(
        long auditId, UrlRecordDto url, DomDocument? document, ScopedVisibleText? scopedText = null, CrawlConfigurationDto? configuration = null) =>
        new(auditId, url, configuration ?? Configuration(), null, document, null, scopedText, []);
}
