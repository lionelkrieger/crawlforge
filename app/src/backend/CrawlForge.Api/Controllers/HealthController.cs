using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>GET /api/health — readiness flag and process uptime (FS-CRAWL-001 §10.2).</summary>
/// <param name="Ready">True once the backend is ready to serve the shell.</param>
/// <param name="UptimeSeconds">Backend process uptime in seconds.</param>
public sealed record HealthResponse(bool Ready, double UptimeSeconds);

/// <summary>GET /api/version — product version, schema version, runtime profile.</summary>
/// <param name="ProductVersion">Product version string.</param>
/// <param name="SchemaVersion">Applied migration schema level of the local database.</param>
/// <param name="RuntimeProfile">Active runtime profile identifier (local configuration only).</param>
public sealed record VersionResponse(string ProductVersion, int SchemaVersion, string RuntimeProfile);

/// <summary>
/// Liveness surfaces of the loopback API (FS-CRAWL-001 §10.2): health probe and
/// version info. Local technical data only — no identity dimensions.
/// </summary>
[Route("api")]
public sealed class HealthController : ApiControllerBase
{
    private static readonly DateTimeOffset ProcessStart = DateTimeOffset.UtcNow;

    private readonly ISchemaMigrator _migrator;

    /// <summary>Creates the controller.</summary>
    /// <param name="migrator">Schema migrator serving the version surface.</param>
    public HealthController(ISchemaMigrator migrator) => _migrator = migrator;

    /// <summary>Health probe: readiness flag and uptime.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Health envelope.</returns>
    [HttpGet("health")]
    public Task<ObjectResult> GetHealthAsync(CancellationToken cancellationToken)
    {
        var uptime = (DateTimeOffset.UtcNow - ProcessStart).TotalSeconds;
        return Task.FromResult(OkEnvelope(new HealthResponse(true, Math.Round(uptime, 3))));
    }

    /// <summary>Version info: product version, schema version, active runtime profile.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Version envelope.</returns>
    [HttpGet("version")]
    public async Task<ObjectResult> GetVersionAsync(CancellationToken cancellationToken)
    {
        var schemaVersion = await _migrator.GetCurrentVersionAsync(cancellationToken);
        var productVersion = typeof(HealthController).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return OkEnvelope(new VersionResponse(productVersion, schemaVersion, "local-default"));
    }
}

/// <summary>
/// GET /metrics — pull-only local technical metrics in Prometheus-class text
/// exposition (FS-CRAWL-001 §10.5). NOT JSON-enveloped; never pushed anywhere.
/// </summary>
[ApiController]
public sealed class MetricsController : ControllerBase
{
    private readonly IMetricsExposition _exposition;

    /// <summary>Creates the controller.</summary>
    /// <param name="exposition">Local metrics exposition surface.</param>
    public MetricsController(IMetricsExposition exposition) => _exposition = exposition;

    /// <summary>Renders the current metric families as text.</summary>
    /// <returns>Plain-text exposition payload.</returns>
    [HttpGet("/metrics")]
    public IActionResult GetMetrics() =>
        Content(_exposition.Render(), "text/plain; version=0.0.4; charset=utf-8");
}
