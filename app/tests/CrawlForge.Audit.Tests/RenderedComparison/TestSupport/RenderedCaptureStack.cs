using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using Xunit;

namespace CrawlForge.Audit.Tests.RenderedComparison.TestSupport;

/// <summary>
/// Real persistence stack for the rendered-document conformance tests: temp
/// SQLite (schema v5), locked generic repositories, real content store. The
/// capture coordinator persists rendered artifacts here; the tests reload them
/// exactly as AuditExecutionEngine.BuildStoredContextAsync does.
/// </summary>
public sealed class RenderedCaptureStack : IAsyncLifetime
{
    private string _directory = null!;

    public SqliteDbProvider Provider { get; private set; } = null!;

    public CompressedContentStore ContentStore { get; private set; } = null!;

    public SqlRepository<UrlRecordDto> Urls { get; private set; } = null!;

    public SqlRepository<ContentArtifactDto> Artifacts { get; private set; } = null!;

    public SqlRepository<FindingDto> Findings { get; private set; } = null!;

    public long AuditId { get; private set; }

    public long ProjectId { get; private set; }

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cf-wp-10-02-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Provider = new SqliteDbProvider(new SqliteProviderOptions { DatabasePath = Path.Combine(_directory, "test.db") });
        await new SchemaMigrator(Provider).MigrateAsync();
        ContentStore = new CompressedContentStore(Path.Combine(_directory, "artifacts"), Provider);
        Urls = new SqlRepository<UrlRecordDto>(Provider);
        Artifacts = new SqlRepository<ContentArtifactDto>(Provider);
        Findings = new SqlRepository<FindingDto>(Provider);

        var now = DateTimeOffset.UtcNow;
        var projects = new SqlRepository<ProjectDto>(Provider);
        var project = await projects.CreateAsync(new ProjectDto(0, "rendered-tests", null, null, now, now, null, false));
        ProjectId = project.Id;
        var configurations = new SqlRepository<CrawlConfigurationDto>(Provider);
        var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "cfg", "[\"https://fixture.test/\"]", null, null, null, RenderMode.None, null, null, null, null, null, now, now));
        var audits = new SqlRepository<AuditDto>(Provider);
        var audit = await audits.CreateAsync(new AuditDto(
            0, project.Id, configuration.Id, "{}", AuditStatus.Queued, AuditPhase.NotStarted, null, null, "{}", "tests", false));
        AuditId = audit.Id;
    }

    public async Task<UrlRecordDto> CreateUrlAsync(string path) =>
        await Urls.CreateAsync(new UrlRecordDto(
            0, AuditId, FixturePages.BaseUrl + path, "fixture.test", path, 1, DiscoverySource.Link,
            CrawlStatusClass.Success, 200, null, "text/html", 1024, null, RobotsState.Allowed,
            IndexabilityFlags.None, true));

    public Task DisposeAsync()
    {
        Provider.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }

        return Task.CompletedTask;
    }
}
