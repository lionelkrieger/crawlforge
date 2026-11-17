using System.Text.Json;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Audit.Tests.StructuredData.Support;

/// <summary>
/// Real-stack fixture: migrated SQLite database (production SchemaMigrator),
/// compressed content store, and a seeded project/configuration/audit/URL
/// chain. Used by the persistence and offline re-analysis suites.
/// </summary>
public sealed class StructuredDataRealStack : IAsyncDisposable
{
    private StructuredDataRealStack(string root, SqliteDbProvider provider, CompressedContentStore store)
    {
        RootDirectory = root;
        Provider = provider;
        ContentStore = store;
    }

    /// <summary>Temporary root owned by this fixture.</summary>
    public string RootDirectory { get; }

    /// <summary>The real SQLite provider.</summary>
    public SqliteDbProvider Provider { get; }

    /// <summary>The real compressed content store.</summary>
    public CompressedContentStore ContentStore { get; }

    /// <summary>Seeded project identifier.</summary>
    public long ProjectId { get; private set; }

    /// <summary>Seeded configuration identifier.</summary>
    public long ConfigurationId { get; private set; }

    /// <summary>Seeded audit identifier.</summary>
    public long AuditId { get; private set; }

    /// <summary>Seeded URL identifier.</summary>
    public long UrlId { get; private set; }

    /// <summary>Bundled technology catalogue path (resolved from the repo root).</summary>
    public static string CatalogueAssetPath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CrawlForge.sln")))
                {
                    return Path.Combine(directory.FullName, "assets", "tech-fingerprints", "tech-catalogue.json");
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate CrawlForge.sln above the test base directory.");
        }
    }

    /// <summary>Creates the migrated, seeded fixture.</summary>
    /// <returns>The fixture; dispose to delete all files.</returns>
    public static async Task<StructuredDataRealStack> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-wp-03-05-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(root, "crawlforge.db"),
        });
        await new SchemaMigrator(provider).MigrateAsync();
        var store = new CompressedContentStore(Path.Combine(root, "content"), provider);
        var fixture = new StructuredDataRealStack(root, provider, store);
        await fixture.SeedAsync();
        return fixture;
    }

    /// <summary>Stores a body artifact for the seeded URL.</summary>
    /// <param name="html">Body markup.</param>
    /// <returns>The stored artifact.</returns>
    public async Task<ContentArtifactDto> StoreBodyAsync(string html)
    {
        return await ContentStore.PutAsync(
            AuditId, UrlId, ContentArtifactKind.Body, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)));
    }

    private async Task SeedAsync()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var project = await new SqlRepository<ProjectDto>(Provider).CreateAsync(
            new ProjectDto(0, "sd-test", null, null, timestamp, timestamp, null, false));
        ProjectId = project.Id;

        var configuration = await new SqlRepository<CrawlConfigurationDto>(Provider).CreateAsync(
            new CrawlConfigurationDto(0, ProjectId, "sd-config", "[\"https://example.test/\"]",
                null, null, null, RenderMode.None, null, null, null, null, null, timestamp, timestamp));
        ConfigurationId = configuration.Id;

        var snapshot = JsonSerializer.Serialize(configuration);
        var audit = await new SqlRepository<AuditDto>(Provider).CreateAsync(
            new AuditDto(0, ProjectId, ConfigurationId, snapshot, AuditStatus.Running, AuditPhase.Analyzing,
                timestamp, null, "{}", "test", false));
        AuditId = audit.Id;

        var url = await new SqlRepository<UrlRecordDto>(Provider).CreateAsync(
            new UrlRecordDto(0, AuditId, "https://example.test/page", "example.test", "/page", 0,
                DiscoverySource.Seed, CrawlStatusClass.Success, 200, null, "text/html", 0, null,
                RobotsState.Allowed, IndexabilityFlags.None, true));
        UrlId = url.Id;
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
