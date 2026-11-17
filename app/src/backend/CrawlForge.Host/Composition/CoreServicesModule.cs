using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Contracts.Security;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using CrawlForge.Host.Configuration;
using CrawlForge.Host.Hosting;
using CrawlForge.Security.Diagnostics;
using CrawlForge.Security.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CrawlForge.Host.Composition;

/// <summary>
/// Core services module (WP-06-01): persistence provider + migrator, all
/// repositories, secret protection, local diagnostics, and the content store.
/// Everything is local-first: embedded SQLite by default, artifacts on the
/// local filesystem, secrets protected with locally generated app entropy
/// (never machine identity — clean-room guardrail #1).
/// </summary>
public sealed class CoreServicesModule : IHostModule
{
    /// <inheritdoc />
    public string Name => "core-services";

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var paths = provider.GetRequiredService<CrawlForgePaths>();
            return new SqliteDbProvider(new SqliteProviderOptions
            {
                DatabasePath = Path.Combine(paths.DataDirectory, "crawlforge.db")
            });
        });
        services.AddSingleton<IDbProvider>(provider => provider.GetRequiredService<SqliteDbProvider>());
        services.AddSingleton<ISchemaMigrator>(provider =>
            new SchemaMigrator(provider.GetRequiredService<IDbProvider>()));

        RegisterRepositories(services);

        // Secret protection: locally generated entropy key, never OS/machine stores.
        services.AddSingleton(provider =>
        {
            var paths = provider.GetRequiredService<CrawlForgePaths>();
            return new EntropyKeyStore(Path.Combine(paths.DataDirectory, "entropy.key"));
        });
        services.AddSingleton<ISecretProtector>(provider =>
            new AppEntropySecretProtector(provider.GetRequiredService<EntropyKeyStore>()));

        // Local diagnostics: bounded, user-viewable, purgeable artifacts.
        services.AddSingleton(provider =>
        {
            var paths = provider.GetRequiredService<CrawlForgePaths>();
            return new DiagnosticsOptions(paths.DiagnosticsDirectory);
        });
        services.AddSingleton(provider =>
            new DiagnosticArtifactRegistry(provider.GetRequiredService<DiagnosticsOptions>()));
        services.AddSingleton<ICrashDumpHandler>(provider =>
            new MinidumpCrashHandler(provider.GetRequiredService<DiagnosticArtifactRegistry>()));

        // Local structured logging (WP-06-01 reopen): the accepted sink writes
        // identity-filtered JSON lines into the diagnostics directory; the
        // provider bridge forwards framework logs without changing the
        // existing console behavior.
        services.AddSingleton(provider =>
            new LocalStructuredLogSink(provider.GetRequiredService<DiagnosticArtifactRegistry>()));
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(provider =>
            new LocalStructuredLogProvider(provider.GetRequiredService<LocalStructuredLogSink>()));

        // Compressed content store over the local filesystem + index rows.
        services.AddSingleton(provider =>
        {
            var paths = provider.GetRequiredService<CrawlForgePaths>();
            return new CompressedContentStore(
                Path.Combine(paths.DataDirectory, "content-store"),
                provider.GetRequiredService<IDbProvider>());
        });
        services.AddSingleton<IContentStore>(provider => provider.GetRequiredService<CompressedContentStore>());
        services.AddSingleton<IArtifactFileStore>(provider => provider.GetRequiredService<CompressedContentStore>());
    }

    private static void RegisterRepositories(IServiceCollection services)
    {
        AddRepository<ProjectDto>(services);
        AddRepository<CrawlConfigurationDto>(services);
        AddRepository<AuditDto>(services);
        AddRepository<UrlRecordDto>(services);
        AddRepository<LinkEdgeDto>(services);
        AddRepository<FindingDto>(services);
        AddRepository<FindingSuppressionDto>(services);
        AddRepository<UrlElementDto>(services);
        AddRepository<MetricSummaryDto>(services);
        AddRepository<AuditScoreDto>(services);
        AddRepository<ContentArtifactDto>(services);
        AddRepository<CrawlCheckpointDto>(services);
        AddRepository<ScheduleDefinitionDto>(services);
        AddRepository<ScheduleRunDto>(services);
        AddRepository<ExportDestinationDto>(services);
        AddRepository<ApplicationSettingsDto>(services);
        AddRepository<UserDictionaryEntryDto>(services);
        AddRepository<UserStopwordDto>(services);
        AddRepository<SavedViewDto>(services);
        AddRepository<UiPluginDto>(services);
        AddRepository<RedirectMappingDto>(services);
        AddRepository<HostThrottleEventDto>(services);
        AddRepository<WafDetectionDto>(services);
        AddRepository<WafUserSignatureDto>(services);
        AddRepository<TechnologyDetectionDto>(services);
        AddRepository<AnalyticsDetectionDto>(services);
        AddRepository<ExtractedContentDto>(services);
        // Custom-extraction explorer surfaces (WP-06-01 reopen, defect 6):
        // ContentExplorerController consumes both read repositories.
        AddRepository<ContentSearchResultDto>(services);
        AddRepository<SnippetResultDto>(services);
        AddRepository<ContentScopeProfileDto>(services);
        AddRepository<NgramFrequencyDto>(services);
        AddRepository<NgramSiteSummaryDto>(services);
        AddRepository<UrlEmbeddingDto>(services);
        AddRepository<SemanticLinkOpportunityDto>(services);
        AddRepository<SemanticClusterDto>(services);
        AddRepository<LogImportDto>(services);
        AddRepository<LogUrlAggregateDto>(services);
        AddRepository<GovernanceMatrixRowDto>(services);
        AddRepository<AutomationActionLogDto>(services);
    }

    private static void AddRepository<TDto>(IServiceCollection services)
        where TDto : class
    {
        services.AddSingleton<IReadRepository<TDto>>(provider =>
            new SqlRepository<TDto>(provider.GetRequiredService<IDbProvider>()));
        services.AddSingleton<IWriteRepository<TDto>>(provider =>
            new SqlRepository<TDto>(provider.GetRequiredService<IDbProvider>()));
    }
}
