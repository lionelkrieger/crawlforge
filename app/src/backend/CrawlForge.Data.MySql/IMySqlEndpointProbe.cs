namespace CrawlForge.Data.MySql;

/// <summary>
/// Probes the user-configured MySQL endpoint once, without side effects. The probe
/// is the only component that touches the network during a connection test, and it
/// contacts exactly the configured server — nothing else (network boundary).
/// Implementations must translate endpoint failures into outcomes rather than
/// throwing; only genuine caller cancellation may propagate.
/// </summary>
public interface IMySqlEndpointProbe
{
    /// <summary>Probes reachability, authentication, and database usability.</summary>
    /// <param name="settings">User-configured endpoint and credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The probe outcome.</returns>
    Task<MySqlProbeOutcome> ProbeAsync(MySqlDatabaseSettings settings, CancellationToken cancellationToken = default);
}
