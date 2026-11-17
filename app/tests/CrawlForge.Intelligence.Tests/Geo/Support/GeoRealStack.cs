using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Intelligence.Tests.Geo.Support;

/// <summary>
/// Real-stack GEO fixture: migrated SQLite database (production SchemaMigrator)
/// plus the real compressed content store, with helpers for URLs, stored
/// bodies, element-inventory rows, and findings. All GEO analysis must work
/// from these stored artifacts alone (offline, zero fetch).
/// </summary>
public sealed class GeoRealStack : IAsyncDisposable
{
    private GeoRealStack(string root, SqliteDbProvider provider, CompressedContentStore store)
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

    /// <summary>Bundled agent-profile asset path (resolved from the repo root).</summary>
    public static string AgentProfileAssetPath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CrawlForge.sln")))
                {
                    return Path.Combine(directory.FullName, "assets", "ai-agent-profiles", "ai-agent-profiles.json");
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate CrawlForge.sln above the test base directory.");
        }
    }

    /// <summary>Creates the migrated, seeded fixture.</summary>
    /// <returns>The fixture; dispose to delete all files.</returns>
    public static async Task<GeoRealStack> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-wp-04-03-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(root, "crawlforge.db"),
        });
        await new SchemaMigrator(provider).MigrateAsync();
        var store = new CompressedContentStore(Path.Combine(root, "content"), provider);
        var fixture = new GeoRealStack(root, provider, store);
        await fixture.SeedAsync();
        return fixture;
    }

    /// <summary>Adds a URL record.</summary>
    /// <param name="canonicalUrl">Canonical URL (unique per audit).</param>
    /// <param name="statusCode">Final HTTP status code.</param>
    /// <param name="isIndexable">Indexability flag.</param>
    /// <param name="contentType">Content type, when known.</param>
    /// <param name="auditId">Owning audit (defaults to the seeded audit).</param>
    /// <returns>The persisted URL record.</returns>
    public async Task<UrlRecordDto> AddUrlAsync(
        string canonicalUrl,
        int statusCode = 200,
        bool isIndexable = true,
        string? contentType = "text/html",
        long? auditId = null)
    {
        var uri = new Uri(canonicalUrl);
        var statusClass = statusCode is >= 200 and < 300 ? CrawlStatusClass.Success
            : statusCode is >= 300 and < 400 ? CrawlStatusClass.Redirect
            : statusCode is >= 400 and < 500 ? CrawlStatusClass.ClientError
            : CrawlStatusClass.ServerError;
        return await new SqlRepository<UrlRecordDto>(Provider).CreateAsync(new UrlRecordDto(
            0, auditId ?? AuditId, canonicalUrl, uri.Host, uri.AbsolutePath, 0,
            DiscoverySource.Seed, statusClass, statusCode, null, contentType, 0, null,
            RobotsState.Allowed,
            isIndexable ? IndexabilityFlags.None : IndexabilityFlags.Noindex,
            isIndexable));
    }

    /// <summary>Stores a body artifact for a URL.</summary>
    /// <param name="urlId">URL record identifier.</param>
    /// <param name="body">Body text (UTF-8).</param>
    /// <param name="auditId">Owning audit (defaults to the seeded audit).</param>
    /// <returns>The stored artifact.</returns>
    public async Task<ContentArtifactDto> StoreBodyAsync(long urlId, string body, long? auditId = null)
    {
        return await ContentStore.PutAsync(
            auditId ?? AuditId, urlId, ContentArtifactKind.Body,
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)));
    }

    /// <summary>Inserts an element-inventory row (keystone payload shape).</summary>
    /// <param name="urlId">URL record identifier.</param>
    /// <param name="elementGroup">Element group key.</param>
    /// <param name="payloadJson">Payload JSON.</param>
    /// <returns>A task representing the insert.</returns>
    public async Task AddElementAsync(long urlId, string elementGroup, string payloadJson)
    {
        await using var connection = await Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO url_elements (url_id, element_group, payload_json, ordinal) VALUES (@urlId, @group, @payload, 0)";
        var urlParameter = command.CreateParameter();
        urlParameter.ParameterName = "@urlId";
        urlParameter.Value = urlId;
        command.Parameters.Add(urlParameter);
        var groupParameter = command.CreateParameter();
        groupParameter.ParameterName = "@group";
        groupParameter.Value = elementGroup;
        command.Parameters.Add(groupParameter);
        var payloadParameter = command.CreateParameter();
        payloadParameter.ParameterName = "@payload";
        payloadParameter.Value = payloadJson;
        command.Parameters.Add(payloadParameter);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Adds a finding.</summary>
    /// <param name="ruleKey">Stable rule key.</param>
    /// <param name="category">Taxonomy category.</param>
    /// <param name="severity">Finding severity.</param>
    /// <param name="urlId">Optional URL reference.</param>
    /// <returns>The persisted finding.</returns>
    public async Task<FindingDto> AddFindingAsync(
        string ruleKey, AuditCategory category, FindingSeverity severity, long? urlId = null)
    {
        return await new SqlRepository<FindingDto>(Provider).CreateAsync(new FindingDto(
            0, AuditId, ruleKey, category, severity, urlId, "{}", null, false));
    }

    /// <summary>Adds a second audit to the same project.</summary>
    /// <returns>The new audit identifier.</returns>
    public async Task<long> AddAuditAsync()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var audit = await new SqlRepository<AuditDto>(Provider).CreateAsync(new AuditDto(
            0, ProjectId, ConfigurationId, "{}", AuditStatus.Completed, AuditPhase.Finished,
            timestamp, timestamp, "{}", "test", false));
        return audit.Id;
    }

    private async Task SeedAsync()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var project = await new SqlRepository<ProjectDto>(Provider).CreateAsync(
            new ProjectDto(0, "geo-test", null, null, timestamp, timestamp, null, false));
        ProjectId = project.Id;

        var configuration = await new SqlRepository<CrawlConfigurationDto>(Provider).CreateAsync(
            new CrawlConfigurationDto(0, ProjectId, "geo-config", "[\"https://example.test/\"]",
                null, null, null, RenderMode.None, null, null, null, null, null, timestamp, timestamp));
        ConfigurationId = configuration.Id;

        var audit = await new SqlRepository<AuditDto>(Provider).CreateAsync(new AuditDto(
            0, ProjectId, ConfigurationId, "{}", AuditStatus.Completed, AuditPhase.Finished,
            timestamp, timestamp, "{}", "test", false));
        AuditId = audit.Id;
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
