using System.Text.Json;
using CrawlForge.Audit.Scoring;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Host.Configuration;
using CrawlForge.Host.Hosting;
using CrawlForge.Intelligence.NGrams;
using CrawlForge.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Host.Composition;

/// <summary>
/// MCP module (WP-06-01): composes the accepted MCP server per its README —
/// tool/resource/prompt surfaces over the real engine, the local token service
/// (encrypted at rest, rotation invalidates prior clients), and the loopback
/// streamable-HTTP server started AFTER readiness. The local endpoint
/// descriptor feeds the stdio adapter (CF_MCP_ENDPOINT / CF_MCP_TOKEN).
/// </summary>
public sealed class McpModule : IHostModule
{
    /// <inheritdoc />
    public string Name => "mcp";

    /// <inheritdoc />
    public int Order => 40;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IMcpTokenStore>(p =>
        {
            var paths = p.GetRequiredService<CrawlForgePaths>();
            return new FileMcpTokenStore(Path.Combine(paths.DataDirectory, "mcp-token.json"));
        });
        services.AddSingleton(p => new McpTokenService(
            p.GetRequiredService<Contracts.Security.ISecretProtector>(),
            p.GetRequiredService<IMcpTokenStore>()));
        services.AddSingleton<IAutomationActionLog>(p => new AutomationActionLog(
            p.GetRequiredService<IReadRepository<AutomationActionLogDto>>(),
            p.GetRequiredService<IWriteRepository<AutomationActionLogDto>>()));

        services.AddSingleton(p => new McpToolSurface(
            p.GetRequiredService<Contracts.Crawl.IAuditExecutionService>(),
            p.GetRequiredService<IAuditLifecyclePort>(),
            p.GetRequiredService<Contracts.Crawl.IScheduledAuditService>(),
            p.GetRequiredService<Contracts.Audit.ISinglePageAnalyzer>(),
            p.GetRequiredService<IReadRepository<AuditDto>>()));
        services.AddSingleton(p => new McpResourcePorts(
            p.GetRequiredService<IReadRepository<AuditDto>>(),
            p.GetRequiredService<IReadRepository<FindingDto>>(),
            p.GetRequiredService<IReadRepository<UrlRecordDto>>(),
            p.GetRequiredService<IReadRepository<MetricSummaryDto>>(),
            p.GetRequiredService<IReadRepository<AuditScoreDto>>(),
            p.GetRequiredService<IReadRepository<ScheduleDefinitionDto>>(),
            p.GetRequiredService<IReadRepository<ScheduleRunDto>>(),
            p.GetRequiredService<IReadRepository<WafDetectionDto>>(),
            p.GetRequiredService<IReadRepository<HostThrottleEventDto>>(),
            p.GetRequiredService<IReadRepository<RedirectMappingDto>>(),
            p.GetRequiredService<IReadRepository<LogImportDto>>(),
            p.GetRequiredService<IReadRepository<LogUrlAggregateDto>>(),
            p.GetRequiredService<IReadRepository<SemanticLinkOpportunityDto>>(),
            p.GetRequiredService<IReadRepository<SemanticClusterDto>>(),
            p.GetRequiredService<IReadRepository<AnalyticsDetectionDto>>(),
            p.GetRequiredService<IReadRepository<ExtractedContentDto>>(),
            p.GetRequiredService<IReadRepository<GovernanceMatrixRowDto>>(),
            p.GetRequiredService<IReadRepository<NgramFrequencyDto>>(),
            p.GetRequiredService<IReadRepository<ContentArtifactDto>>(),
            p.GetRequiredService<IContentStore>(),
            p.GetRequiredService<NGramReadModel>(),
            p.GetRequiredService<IGeoReadinessAnalyzer>(),
            p.GetRequiredService<ILogCorrelationEngine>(),
            p.GetRequiredService<IAuditLifecyclePort>()));
        services.AddSingleton(p => new McpResourceSurface(p.GetRequiredService<McpResourcePorts>()));
        services.AddSingleton(p => new McpPromptSurface(
            p.GetRequiredService<McpResourceSurface>(),
            p.GetRequiredService<AuditComparisonReader>()));
        services.AddSingleton(p => new McpJsonRpcDispatcher(
            p.GetRequiredService<McpToolSurface>(),
            p.GetRequiredService<McpResourceSurface>(),
            p.GetRequiredService<McpPromptSurface>(),
            p.GetRequiredService<IAutomationActionLog>()));
        services.AddSingleton(p => new McpLoopbackServer(
            p.GetRequiredService<McpJsonRpcDispatcher>(),
            p.GetRequiredService<McpTokenService>()));
        services.AddSingleton<IBackendLifecycle, McpLifecycleParticipant>();
    }
}

/// <summary>
/// Starts the MCP loopback server after the backend is ready and publishes the
/// local endpoint descriptor for the stdio adapter. The descriptor lives in the
/// per-user data directory — the same trust boundary as the loopback handshake
/// (never transmitted anywhere; clean-room guardrail #4).
/// </summary>
public sealed class McpLifecycleParticipant : IBackendLifecycle
{
    private readonly McpLoopbackServer _server;
    private readonly McpTokenService _tokens;
    private readonly CrawlForgePaths _paths;
    private readonly ILogger<McpLifecycleParticipant> _logger;

    /// <summary>Creates the participant.</summary>
    /// <param name="server">Loopback MCP server.</param>
    /// <param name="tokens">Local token service.</param>
    /// <param name="paths">Per-user paths.</param>
    /// <param name="logger">Logger.</param>
    public McpLifecycleParticipant(
        McpLoopbackServer server,
        McpTokenService tokens,
        CrawlForgePaths paths,
        ILogger<McpLifecycleParticipant> logger)
    {
        _server = server;
        _tokens = tokens;
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public int Order => 40;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _tokens.InitializeAsync(cancellationToken);
        _server.Start();

        var descriptorPath = Path.Combine(_paths.DataDirectory, "mcp-endpoint.json");
        var payload = JsonSerializer.Serialize(new
        {
            endpoint = _server.MessageEndpoint,
            token = _tokens.CurrentToken
        });
        var tempPath = descriptorPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, payload, cancellationToken);
        File.Move(tempPath, descriptorPath, overwrite: true);
        _logger.LogInformation("MCP loopback server listening at {Endpoint}.", _server.MessageEndpoint);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The DI container owns the server's single disposal (it is a tracked
        // IDisposable singleton); the participant only retires the descriptor
        // during drain so no stale endpoint survives shutdown.
        var descriptorPath = Path.Combine(_paths.DataDirectory, "mcp-endpoint.json");
        if (File.Exists(descriptorPath))
        {
            File.Delete(descriptorPath);
        }

        return Task.CompletedTask;
    }
}
