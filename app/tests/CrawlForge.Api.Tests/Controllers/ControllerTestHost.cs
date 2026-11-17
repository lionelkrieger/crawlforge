using CrawlForge.Api.Controllers;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoring;
using CrawlForge.Audit.Scoping;
using CrawlForge.Audit.StructuredData;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Contracts.Security;
using CrawlForge.Crawler.Canonicalization;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Crawler.Waf;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;
using CrawlForge.Intelligence.LogAnalyzer;
using CrawlForge.Intelligence.NGrams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-test composition root: a real TestServer over the real SQLite stack
/// (provider + migrator + repositories) with double engine services. Composed
/// entirely inside the Controllers test folder; CrawlForge.Host is the real
/// composition root in a later phase.
/// </summary>
internal sealed class ControllerTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _rootDirectory;

    private ControllerTestHost(WebApplication app, string rootDirectory, SqliteDbProvider provider, FakeServices fakes)
    {
        _app = app;
        _rootDirectory = rootDirectory;
        Provider = provider;
        Fakes = fakes;
        Services = app.Services;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services { get; }

    public SqliteDbProvider Provider { get; }

    public FakeServices Fakes { get; }

    public static async Task<ControllerTestHost> CreateAsync()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "crawlforge-api-controller-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        var provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(rootDirectory, "crawlforge.db"),
        });
        var migrator = new SchemaMigrator(provider);
        await migrator.MigrateAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = rootDirectory,
            EnvironmentName = "Testing",
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var fakes = new FakeServices();
        ConfigureServices(builder.Services, provider, migrator, fakes);

        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(ApiControllerBase).Assembly)
            .AddEnvelopeValidation()
            .AddJsonOptions(options =>
            {
                var source = ApiJsonOptions.Create();
                options.JsonSerializerOptions.PropertyNamingPolicy = source.PropertyNamingPolicy;
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = source.PropertyNameCaseInsensitive;
                options.JsonSerializerOptions.DefaultIgnoreCondition = source.DefaultIgnoreCondition;
                foreach (var converter in source.Converters)
                {
                    options.JsonSerializerOptions.Converters.Add(converter);
                }
            });

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        app.Services.GetRequiredService<IAuditExecutionService>();
        app.Services.GetRequiredService<IWafSignatureCatalogue>();
        app.Services.GetRequiredService<ILogImportService>();

        return new ControllerTestHost(app, rootDirectory, provider, fakes);
    }

    /// <summary>Resolves a registered service of the given type.</summary>
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    private static void ConfigureServices(IServiceCollection services, SqliteDbProvider provider, SchemaMigrator migrator, FakeServices fakes)
    {
        services.AddSingleton<IDbProvider>(provider);
        services.AddSingleton<ISchemaMigrator>(migrator);

        AddRepository<ProjectDto>(services);
        AddRepository<CrawlConfigurationDto>(services);
        AddRepository<AuditDto>(services);
        AddRepository<UrlRecordDto>(services);
        AddRepository<UrlElementDto>(services);
        AddRepository<LinkEdgeDto>(services);
        AddRepository<CrawlCheckpointDto>(services);
        AddRepository<ContentArtifactDto>(services);
        AddRepository<UrlEmbeddingDto>(services);
        AddRepository<FindingDto>(services);
        AddRepository<MetricSummaryDto>(services);
        AddRepository<ExtractedContentDto>(services);
        AddRepository<ContentSearchResultDto>(services);
        AddRepository<SnippetResultDto>(services);
        AddRepository<TechnologyDetectionDto>(services);
        AddRepository<AuditScoreDto>(services);
        AddRepository<FindingSuppressionDto>(services);
        AddRepository<ScheduleDefinitionDto>(services);
        AddRepository<ScheduleRunDto>(services);
        AddRepository<ExportDestinationDto>(services);
        AddRepository<UserDictionaryEntryDto>(services);
        AddRepository<AutomationActionLogDto>(services);
        AddRepository<ContentScopeProfileDto>(services);
        AddRepository<NgramFrequencyDto>(services);
        AddRepository<NgramSiteSummaryDto>(services);
        AddRepository<UserStopwordDto>(services);
        AddRepository<UiPluginDto>(services);
        AddRepository<RedirectMappingDto>(services);
        AddRepository<LogImportDto>(services);
        AddRepository<LogUrlAggregateDto>(services);
        AddRepository<SemanticLinkOpportunityDto>(services);
        AddRepository<SemanticClusterDto>(services);
        AddRepository<GovernanceMatrixRowDto>(services);
        AddRepository<AnalyticsDetectionDto>(services);
        AddRepository<WafDetectionDto>(services);
        AddRepository<WafUserSignatureDto>(services);
        AddRepository<HostThrottleEventDto>(services);
        AddRepository<ApplicationSettingsDto>(services);
        AddRepository<SavedViewDto>(services);

        // Real read models over the real provider (accepted surfaces).
        services.AddSingleton(new NGramReadModel(provider));
        services.AddSingleton(new AuditComparisonReader(provider));
        services.AddSingleton(new ScopePreviewService(new ContentScopeResolver()));
        services.AddSingleton<IUrlRewriteEngine>(new UrlRewriteEngine());
        services.AddSingleton<ITolerantDomParser>(new AngleSharpDomParser());
        services.AddSingleton<IRobotsTxtParser>(new RobotsTxtParser());
        services.AddSingleton<IStructuredDataValidator>(sp => new StructuredDataValidator(sp.GetRequiredService<ITolerantDomParser>()));
        services.AddSingleton<IHttpFetcher>(fakes.Fetcher);
        services.AddSingleton<IWafChallengeEvaluator>(sp =>
            WafChallengeEvaluator.CreateAsync(sp.GetRequiredService<IWafSignatureCatalogue>()).GetAwaiter().GetResult());
        services.AddSingleton<IFindingSuppressionService>(sp => new FindingSuppressionService(
            sp.GetRequiredService<IReadRepository<FindingSuppressionDto>>(),
            sp.GetRequiredService<IWriteRepository<FindingSuppressionDto>>()));

        // Engine/service doubles.
        services.AddSingleton<IAuditExecutionService>(sp =>
        {
            fakes.AuditExecution = new FakeAuditExecutionService(
                sp.GetRequiredService<IWriteRepository<AuditDto>>(),
                sp.GetRequiredService<IReadRepository<AuditDto>>());
            return fakes.AuditExecution;
        });
        services.AddSingleton<CrawlForge.Api.Controllers.IReAuditExecutionPort>(sp =>
            sp.GetRequiredService<IAuditExecutionService>() is FakeAuditExecutionService fake
                ? fake
                : throw new InvalidOperationException("The reaudit port must share the audit-execution double."));
        services.AddSingleton<IScheduledAuditService>(fakes.Scheduler);
        services.AddSingleton<IExportEngine>(fakes.Exports);
        services.AddSingleton<IExportDestinationDelivery>(fakes.Delivery);
        services.AddSingleton<ICrashDumpHandler>(fakes.Diagnostics);
        services.AddSingleton<IMetricsExposition>(fakes.Metrics);
        services.AddSingleton<ISinglePageAnalyzer>(fakes.SinglePage);
        services.AddSingleton<IRenderingPool>(fakes.Rendering);
        services.AddSingleton<IPreCrawlValidator>(fakes.UrlCheck);
        services.AddSingleton<IRedirectMatrixValidator>(fakes.RedirectMatrix);
        services.AddSingleton<ISemanticLinkAnalyzer>(fakes.Semantics);
        services.AddSingleton<ILogImportService>(sp =>
        {
            fakes.LogImports = new FakeLogImportService(
                sp.GetRequiredService<IReadRepository<LogImportDto>>(),
                sp.GetRequiredService<IWriteRepository<LogImportDto>>());
            return fakes.LogImports;
        });
        services.AddSingleton<ILogCorrelationReadModel>(fakes.LogCorrelation);
        services.AddSingleton<ILogDnsVerificationTool>(fakes.DnsVerification);
        services.AddSingleton<IGeoReadinessAnalyzer>(fakes.Geo);
        services.AddSingleton<IAiBotGovernanceMatrix>(fakes.Governance);
        services.AddSingleton<ILlmsTxtGenerator>(fakes.LlmsGenerator);
        services.AddSingleton<IWafSignatureCatalogue>(sp =>
        {
            fakes.WafCatalogue = new FakeWafSignatureCatalogue(
                sp.GetRequiredService<IReadRepository<WafUserSignatureDto>>(),
                sp.GetRequiredService<IWriteRepository<WafUserSignatureDto>>());
            return fakes.WafCatalogue;
        });
        services.AddSingleton<ISystemHealthService>(fakes.SystemHealth);
        services.AddSingleton<ICompactService>(fakes.Compact);
        services.AddSingleton<ISecretProtector>(fakes.Secrets);
        services.AddSingleton<IContentStore>(fakes.ContentStore);
        services.AddSingleton<IRealtimeHub>(fakes.Hub);
        services.AddSingleton<IAimdPolitenessController>(fakes.Aimd);
        services.AddSingleton<IListModeIngestor>(fakes.ListMode);
    }

    private static void AddRepository<TDto>(IServiceCollection services)
        where TDto : class
    {
        services.AddSingleton<IReadRepository<TDto>>(sp => new SqlRepository<TDto>(sp.GetRequiredService<IDbProvider>()));
        services.AddSingleton<IWriteRepository<TDto>>(sp => new SqlRepository<TDto>(sp.GetRequiredService<IDbProvider>()));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        Fakes.Exports.Dispose();
        Provider.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}

/// <summary>Mutable bag of service doubles shared with the test server.</summary>
internal sealed class FakeServices
{
    public FakeAuditExecutionService AuditExecution { get; set; } = null!;

    public FakeScheduledAuditService Scheduler { get; } = new();

    public FakeExportEngine Exports { get; } = new();

    public FakeDestinationDelivery Delivery { get; } = new();

    public FakeCrashDumpHandler Diagnostics { get; } = new();

    public FakeMetricsExposition Metrics { get; } = new();

    public FakeSinglePageAnalyzer SinglePage { get; } = new();

    public FakeRenderingPool Rendering { get; } = new();

    public FakePreCrawlValidator UrlCheck { get; } = new();

    public FakeHttpFetcher Fetcher { get; } = new();

    public FakeRedirectMatrixValidator RedirectMatrix { get; } = new();

    public FakeSemanticLinkAnalyzer Semantics { get; } = new();

    public FakeLogImportService LogImports { get; set; } = null!;

    public FakeLogCorrelationReadModel LogCorrelation { get; } = new();

    public FakeLogDnsVerificationTool DnsVerification { get; } = new();

    public FakeGeoReadinessAnalyzer Geo { get; } = new();

    public FakeAiBotGovernanceMatrix Governance { get; } = new();

    public FakeLlmsTxtGenerator LlmsGenerator { get; } = new();

    public FakeWafSignatureCatalogue WafCatalogue { get; set; } = null!;

    public FakeSystemHealthService SystemHealth { get; } = new();

    public FakeCompactService Compact { get; } = new();

    public FakeSecretProtector Secrets { get; } = new();

    public FakeContentStore ContentStore { get; } = new();

    public FakeRealtimeHub Hub { get; } = new();

    public FakeAimdPolitenessController Aimd { get; } = new();

    public FakeListModeIngestor ListMode { get; } = new();
}
