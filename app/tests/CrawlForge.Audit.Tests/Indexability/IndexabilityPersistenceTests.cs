using System.Text;
using System.Text.Json;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.Tests.Indexability.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using Xunit;

namespace CrawlForge.Audit.Tests.Indexability;

/// <summary>
/// Real-stack persistence evidence: findings and the header_directives element
/// group persist through SqliteDbProvider + SchemaMigrator + SqlRepository,
/// and every indexability rule re-runs offline from stored artifacts on
/// re-audit (FS-CRAWL-001 §9.6), reproducing identical findings.
/// </summary>
public sealed class IndexabilityPersistenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Findings_PersistAndReadBack_ThroughRealStack()
    {
        using var stack = await RealStack.CreateAsync();
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page(
                "Conflict page",
                new[] { DomBuilder.Meta("robots", "index, follow"), DomBuilder.Link("canonical", "/elsewhere") },
                new[] { DomBuilder.Text("p", "content") }));
        PageAuditContext context = IndexabilityContextFactory.Context(
            document: document,
            fetch: IndexabilityContextFactory.Fetch(headers: new Dictionary<string, string> { ["X-Robots-Tag"] = "noindex" }));

        var indexability = new IndexabilityAnalyzer(new Soft404Detector());
        var equivalency = new HeaderHtmlEquivalencyAnalyzer();
        List<FindingDraft> drafts = new();
        drafts.AddRange(await indexability.AnalyzeAsync(context));
        drafts.AddRange(await equivalency.AnalyzeAsync(context));
        Assert.NotEmpty(drafts);

        foreach (FindingDraft draft in drafts)
        {
            await stack.Findings.CreateAsync(new FindingDto(
                0, context.AuditId, draft.RuleKey, draft.Category, draft.Severity, draft.UrlId, draft.EvidenceJson, draft.RecommendationKey, false));
        }

        PagedResult<FindingDto> stored = await stack.Findings.ListAsync(new ListQuery(
            Limit: 200, Filters: new Dictionary<string, string> { ["AuditId"] = context.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

        Assert.Equal(drafts.Count, stored.Items.Count);
        foreach (FindingDraft draft in drafts)
        {
            Assert.Contains(stored.Items, f =>
                f.RuleKey == draft.RuleKey
                && f.Severity == draft.Severity
                && f.Category == draft.Category
                && f.EvidenceJson == draft.EvidenceJson
                && f.UrlId == draft.UrlId);
        }

        // The criterion #48 conflict survived persistence with dual evidence.
        FindingDto conflict = Assert.Single(stored.Items, f => f.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict && f.Severity == FindingSeverity.Error);
        using JsonDocument evidence = JsonDocument.Parse(conflict.EvidenceJson);
        Assert.Equal("noindex", evidence.RootElement.GetProperty("headerValue").GetString());
        Assert.Equal("index, follow", evidence.RootElement.GetProperty("htmlValue").GetString());
    }

    [Fact]
    public async Task HeaderDirectivesGroup_PersistsAsUrlElement()
    {
        using var stack = await RealStack.CreateAsync();
        PageAuditContext context = IndexabilityContextFactory.Context(
            fetch: IndexabilityContextFactory.Fetch(headers: new Dictionary<string, string>
            {
                ["X-Robots-Tag"] = "noindex",
                ["Link"] = "<https://example.com/page>; rel=\"canonical\"",
            }));
        var analyzer = new HeaderHtmlEquivalencyAnalyzer();
        ElementInventoryItem item = analyzer.BuildHeaderDirectivesInventory(context)!;

        UrlElementDto stored = await stack.Elements.CreateAsync(new UrlElementDto(0, context.Url.Id, item.Group, item.PayloadJson, item.Ordinal));

        PagedResult<UrlElementDto> readBack = await stack.Elements.ListAsync(new ListQuery(
            Filters: new Dictionary<string, string> { ["UrlId"] = context.Url.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

        UrlElementDto element = Assert.Single(readBack.Items);
        Assert.Equal(ElementGroups.HeaderDirectives, element.ElementGroup);
        Assert.Equal(item.PayloadJson, element.PayloadJson);
        Assert.Equal(0, element.Ordinal);
        Assert.True(stored.Id > 0);
    }

    [Fact]
    public async Task ReAudit_OfflineFromStoredArtifacts_ReproducesFindings()
    {
        using var stack = await RealStack.CreateAsync();
        const string html = "<html><head><title>404 - Page Not Found</title><meta name=\"robots\" content=\"index\"></head><body><p>The page you requested was not found.</p></body></html>";
        var headers = new Dictionary<string, string> { ["X-Robots-Tag"] = "noindex" };
        PageAuditContext original = IndexabilityContextFactory.Context(
            document: DomBuilder.Document(IndexabilityContextFactory.PageUrl, DomBuilder.Page(
                "404 - Page Not Found",
                new[] { DomBuilder.Meta("robots", "index") },
                new[] { DomBuilder.Text("p", "The page you requested was not found.") })),
            fetch: IndexabilityContextFactory.Fetch(headers: headers, body: html));

        // First audit: run rules and persist findings. This is a STANDALONE
        // composition (no audit pipeline), so soft-404 emission is explicitly
        // opted in — in the pipeline the dedicated ISoft404Detector step is the
        // sole emitter (WP-09-05 single-emission contract).
        var indexability = new IndexabilityAnalyzer(
            new Soft404Detector(), new IndexabilityAnalyzerOptions(EmitSoft404Findings: true));
        var equivalency = new HeaderHtmlEquivalencyAnalyzer();
        List<FindingDraft> firstRun = new();
        firstRun.AddRange(await indexability.AnalyzeAsync(original));
        firstRun.AddRange(await equivalency.AnalyzeAsync(original));
        foreach (FindingDraft draft in firstRun)
        {
            await stack.Findings.CreateAsync(new FindingDto(
                0, original.AuditId, draft.RuleKey, draft.Category, draft.Severity, draft.UrlId, draft.EvidenceJson, null, false));
        }

        // Store the raw response artifact (headers + body) in the content store.
        string artifactJson = JsonSerializer.Serialize(new StoredResponse(headers, html), JsonOptions);
        ContentArtifactDto artifact = await stack.ContentStore.PutAsync(
            original.AuditId, original.Url.Id, ContentArtifactKind.Body, new MemoryStream(Encoding.UTF8.GetBytes(artifactJson)));

        // Re-audit: rebuild the context ONLY from stored artifacts + URL record.
        UrlRecordDto storedUrl = (await stack.Urls.ListAsync(new ListQuery(
            Filters: new Dictionary<string, string> { ["AuditId"] = original.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) }))).Items.Single();
        using Stream? artifactStream = await stack.ContentStore.OpenReadAsync(artifact.StorageKey);
        Assert.NotNull(artifactStream);
        using var reader = new StreamReader(artifactStream, Encoding.UTF8);
        StoredResponse storedResponse = JsonSerializer.Deserialize<StoredResponse>(await reader.ReadToEndAsync(), JsonOptions)!;

        ITolerantDomParser parser = new FakeTolerantDomParser();
        DomDocument reparsed = parser.Parse(storedResponse.Body, storedUrl.CanonicalUrl);
        PageAuditContext reauditContext = new(
            AuditId: original.AuditId,
            Url: storedUrl,
            Configuration: original.Configuration,
            Fetch: new HttpFetchResult(
                storedUrl.CanonicalUrl,
                storedUrl.FinalStatusCode ?? 200,
                storedResponse.Headers,
                Encoding.UTF8.GetBytes(storedResponse.Body),
                TimeSpan.Zero,
                TimeSpan.Zero),
            Document: reparsed,
            RenderedDocument: null,
            ScopedText: null,
            CapturedRequests: null);

        List<FindingDraft> secondRun = new();
        secondRun.AddRange(await indexability.AnalyzeAsync(reauditContext));
        secondRun.AddRange(await equivalency.AnalyzeAsync(reauditContext));

        // Identical rule outcomes from stored artifacts — fully offline.
        Assert.Equal(firstRun.Count, secondRun.Count);
        foreach (FindingDraft draft in firstRun)
        {
            Assert.Contains(secondRun, d => d.RuleKey == draft.RuleKey && d.Severity == draft.Severity && d.EvidenceJson == draft.EvidenceJson);
        }

        Assert.Contains(secondRun, d => d.RuleKey == IndexabilityRuleKeys.Soft404);
        Assert.Contains(secondRun, d => d.RuleKey == FindingRuleKeys.IndexabilityHeaderHtmlConflict && d.Severity == FindingSeverity.Error);
    }

    private sealed record StoredResponse(IReadOnlyDictionary<string, string> Headers, string Body);

    private sealed class RealStack : IDisposable
    {
        private readonly TempDirectory _temp;

        private RealStack(TempDirectory temp, SqliteDbProvider provider)
        {
            _temp = temp;
            Provider = provider;
            Findings = new SqlRepository<FindingDto>(provider);
            Elements = new SqlRepository<UrlElementDto>(provider);
            Urls = new SqlRepository<UrlRecordDto>(provider);
            ContentStore = new CompressedContentStore(Path.Combine(temp.RootPath, "artifacts"), provider);
        }

        public SqliteDbProvider Provider { get; }

        public SqlRepository<FindingDto> Findings { get; }

        public SqlRepository<UrlElementDto> Elements { get; }

        public SqlRepository<UrlRecordDto> Urls { get; }

        public CompressedContentStore ContentStore { get; }

        public static async Task<RealStack> CreateAsync()
        {
            var temp = new TempDirectory();
            var provider = new SqliteDbProvider(new SqliteProviderOptions
            {
                DatabasePath = Path.Combine(temp.RootPath, "test.db"),
            });
            var migrator = new SchemaMigrator(provider);
            await migrator.MigrateAsync();

            var stack = new RealStack(temp, provider);
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
                IndexabilityContextFactory.Configuration() with { Id = 0, ProjectId = project.Id });

            var audits = new SqlRepository<AuditDto>(Provider);
            await audits.CreateAsync(new AuditDto(
                0, project.Id, configuration.Id, "{}", AuditStatus.Completed, AuditPhase.Finished,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "{}", "test-engine", false));

            await Urls.CreateAsync(IndexabilityContextFactory.UrlRecord() with { Id = 0 });
        }

        public void Dispose()
        {
            Provider.Dispose();
            _temp.Dispose();
        }
    }
}