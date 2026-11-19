using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Host.Execution;
using CrawlForge.Host.Hosting;
using CrawlForge.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Host.Composition;

/// <summary>
/// Execution module (WP-06-01): binds the audit execution engine onto both the
/// locked <c>IAuditExecutionService</c> contract and the MCP-local
/// <c>IAuditLifecyclePort</c> adapter surface, and registers the startup
/// ordering participants — migrations BEFORE any engine start or readiness
/// signal, scheduler AFTER the engine (composition ordering ruling).
/// </summary>
public sealed class ExecutionModule : IHostModule
{
    /// <inheritdoc />
    public string Name => "execution";

    /// <inheritdoc />
    public int Order => 20;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<AuditExecutionEngine>();
        services.AddSingleton<IAuditExecutionService>(p => p.GetRequiredService<AuditExecutionEngine>());
        services.AddSingleton<IAuditLifecyclePort>(p => p.GetRequiredService<AuditExecutionEngine>());
        services.AddSingleton<CrawlForge.Api.Controllers.IReAuditExecutionPort>(p => p.GetRequiredService<AuditExecutionEngine>());
        // WP-09-11: G10 persist-before-start list-audit port (REST delegates).
        services.AddSingleton<CrawlForge.Api.Controllers.IListAuditStartPort>(p => p.GetRequiredService<AuditExecutionEngine>());
        services.AddSingleton<IBackendLifecycle>(p => p.GetRequiredService<AuditExecutionEngine>());

        services.AddSingleton<IBackendLifecycle, MigrationLifecycleParticipant>();
        services.AddSingleton<IBackendLifecycle, SchedulerLifecycleParticipant>();
    }
}

/// <summary>
/// Runs schema migrations before any engine participant starts and before the
/// readiness signal (process-model spec §2.1: migrations + seeding precede
/// health). First-launch directory/settings seeding already ran in the backend
/// bootstrap; this participant owns the database schema exclusively.
/// </summary>
public sealed class MigrationLifecycleParticipant : IBackendLifecycle
{
    private readonly ISchemaMigrator _migrator;
    private readonly ILogger<MigrationLifecycleParticipant> _logger;

    /// <summary>Creates the participant.</summary>
    /// <param name="migrator">Schema migrator.</param>
    /// <param name="logger">Logger.</param>
    public MigrationLifecycleParticipant(ISchemaMigrator migrator, ILogger<MigrationLifecycleParticipant> logger)
    {
        _migrator = migrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public int Order => -100;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _migrator.MigrateAsync(cancellationToken);
        var version = await _migrator.GetCurrentVersionAsync(cancellationToken);
        _logger.LogInformation("Schema migrations applied; schema version {Version}.", version);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Starts the local scheduler after the execution engine is ready and drains
/// it first on shutdown (reverse order), so no schedule fires into a draining
/// engine (process-model spec §2.3).
/// </summary>
public sealed class SchedulerLifecycleParticipant : IBackendLifecycle
{
    private readonly IScheduledAuditService _scheduler;
    private readonly ILogger<SchedulerLifecycleParticipant> _logger;

    /// <summary>Creates the participant.</summary>
    /// <param name="scheduler">Local scheduler.</param>
    /// <param name="logger">Logger.</param>
    public SchedulerLifecycleParticipant(IScheduledAuditService scheduler, ILogger<SchedulerLifecycleParticipant> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    /// <inheritdoc />
    public int Order => 30;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _scheduler.StartAsync(cancellationToken);
        _logger.LogInformation("Local scheduler started.");
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _scheduler.StopAsync(cancellationToken);
        _logger.LogInformation("Local scheduler drained.");
    }
}
