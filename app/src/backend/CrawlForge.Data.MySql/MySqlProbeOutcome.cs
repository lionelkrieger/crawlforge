namespace CrawlForge.Data.MySql;

/// <summary>
/// Raw outcome of one endpoint probe against the user-configured MySQL server.
/// The probe separates transport reachability from authentication and database
/// usability so the connection tester can report a precise verdict (criterion #18).
/// </summary>
/// <param name="IsReachable">Whether the server endpoint accepted a transport connection.</param>
/// <param name="AuthenticationOk">Whether the server accepted the configured credentials.</param>
/// <param name="ServerVersion">Server version banner, when the handshake completed.</param>
/// <param name="DatabaseAccessible">Whether the configured database exists and is usable.</param>
/// <param name="ErrorDetail">Human-readable failure detail, when any stage failed.</param>
public sealed record MySqlProbeOutcome(
    bool IsReachable,
    bool AuthenticationOk,
    string? ServerVersion,
    bool DatabaseAccessible,
    string? ErrorDetail)
{
    /// <summary>Creates an unreachable-endpoint outcome (transport or DNS failure).</summary>
    /// <param name="errorDetail">Failure detail.</param>
    /// <returns>The outcome.</returns>
    public static MySqlProbeOutcome Unreachable(string errorDetail) =>
        new(false, false, null, false, errorDetail);

    /// <summary>Creates a reachable-but-rejected-credentials outcome.</summary>
    /// <param name="errorDetail">Server rejection detail.</param>
    /// <returns>The outcome.</returns>
    public static MySqlProbeOutcome AuthenticationFailed(string errorDetail) =>
        new(true, false, null, false, errorDetail);

    /// <summary>Creates a fully successful outcome.</summary>
    /// <param name="serverVersion">Server version banner.</param>
    /// <returns>The outcome.</returns>
    public static MySqlProbeOutcome Success(string serverVersion) =>
        new(true, true, serverVersion, true, null);
}
