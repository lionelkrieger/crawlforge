using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Scheduling;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Crawler.Tests.Scheduling;

/// <summary>
/// One temporary embedded database directory. Cleanup clears idle pooled handles
/// before deleting so Windows file locks never leak between tests.
/// </summary>
internal sealed class SchedulingDatabase : IDisposable
{
    private readonly string _directory;

    public SchedulingDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "crawlforge-scheduling-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "scheduling.db");
        Options = new SqliteProviderOptions { DatabasePath = DatabasePath };
    }

    public string DatabasePath { get; }

    public SqliteProviderOptions Options { get; }

    public SqliteDbProvider CreateProvider() => new(Options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the test scratch directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of the test scratch directory.
        }
    }
}

/// <summary>
/// The real persistence stack for scheduling tests: SqliteDbProvider + SchemaMigrator
/// + generic repositories, with one seeded project and configuration to satisfy the
/// schedule foreign keys. No external service is involved.
/// </summary>
internal sealed class SchedulingStack : IAsyncDisposable
{
    private SchedulingStack(SqliteDbProvider provider)
    {
        Provider = provider;
        Schedules = new SqlRepository<ScheduleDefinitionDto>(provider);
        Runs = new SqlRepository<ScheduleRunDto>(provider);
        Audits = new SqlRepository<AuditDto>(provider);
    }

    public SqliteDbProvider Provider { get; }

    public SqlRepository<ScheduleDefinitionDto> Schedules { get; }

    public SqlRepository<ScheduleRunDto> Runs { get; }

    public SqlRepository<AuditDto> Audits { get; }

    public long ProjectId { get; private set; }

    public long ConfigurationId { get; private set; }

    public static async Task<SchedulingStack> CreateAsync(SchedulingDatabase database)
    {
        var provider = database.CreateProvider();
        try
        {
            var migrator = new SchemaMigrator(provider);
            await migrator.MigrateAsync();

            var stack = new SchedulingStack(provider);
            var now = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

            var projects = new SqlRepository<ProjectDto>(provider);
            var project = await projects.CreateAsync(new ProjectDto(0, "Scheduling tests", null, null, now, now, null, false));
            stack.ProjectId = project.Id;

            var configurations = new SqlRepository<CrawlConfigurationDto>(provider);
            var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
                0, project.Id, "config", "[\"https://example.com/\"]", null, null, null, RenderMode.None, null, null, null, null, null, now, now));
            stack.ConfigurationId = configuration.Id;

            return stack;
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    public async Task<AuditDto> CreateAuditAsync(AuditStatus status = AuditStatus.Completed, DateTimeOffset? finishedAt = null)
    {
        var startedAt = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);
        return await Audits.CreateAsync(new AuditDto(
            0, ProjectId, ConfigurationId, "{}", status, AuditPhase.Finished, startedAt, finishedAt ?? startedAt.AddMinutes(5), "{}", "test-engine", false));
    }

    public ValueTask DisposeAsync()
    {
        Provider.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Controllable scheduler clock: tests advance time explicitly, so no test ever
/// waits on real time (criterion #12 evidence).
/// </summary>
internal sealed class FakeSchedulerClock : ISchedulerClock
{
    public FakeSchedulerClock(DateTimeOffset start)
    {
        UtcNow = start;
    }

    public DateTimeOffset UtcNow { get; set; }

    public TimeZoneInfo LocalTimeZone { get; set; } = TimeZoneInfo.Utc;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
