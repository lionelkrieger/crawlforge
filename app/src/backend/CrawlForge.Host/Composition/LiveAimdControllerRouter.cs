using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Host.Execution;

namespace CrawlForge.Host.Composition;

/// <summary>
/// Live AIMD router (WP-06-01 reopen): the locked
/// <see cref="IAimdPolitenessController"/> DI surface delegates to the
/// currently running audit's per-audit controller, so the host explorer and
/// manual rate override always address the live politeness state. When no
/// audit is crawling, an idle default controller answers (empty state,
/// harmless overrides) — the surface never fails activation. Resolution is
/// lazy through the provider, keeping composition free of cycles.
/// </summary>
public sealed class LiveAimdControllerRouter : IAimdPolitenessController
{
    private readonly IServiceProvider _services;
    private readonly Lazy<AimdPolitenessController> _idle = new(() => new AimdPolitenessController());

    /// <summary>Creates the router over the application service provider.</summary>
    /// <param name="services">Provider used to resolve the execution engine lazily.</param>
    public LiveAimdControllerRouter(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    private IAimdPolitenessController Current =>
        _services.GetService(typeof(AuditExecutionEngine)) is AuditExecutionEngine engine
        && engine.ActiveAimdController is { } live
            ? live
            : _idle.Value;

    /// <inheritdoc />
    public ValueTask WaitAsync(string host, CancellationToken cancellationToken = default) =>
        Current.WaitAsync(host, cancellationToken);

    /// <inheritdoc />
    public void RecordResponse(string host, HostResponseSample sample) =>
        Current.RecordResponse(host, sample);

    /// <inheritdoc />
    public HostThrottleState GetState(string host) => Current.GetState(host);

    /// <inheritdoc />
    public void SetManualOverride(string host, double? requestsPerSecond) =>
        Current.SetManualOverride(host, requestsPerSecond);

    /// <inheritdoc />
    public AimdCheckpointState CreateCheckpointState() => Current.CreateCheckpointState();

    /// <inheritdoc />
    public void RestoreCheckpointState(AimdCheckpointState state) =>
        Current.RestoreCheckpointState(state);
}
