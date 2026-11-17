using System.Text.Json;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Audit.Tests.Scoring.Support;

/// <summary>
/// Real-stack scoring fixture: migrated SQLite database (production
/// SchemaMigrator) with a seeded project/configuration/audit chain and helpers
/// for URLs, findings, and additional audits. No content store is created —
/// scoring and comparison must work from stored rows alone (zero fetch).
/// </summary>
public sealed class ScoringRealStack : IAsyncDisposable
{
    private ScoringRealStack(string root, SqliteDbProvider provider)
    {
        RootDirectory = root;
        Provider = provider;
    }

    /// <summary>Temporary root owned by this fixture.</summary>
    public string RootDirectory { get; }

    /// <summary>The real SQLite provider.</summary>
    public SqliteDbProvider Provider { get; }

    /// <summary>Seeded project identifier.</summary>
    public long ProjectId { get; private set; }

    /// <summary>Seeded configuration identifier.</summary>
    public long ConfigurationId { get; private set; }

    /// <summary>Seeded (first) audit identifier.</summary>
    public long AuditId { get; private set; }

    /// <summary>Creates the migrated, seeded fixture with three indexable URLs.</summary>
    /// <returns>The fixture; dispose to delete all files.</returns>
    public static async Task<ScoringRealStack> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-wp-03-08-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(root, "crawlforge.db"),
        });
        await new SchemaMigrator(provider).MigrateAsync();
        var fixture = new ScoringRealStack(root, provider);
        await fixture.SeedAsync();
        return fixture;
    }

    /// <summary>Adds a URL record to an audit.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="canonicalUrl">Canonical URL (unique per audit).</param>
    /// <param name="isIndexable">Indexability coverage flag.</param>
    /// <returns>The persisted URL record.</returns>
    public async Task<UrlRecordDto> AddUrlAsync(long auditId, string canonicalUrl, bool isIndexable = true)
    {
        var uri = new Uri(canonicalUrl);
        return await new SqlRepository<UrlRecordDto>(Provider).CreateAsync(new UrlRecordDto(
            0, auditId, canonicalUrl, uri.Host, uri.AbsolutePath, 0,
            DiscoverySource.Seed, CrawlStatusClass.Success, 200, null, "text/html", 0, null,
            RobotsState.Allowed,
            isIndexable ? IndexabilityFlags.None : IndexabilityFlags.Noindex,
            isIndexable));
    }

    /// <summary>Adds a finding to an audit.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="ruleKey">Stable rule key.</param>
    /// <param name="category">Taxonomy category.</param>
    /// <param name="severity">Finding severity.</param>
    /// <param name="urlId">Optional URL reference.</param>
    /// <param name="isSuppressed">Initial suppression flag (emission-time state).</param>
    /// <returns>The persisted finding.</returns>
    public async Task<FindingDto> AddFindingAsync(
        long auditId,
        string ruleKey,
        AuditCategory category,
        FindingSeverity severity,
        long? urlId = null,
        bool isSuppressed = false)
    {
        return await new SqlRepository<FindingDto>(Provider).CreateAsync(new FindingDto(
            0, auditId, ruleKey, category, severity, urlId, "{}", null, isSuppressed));
    }

    /// <summary>Adds a second audit to the same project (comparison/re-audit paths).</summary>
    /// <param name="snapshotJson">Optional configuration snapshot override (enabledCategories convention).</param>
    /// <returns>The new audit identifier.</returns>
    public async Task<long> AddAuditAsync(string? snapshotJson = null)
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshot = snapshotJson ?? "{}";
        var audit = await new SqlRepository<AuditDto>(Provider).CreateAsync(new AuditDto(
            0, ProjectId, ConfigurationId, snapshot, AuditStatus.Completed, AuditPhase.Finished,
            timestamp, timestamp, "{}", "test", false));
        return audit.Id;
    }

    /// <summary>Replaces the first audit's configuration snapshot (convention tests).</summary>
    /// <param name="snapshotJson">Snapshot JSON payload.</param>
    /// <returns>A task representing the update.</returns>
    public async Task SetSnapshotAsync(string snapshotJson)
    {
        var repository = new SqlRepository<AuditDto>(Provider);
        var audit = await repository.GetByIdAsync(AuditId);
        await repository.UpdateAsync(audit! with { ConfigurationSnapshotJson = snapshotJson });
    }

    private async Task SeedAsync()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var project = await new SqlRepository<ProjectDto>(Provider).CreateAsync(
            new ProjectDto(0, "scoring-test", null, null, timestamp, timestamp, null, false));
        ProjectId = project.Id;

        var configuration = await new SqlRepository<CrawlConfigurationDto>(Provider).CreateAsync(
            new CrawlConfigurationDto(0, ProjectId, "scoring-config", "[\"https://example.test/\"]",
                null, null, null, RenderMode.None, null, null, null, null, null, timestamp, timestamp));
        ConfigurationId = configuration.Id;

        var audit = await new SqlRepository<AuditDto>(Provider).CreateAsync(new AuditDto(
            0, ProjectId, ConfigurationId, JsonSerializer.Serialize(configuration),
            AuditStatus.Completed, AuditPhase.Finished, timestamp, timestamp, "{}", "test", false));
        AuditId = audit.Id;

        await AddUrlAsync(AuditId, "https://example.test/one");
        await AddUrlAsync(AuditId, "https://example.test/two");
        await AddUrlAsync(AuditId, "https://example.test/three");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
        }
    }
}
