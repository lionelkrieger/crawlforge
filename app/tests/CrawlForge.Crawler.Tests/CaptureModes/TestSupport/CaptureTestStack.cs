using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using Xunit;

namespace CrawlForge.Crawler.Tests.CaptureModes.TestSupport;

/// <summary>
/// Real completed persistence stack for capture-mode component tests: a temp
/// embedded SQLite database (schema v5 incl. url_records.capture_source), the
/// locked generic repositories, and the real Brotli+SHA-256 content store. No
/// mocks of the persistence layer — the coordinator stamps CaptureSource and
/// persists rendered artifacts through the production surfaces.
/// </summary>
public sealed class CaptureTestStack : IAsyncLifetime
{
    private string _directory = null!;

    public SqliteDbProvider Provider { get; private set; } = null!;

    public CompressedContentStore ContentStore { get; private set; } = null!;

    public SqlRepository<UrlRecordDto> Urls { get; private set; } = null!;

    public SqlRepository<ContentArtifactDto> Artifacts { get; private set; } = null!;

    public SqlRepository<CrawlConfigurationDto> Configurations { get; private set; } = null!;

    public long AuditId { get; private set; }

    public long ProjectId { get; private set; }

    public long ConfigurationId { get; private set; }

    public string RootDirectory => _directory;

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cf-wp-10-02-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(_directory, "test.db"),
        });
        await new SchemaMigrator(Provider).MigrateAsync();
        ContentStore = new CompressedContentStore(Path.Combine(_directory, "artifacts"), Provider);
        Urls = new SqlRepository<UrlRecordDto>(Provider);
        Artifacts = new SqlRepository<ContentArtifactDto>(Provider);
        Configurations = new SqlRepository<CrawlConfigurationDto>(Provider);

        var now = DateTimeOffset.UtcNow;
        var projects = new SqlRepository<ProjectDto>(Provider);
        var project = await projects.CreateAsync(new ProjectDto(
            Id: 0, Name: "capture-tests", Description: null, TagsJson: null,
            CreatedAt: now, UpdatedAt: now, ScopeSummary: null, IsDeleted: false));
        ProjectId = project.Id;
        var configuration = await Configurations.CreateAsync(NewConfiguration(project.Id, CaptureMode.NonJs, null));
        ConfigurationId = configuration.Id;
        var audits = new SqlRepository<AuditDto>(Provider);
        var audit = await audits.CreateAsync(new AuditDto(
            Id: 0, ProjectId: project.Id, ConfigurationId: configuration.Id,
            ConfigurationSnapshotJson: "{}", Status: AuditStatus.Queued, Phase: AuditPhase.NotStarted,
            StartedAt: null, FinishedAt: null, CountersJson: "{}", EngineVersion: "tests", IsDeleted: false));
        AuditId = audit.Id;
    }

    public CrawlConfigurationDto NewConfiguration(long projectId, CaptureMode? mode, CaptureSettings? settings) =>
        new(
            Id: 0, ProjectId: projectId, Name: "cfg",
            SeedsJson: "[\"https://fixture.test/\"]", ScopeRulesJson: null, Depth: null, LimitOverrides: null,
            RenderingMode: RenderMode.None, PolitenessJson: null, ProxyRoutingJson: null,
            CustomExtractionRulesJson: null, HeadersJson: null, CookiesJson: null,
            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow)
        {
            CaptureMode = mode,
            CaptureSettings = settings,
        };

    public async Task<UrlRecordDto> CreateUrlAsync(string path, DiscoverySource discovery = DiscoverySource.Link)
    {
        var url = await Urls.CreateAsync(new UrlRecordDto(
            Id: 0, AuditId: AuditId, CanonicalUrl: "https://fixture.test" + path, Host: "fixture.test",
            Path: path, Depth: 1, DiscoverySource: discovery, StatusClass: CrawlStatusClass.Success,
            FinalStatusCode: 200, RedirectTargetUrl: null, ContentType: "text/html", SizeBytes: 1024,
            TimingJson: null, RobotsState: RobotsState.Allowed, IndexabilityFlags: IndexabilityFlags.None,
            IsIndexable: true));
        return url;
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
}
