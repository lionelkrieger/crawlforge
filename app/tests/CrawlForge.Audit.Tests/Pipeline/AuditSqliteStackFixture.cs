using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using Xunit;

namespace CrawlForge.Audit.Tests.Pipeline;

/// <summary>
/// Real completed persistence stack for integration-faithful pipeline tests:
/// temp embedded SQLite, the full migration catalog, and the locked generic
/// repositories (mapping is catalog-authoritative after the phase-01 fix).
/// </summary>
public sealed class AuditSqliteStackFixture : IAsyncLifetime
{
    private string _directory = null!;

    public SqliteDbProvider Provider { get; private set; } = null!;

    public SqlRepository<FindingDto> Findings { get; private set; } = null!;

    public SqlRepository<FindingSuppressionDto> Suppressions { get; private set; } = null!;

    public SqlRepository<ContentScopeProfileDto> Profiles { get; private set; } = null!;

    public SqlRepository<UrlElementDto> UrlElements { get; private set; } = null!;

    public SqlRepository<MetricSummaryDto> Metrics { get; private set; } = null!;

    public SqlRepository<ProjectDto> Projects { get; private set; } = null!;

    public SqlRepository<CrawlConfigurationDto> Configurations { get; private set; } = null!;

    public SqlRepository<AuditDto> Audits { get; private set; } = null!;

    public SqlRepository<UrlRecordDto> Urls { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cf-wp-03-01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(_directory, "test.db"),
        });
        await new SchemaMigrator(Provider).MigrateAsync();
        Findings = new SqlRepository<FindingDto>(Provider);
        Suppressions = new SqlRepository<FindingSuppressionDto>(Provider);
        Profiles = new SqlRepository<ContentScopeProfileDto>(Provider);
        UrlElements = new SqlRepository<UrlElementDto>(Provider);
        Metrics = new SqlRepository<MetricSummaryDto>(Provider);
        Projects = new SqlRepository<ProjectDto>(Provider);
        Configurations = new SqlRepository<CrawlConfigurationDto>(Provider);
        Audits = new SqlRepository<AuditDto>(Provider);
        Urls = new SqlRepository<UrlRecordDto>(Provider);
    }

    /// <summary>Creates a stored URL record under the audit (findings FK target).</summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="path">URL path (unique per audit via the canonical-url constraint).</param>
    /// <returns>The new URL record identifier.</returns>
    public async Task<long> CreateUrlAsync(long auditId, string path = "/page")
    {
        var url = await Urls.CreateAsync(new UrlRecordDto(
            Id: 0,
            AuditId: auditId,
            CanonicalUrl: "https://example.test" + path,
            Host: "example.test",
            Path: path,
            Depth: 0,
            DiscoverySource: DiscoverySource.Seed,
            StatusClass: CrawlStatusClass.Success,
            FinalStatusCode: 200,
            RedirectTargetUrl: null,
            ContentType: "text/html",
            SizeBytes: 1024,
            TimingJson: null,
            RobotsState: RobotsState.Allowed,
            IndexabilityFlags: IndexabilityFlags.None,
            IsIndexable: true));
        return url.Id;
    }

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

    /// <summary>Creates the project → configuration → audit chain via the real repositories.</summary>
    /// <returns>The new audit identifier plus its project and configuration ids.</returns>
    public async Task<(long AuditId, long ProjectId, long ConfigurationId)> CreateAuditAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var project = await Projects.CreateAsync(new ProjectDto(
            Id: 0,
            Name: "pipeline-tests",
            Description: null,
            TagsJson: null,
            CreatedAt: now,
            UpdatedAt: now,
            ScopeSummary: null,
            IsDeleted: false));
        var configuration = await Configurations.CreateAsync(new CrawlConfigurationDto(
            Id: 0,
            ProjectId: project.Id,
            Name: "cfg",
            SeedsJson: "[\"https://example.test/\"]",
            ScopeRulesJson: null,
            Depth: null,
            LimitOverrides: null,
            RenderingMode: RenderMode.None,
            PolitenessJson: null,
            ProxyRoutingJson: null,
            CustomExtractionRulesJson: null,
            HeadersJson: null,
            CookiesJson: null,
            CreatedAt: now,
            UpdatedAt: now));
        var audit = await Audits.CreateAsync(new AuditDto(
            Id: 0,
            ProjectId: project.Id,
            ConfigurationId: configuration.Id,
            ConfigurationSnapshotJson: "{}",
            Status: AuditStatus.Queued,
            Phase: AuditPhase.NotStarted,
            StartedAt: null,
            FinishedAt: null,
            CountersJson: "{}",
            EngineVersion: "tests",
            IsDeleted: false));
        return (audit.Id, project.Id, configuration.Id);
    }
}
