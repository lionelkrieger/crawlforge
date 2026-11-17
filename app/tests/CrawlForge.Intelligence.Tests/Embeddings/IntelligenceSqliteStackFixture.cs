using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Real completed persistence stack for integration-faithful embeddings tests:
/// temp embedded SQLite, the full migration catalog, the locked generic
/// repositories, and the real compressed content store.
/// </summary>
public sealed class IntelligenceSqliteStackFixture : IAsyncLifetime
{
    private string _directory = null!;

    public SqliteDbProvider Provider { get; private set; } = null!;

    public CompressedContentStore ContentStore { get; private set; } = null!;

    public SqlRepository<ProjectDto> Projects { get; private set; } = null!;

    public SqlRepository<CrawlConfigurationDto> Configurations { get; private set; } = null!;

    public SqlRepository<AuditDto> Audits { get; private set; } = null!;

    public SqlRepository<UrlRecordDto> Urls { get; private set; } = null!;

    public SqlRepository<ContentArtifactDto> Artifacts { get; private set; } = null!;

    public SqlRepository<UrlEmbeddingDto> Embeddings { get; private set; } = null!;

    public SqlRepository<LinkEdgeDto> LinkEdges { get; private set; } = null!;

    public SqlRepository<NgramFrequencyDto> Ngrams { get; private set; } = null!;

    public SqlRepository<SemanticLinkOpportunityDto> Opportunities { get; private set; } = null!;

    public SqlRepository<SemanticClusterDto> Clusters { get; private set; } = null!;

    public SqlRepository<FindingDto> Findings { get; private set; } = null!;

    public SqlRepository<FindingSuppressionDto> Suppressions { get; private set; } = null!;

    public SqlRepository<MetricSummaryDto> Metrics { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cf-wp-04-02-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(_directory, "test.db"),
        });
        await new SchemaMigrator(Provider).MigrateAsync();
        ContentStore = new CompressedContentStore(Path.Combine(_directory, "content"), Provider);
        Projects = new SqlRepository<ProjectDto>(Provider);
        Configurations = new SqlRepository<CrawlConfigurationDto>(Provider);
        Audits = new SqlRepository<AuditDto>(Provider);
        Urls = new SqlRepository<UrlRecordDto>(Provider);
        Artifacts = new SqlRepository<ContentArtifactDto>(Provider);
        Embeddings = new SqlRepository<UrlEmbeddingDto>(Provider);
        LinkEdges = new SqlRepository<LinkEdgeDto>(Provider);
        Ngrams = new SqlRepository<NgramFrequencyDto>(Provider);
        Opportunities = new SqlRepository<SemanticLinkOpportunityDto>(Provider);
        Clusters = new SqlRepository<SemanticClusterDto>(Provider);
        Findings = new SqlRepository<FindingDto>(Provider);
        Suppressions = new SqlRepository<FindingSuppressionDto>(Provider);
        Metrics = new SqlRepository<MetricSummaryDto>(Provider);
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
    /// <returns>Audit, project, and configuration identifiers.</returns>
    public async Task<(long AuditId, long ProjectId, long ConfigurationId)> CreateAuditAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var project = await Projects.CreateAsync(new ProjectDto(
            0, "embeddings-tests", null, null, now, now, null, false));
        var configuration = await Configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "cfg", "[\"https://example.test/\"]", null, null, null,
            RenderMode.None, null, null, null, null, null, now, now));
        var audit = await Audits.CreateAsync(new AuditDto(
            0, project.Id, configuration.Id, "{}", AuditStatus.Queued, AuditPhase.NotStarted,
            null, null, "{}", "tests", false));
        return (audit.Id, project.Id, configuration.Id);
    }

    /// <summary>Creates an indexable success URL record.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="path">Unique URL path.</param>
    /// <param name="indexable">Indexability flag.</param>
    /// <returns>The URL record identifier.</returns>
    public async Task<long> CreateUrlAsync(long auditId, string path, bool indexable = true)
    {
        var url = await Urls.CreateAsync(new UrlRecordDto(
            0, auditId, "https://example.test" + path, "example.test", path, 0,
            DiscoverySource.Seed, CrawlStatusClass.Success, 200, null, "text/html",
            1024, null, RobotsState.Allowed, IndexabilityFlags.None, indexable));
        return url.Id;
    }

    /// <summary>Stores an HTML body artifact through the real content store.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="urlId">Owning URL.</param>
    /// <param name="html">Body content.</param>
    /// <returns>The artifact index row (with content hash).</returns>
    public async Task<ContentArtifactDto> StoreBodyAsync(long auditId, long urlId, string html)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
        return await ContentStore.PutAsync(auditId, urlId, ContentArtifactKind.Body, stream);
    }

    /// <summary>Creates an internal link edge between stored URLs.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="sourceUrlId">Source URL.</param>
    /// <param name="targetUrlId">Target URL.</param>
    /// <param name="anchorText">Anchor text.</param>
    /// <returns>The link edge identifier.</returns>
    public async Task<long> CreateLinkAsync(long auditId, long sourceUrlId, long targetUrlId, string anchorText)
    {
        var target = await Urls.GetByIdAsync(targetUrlId);
        var edge = await LinkEdges.CreateAsync(new LinkEdgeDto(
            0, auditId, sourceUrlId, target!.CanonicalUrl, targetUrlId, anchorText,
            LinkRelationFlags.None, true));
        return edge.Id;
    }

    /// <summary>Builds the semantic persistence port record over the real repositories.</summary>
    /// <returns>The persistence ports.</returns>
    public CrawlForge.Intelligence.Embeddings.SemanticPersistence BuildPersistence() => new(
        Urls, Artifacts, Embeddings, Embeddings, LinkEdges, Ngrams,
        Opportunities, Opportunities, Clusters, Clusters,
        Findings, Suppressions, Metrics, Metrics, Audits);

    /// <summary>Locates the bundled ONNX asset directory by walking up from the test binary.</summary>
    /// <returns>The asset directory containing model.onnx.</returns>
    public static string FindAssetDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "onnx-model");
            if (File.Exists(Path.Combine(candidate, "model.onnx")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Bundled onnx-model asset directory not found above the test binary.");
    }
}
