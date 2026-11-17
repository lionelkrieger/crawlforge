using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoring;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using CrawlForge.Intelligence.Geo;
using CrawlForge.Intelligence.LogAnalyzer;
using CrawlForge.Intelligence.NGrams;
using CrawlForge.Mcp;
using CrawlForge.Security.Secrets;
using Xunit;

namespace CrawlForge.Mcp.Tests;

/// <summary>
/// Real completed persistence stack for MCP integration-faithful tests: temp
/// embedded SQLite, the full migration catalog, locked generic repositories,
/// the real compressed content store, and the accepted read models.
/// </summary>
public sealed class McpSqliteStackFixture : IAsyncLifetime
{
    private string _directory = null!;

    /// <summary>SQLite provider.</summary>
    public SqliteDbProvider Provider { get; private set; } = null!;

    /// <summary>Compressed content store.</summary>
    public CompressedContentStore ContentStore { get; private set; } = null!;

    /// <summary>Secret protector over local app entropy.</summary>
    public AppEntropySecretProtector Protector { get; private set; } = null!;

    /// <summary>Project rows.</summary>
    public SqlRepository<ProjectDto> Projects { get; private set; } = null!;

    /// <summary>Configuration rows.</summary>
    public SqlRepository<CrawlConfigurationDto> Configurations { get; private set; } = null!;

    /// <summary>Audit rows.</summary>
    public SqlRepository<AuditDto> Audits { get; private set; } = null!;

    /// <summary>Finding rows.</summary>
    public SqlRepository<FindingDto> Findings { get; private set; } = null!;

    /// <summary>URL records.</summary>
    public SqlRepository<UrlRecordDto> Urls { get; private set; } = null!;

    /// <summary>Metric summaries.</summary>
    public SqlRepository<MetricSummaryDto> Metrics { get; private set; } = null!;

    /// <summary>Audit scores.</summary>
    public SqlRepository<AuditScoreDto> Scores { get; private set; } = null!;

    /// <summary>Schedule definitions.</summary>
    public SqlRepository<ScheduleDefinitionDto> Schedules { get; private set; } = null!;

    /// <summary>Schedule runs.</summary>
    public SqlRepository<ScheduleRunDto> ScheduleRuns { get; private set; } = null!;

    /// <summary>WAF detections.</summary>
    public SqlRepository<WafDetectionDto> WafDetections { get; private set; } = null!;

    /// <summary>Host throttle events.</summary>
    public SqlRepository<HostThrottleEventDto> ThrottleEvents { get; private set; } = null!;

    /// <summary>Redirect mappings.</summary>
    public SqlRepository<RedirectMappingDto> RedirectMappings { get; private set; } = null!;

    /// <summary>Log imports.</summary>
    public SqlRepository<LogImportDto> LogImports { get; private set; } = null!;

    /// <summary>Log URL aggregates.</summary>
    public SqlRepository<LogUrlAggregateDto> LogAggregates { get; private set; } = null!;

    /// <summary>Semantic opportunities.</summary>
    public SqlRepository<SemanticLinkOpportunityDto> Opportunities { get; private set; } = null!;

    /// <summary>Semantic clusters.</summary>
    public SqlRepository<SemanticClusterDto> Clusters { get; private set; } = null!;

    /// <summary>Analytics detections.</summary>
    public SqlRepository<AnalyticsDetectionDto> AnalyticsDetections { get; private set; } = null!;

    /// <summary>Extracted content values.</summary>
    public SqlRepository<ExtractedContentDto> ExtractedContent { get; private set; } = null!;

    /// <summary>Governance matrix rows.</summary>
    public SqlRepository<GovernanceMatrixRowDto> GovernanceRows { get; private set; } = null!;

    /// <summary>N-gram frequencies.</summary>
    public SqlRepository<NgramFrequencyDto> NgramFrequencies { get; private set; } = null!;

    /// <summary>N-gram site summaries.</summary>
    public SqlRepository<NgramSiteSummaryDto> NgramSiteSummaries { get; private set; } = null!;

    /// <summary>Content artifact index.</summary>
    public SqlRepository<ContentArtifactDto> Artifacts { get; private set; } = null!;

    /// <summary>Automation action log rows.</summary>
    public SqlRepository<AutomationActionLogDto> ActionLog { get; private set; } = null!;

    /// <summary>Accepted n-gram read model.</summary>
    public NGramReadModel Ngrams { get; private set; } = null!;

    /// <summary>Accepted GEO readiness analyzer.</summary>
    public GeoReadinessAnalyzer Geo { get; private set; } = null!;

    /// <summary>Accepted log correlation engine.</summary>
    public LogCorrelationEngine LogCorrelation { get; private set; } = null!;

    /// <summary>Accepted comparison reader.</summary>
    public AuditComparisonReader Comparison { get; private set; } = null!;

    /// <summary>Test lifecycle port.</summary>
    public FakeAuditLifecyclePort Lifecycle { get; private set; } = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cf-wp-05-04-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(_directory, "test.db"),
        });
        await new SchemaMigrator(Provider).MigrateAsync();
        ContentStore = new CompressedContentStore(Path.Combine(_directory, "content"), Provider);
        Protector = new AppEntropySecretProtector(new EntropyKeyStore(Path.Combine(_directory, "entropy.key")));
        Projects = new SqlRepository<ProjectDto>(Provider);
        Configurations = new SqlRepository<CrawlConfigurationDto>(Provider);
        Audits = new SqlRepository<AuditDto>(Provider);
        Findings = new SqlRepository<FindingDto>(Provider);
        Urls = new SqlRepository<UrlRecordDto>(Provider);
        Metrics = new SqlRepository<MetricSummaryDto>(Provider);
        Scores = new SqlRepository<AuditScoreDto>(Provider);
        Schedules = new SqlRepository<ScheduleDefinitionDto>(Provider);
        ScheduleRuns = new SqlRepository<ScheduleRunDto>(Provider);
        WafDetections = new SqlRepository<WafDetectionDto>(Provider);
        ThrottleEvents = new SqlRepository<HostThrottleEventDto>(Provider);
        RedirectMappings = new SqlRepository<RedirectMappingDto>(Provider);
        LogImports = new SqlRepository<LogImportDto>(Provider);
        LogAggregates = new SqlRepository<LogUrlAggregateDto>(Provider);
        Opportunities = new SqlRepository<SemanticLinkOpportunityDto>(Provider);
        Clusters = new SqlRepository<SemanticClusterDto>(Provider);
        AnalyticsDetections = new SqlRepository<AnalyticsDetectionDto>(Provider);
        ExtractedContent = new SqlRepository<ExtractedContentDto>(Provider);
        GovernanceRows = new SqlRepository<GovernanceMatrixRowDto>(Provider);
        NgramFrequencies = new SqlRepository<NgramFrequencyDto>(Provider);
        NgramSiteSummaries = new SqlRepository<NgramSiteSummaryDto>(Provider);
        Artifacts = new SqlRepository<ContentArtifactDto>(Provider);
        ActionLog = new SqlRepository<AutomationActionLogDto>(Provider);
        Ngrams = new NGramReadModel(Provider);
        Geo = new GeoReadinessAnalyzer(Provider, ContentStore, new AngleSharpDomParser());
        LogCorrelation = new LogCorrelationEngine(Provider);
        Comparison = new AuditComparisonReader(Provider);
        Lifecycle = new FakeAuditLifecyclePort();
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        Provider.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; WAL handles may linger briefly on Windows.
        }

        return Task.CompletedTask;
    }

    /// <summary>Builds the resource ports over the real stack.</summary>
    /// <returns>Wired resource ports.</returns>
    public McpResourcePorts BuildPorts() => new(
        Audits, Findings, Urls, Metrics, Scores, Schedules, ScheduleRuns,
        WafDetections, ThrottleEvents, RedirectMappings, LogImports, LogAggregates,
        Opportunities, Clusters, AnalyticsDetections, ExtractedContent, GovernanceRows,
        NgramFrequencies, Artifacts, ContentStore, Ngrams, Geo, LogCorrelation, Lifecycle);

    /// <summary>Creates the project → configuration → audit chain.</summary>
    /// <returns>Audit, project, and configuration identifiers.</returns>
    public async Task<(long AuditId, long ProjectId, long ConfigurationId)> CreateAuditAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var project = await Projects.CreateAsync(new ProjectDto(
            0, "mcp-tests", null, null, now, now, null, false));
        var configuration = await Configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "cfg", "[\"https://example.test/\"]", null, null, null,
            RenderMode.None, null, null, null, null, null, now, now));
        var audit = await Audits.CreateAsync(new AuditDto(
            0, project.Id, configuration.Id, "{}", AuditStatus.Completed, AuditPhase.Finished,
            now, now, "{}", "tests", false));
        return (audit.Id, project.Id, configuration.Id);
    }

    /// <summary>Creates an indexable success URL record.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="path">Unique URL path.</param>
    /// <returns>The URL record identifier.</returns>
    public async Task<long> CreateUrlAsync(long auditId, string path)
    {
        var url = await Urls.CreateAsync(new UrlRecordDto(
            0, auditId, "https://example.test" + path, "example.test", path, 0,
            DiscoverySource.Seed, CrawlStatusClass.Success, 200, null, "text/html",
            1024, null, RobotsState.Allowed, IndexabilityFlags.None, true));
        return url.Id;
    }

    /// <summary>Stores an HTML body artifact through the real content store.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="urlId">Owning URL.</param>
    /// <param name="html">HTML body.</param>
    /// <returns>The artifact row.</returns>
    public async Task<ContentArtifactDto> StoreBodyAsync(long auditId, long urlId, string html)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
        return await ContentStore.PutAsync(auditId, urlId, ContentArtifactKind.Body, stream);
    }
}
