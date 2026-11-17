using System.Text;
using System.Text.Json;
using CrawlForge.Audit.Custom;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Tests.Custom.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using Xunit;

namespace CrawlForge.Audit.Tests.Custom;

/// <summary>
/// Real-stack persistence evidence: extracted_content, content_search_results,
/// and snippet_results persist through SqliteDbProvider + SchemaMigrator +
/// SqlRepository; changed rules re-extract offline from stored bodies
/// (FS-CRAWL-001 §7.9/§9.6).
/// </summary>
public sealed class CustomPersistenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExtractedValues_PersistAndReadBack_WithFlags()
    {
        using var stack = await CustomStack.CreateAsync();
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Persist", new[]
            {
                CustomDomBuilder.Text("span", "42"),
                CustomDomBuilder.Text("em", "not-a-number"),
            }));
        var extractor = new CustomContentExtractor();
        var rules = new[]
        {
            new CustomExtractionRule("count", "span", ExtractedValueType.Number),
            new CustomExtractionRule("bad", "em", ExtractedValueType.Number),
        };

        IReadOnlyList<ExtractedValueDraft> values = await extractor.ExtractAsync(CustomContextFactory.Context(document: document), rules);
        foreach (ExtractedValueDraft value in values)
        {
            await stack.Extracted.CreateAsync(new ExtractedContentDto(
                0, CustomContextFactory.AuditId, CustomContextFactory.UrlId, value.RuleName, value.Ordinal, value.ValueJson, value.ValueType, value.IsInvalid, value.IsTruncated));
        }

        PagedResult<ExtractedContentDto> stored = await stack.Extracted.ListAsync(new ListQuery(
            Filters: new Dictionary<string, string> { ["AuditId"] = CustomContextFactory.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

        Assert.Equal(2, stored.Items.Count);
        ExtractedContentDto good = Assert.Single(stored.Items, r => r.RuleName == "count");
        Assert.Equal("42", good.TypedValueJson);
        Assert.False(good.IsInvalid);
        ExtractedContentDto bad = Assert.Single(stored.Items, r => r.RuleName == "bad");
        Assert.True(bad.IsInvalid);
        Assert.Null(bad.TypedValueJson);
    }

    [Fact]
    public async Task SearchResults_PersistAndReadBack()
    {
        using var stack = await CustomStack.CreateAsync();
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Search", new[] { CustomDomBuilder.Text("p", "needle here and needle there") }));
        var search = new CustomContentSearch();

        IReadOnlyList<ContentSearchMatch> matches = await search.SearchAsync(
            CustomContextFactory.Context(document: document), new[] { new CustomSearchItem("needle", "needle") });
        foreach (ContentSearchMatch match in matches)
        {
            await stack.SearchResults.CreateAsync(new ContentSearchResultDto(
                0, CustomContextFactory.AuditId, CustomContextFactory.UrlId, match.ItemKey, match.MatchCount, JsonSerializer.Serialize(match.Snippets, JsonOptions)));
        }

        PagedResult<ContentSearchResultDto> stored = await stack.SearchResults.ListAsync(new ListQuery(
            Filters: new Dictionary<string, string> { ["UrlId"] = CustomContextFactory.UrlId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

        ContentSearchResultDto row = Assert.Single(stored.Items);
        Assert.Equal("needle", row.SearchItemKey);
        Assert.Equal(2, row.MatchCount);
        IReadOnlyList<string>? snippets = JsonSerializer.Deserialize<IReadOnlyList<string>>(row.SnippetsJson!, JsonOptions);
        Assert.NotNull(snippets);
        Assert.NotEmpty(snippets);
    }

    [Fact]
    public async Task SnippetResults_PersistAndReadBack()
    {
        using var stack = await CustomStack.CreateAsync();
        DomDocument rendered = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Snippet", new[] { CustomDomBuilder.Text("p", "content") }));
        var runner = new JintSnippetRunner();
        var snippets = new[] { new JsSnippetDefinition("s1", "Snippet", "cf.submit('v', 7);") };

        IReadOnlyList<SnippetResultDraft> results = await runner.RunAsync(
            CustomContextFactory.Context(renderedDocument: rendered, renderingMode: RenderMode.Always), snippets);
        foreach (SnippetResultDraft result in results)
        {
            await stack.Snippets.CreateAsync(new SnippetResultDto(
                0, CustomContextFactory.AuditId, CustomContextFactory.UrlId, result.SnippetKey, result.ColumnsJson, result.RowsJson));
        }

        PagedResult<SnippetResultDto> stored = await stack.Snippets.ListAsync(new ListQuery(
            Filters: new Dictionary<string, string> { ["AuditId"] = CustomContextFactory.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

        SnippetResultDto row = Assert.Single(stored.Items);
        Assert.Equal("s1", row.SnippetKey);
        using JsonDocument rows = JsonDocument.Parse(row.RowsJson);
        Assert.Equal(7, rows.RootElement[0].GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task ReAudit_ChangedRules_ReExtractsOfflineFromStoredBody()
    {
        using var stack = await CustomStack.CreateAsync();
        const string html = "<html><head><title>Stored page</title></head><body><h1>Widget</h1><span class='price'>19.99</span><p>needle in the stored body</p></body></html>";

        // Store the raw response body in the content store (audit artifact).
        ContentArtifactDto artifact = await stack.ContentStore.PutAsync(
            CustomContextFactory.AuditId, CustomContextFactory.UrlId, ContentArtifactKind.Body, new MemoryStream(Encoding.UTF8.GetBytes(html)));

        // First audit: original rule set.
        ITolerantDomParser parser = new AngleSharpDomParser();
        PageAuditContext firstContext = CustomContextFactory.Context(document: parser.Parse(html, CustomContextFactory.PageUrl));
        var extractor = new CustomContentExtractor();
        IReadOnlyList<ExtractedValueDraft> firstValues = await extractor.ExtractAsync(
            firstContext, new[] { new CustomExtractionRule("price", ".price", ExtractedValueType.Number) });
        foreach (ExtractedValueDraft value in firstValues)
        {
            await stack.Extracted.CreateAsync(new ExtractedContentDto(
                0, CustomContextFactory.AuditId, CustomContextFactory.UrlId, value.RuleName, value.Ordinal, value.ValueJson, value.ValueType, value.IsInvalid, value.IsTruncated));
        }

        // Re-audit with CHANGED rules: rebuild the context only from stored
        // artifacts (URL record + body) — no network anywhere in this path.
        UrlRecordDto storedUrl = (await stack.Urls.ListAsync(new ListQuery(
            Filters: new Dictionary<string, string> { ["AuditId"] = CustomContextFactory.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) }))).Items.Single();
        using Stream? bodyStream = await stack.ContentStore.OpenReadAsync(artifact.StorageKey);
        Assert.NotNull(bodyStream);
        using var reader = new StreamReader(bodyStream, Encoding.UTF8);
        string storedBody = await reader.ReadToEndAsync();

        PageAuditContext reauditContext = CustomContextFactory.Context(
            document: parser.Parse(storedBody, storedUrl.CanonicalUrl),
            url: storedUrl);
        var changedRules = new[]
        {
            new CustomExtractionRule("title", "h1", ExtractedValueType.Text),
            new CustomExtractionRule("price", ".price", ExtractedValueType.Number),
        };
        IReadOnlyList<ExtractedValueDraft> secondValues = await extractor.ExtractAsync(reauditContext, changedRules);

        Assert.Equal(2, secondValues.Count);
        Assert.Equal("\"Widget\"", Assert.Single(secondValues, v => v.RuleName == "title").ValueJson);
        Assert.Equal("19.99", Assert.Single(secondValues, v => v.RuleName == "price").ValueJson);

        // Search also re-runs offline from the same stored body.
        var search = new CustomContentSearch();
        IReadOnlyList<ContentSearchMatch> matches = await search.SearchAsync(reauditContext, new[] { new CustomSearchItem("needle", "needle") });
        Assert.Equal(1, Assert.Single(matches).MatchCount);
    }

    private sealed class CustomStack : IDisposable
    {
        private readonly TempDirectory _temp;

        private CustomStack(TempDirectory temp, SqliteDbProvider provider)
        {
            _temp = temp;
            Provider = provider;
            Extracted = new SqlRepository<ExtractedContentDto>(provider);
            SearchResults = new SqlRepository<ContentSearchResultDto>(provider);
            Snippets = new SqlRepository<SnippetResultDto>(provider);
            Urls = new SqlRepository<UrlRecordDto>(provider);
            ContentStore = new CompressedContentStore(Path.Combine(temp.RootPath, "artifacts"), provider);
        }

        public SqliteDbProvider Provider { get; }

        public SqlRepository<ExtractedContentDto> Extracted { get; }

        public SqlRepository<ContentSearchResultDto> SearchResults { get; }

        public SqlRepository<SnippetResultDto> Snippets { get; }

        public SqlRepository<UrlRecordDto> Urls { get; }

        public CompressedContentStore ContentStore { get; }

        public static async Task<CustomStack> CreateAsync()
        {
            var temp = new TempDirectory();
            var provider = new SqliteDbProvider(new SqliteProviderOptions
            {
                DatabasePath = Path.Combine(temp.RootPath, "test.db"),
            });
            var migrator = new SchemaMigrator(provider);
            await migrator.MigrateAsync();

            var stack = new CustomStack(temp, provider);
            await stack.SeedAsync();
            return stack;
        }

        private async Task SeedAsync()
        {
            var projects = new SqlRepository<ProjectDto>(Provider);
            ProjectDto project = await projects.CreateAsync(new ProjectDto(
                0, "test-project", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, false));

            var configurations = new SqlRepository<CrawlConfigurationDto>(Provider);
            CrawlConfigurationDto configuration = await configurations.CreateAsync(
                CustomContextFactory.Configuration() with { Id = 0, ProjectId = project.Id });

            var audits = new SqlRepository<AuditDto>(Provider);
            await audits.CreateAsync(new AuditDto(
                0, project.Id, configuration.Id, "{}", AuditStatus.Completed, AuditPhase.Finished,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "{}", "test-engine", false));

            await Urls.CreateAsync(CustomContextFactory.UrlRecord() with { Id = 0 });
        }

        public void Dispose()
        {
            Provider.Dispose();
            _temp.Dispose();
        }
    }
}