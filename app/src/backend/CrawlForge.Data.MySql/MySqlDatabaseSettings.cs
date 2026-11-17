namespace CrawlForge.Data.MySql;

/// <summary>
/// User-configured connection settings for the optional external MySQL database
/// (FS-CRAWL-001 §2.4). The server is the user's own infrastructure: these settings
/// describe the only outbound destination this provider may ever contact. No vendor
/// service exists or is contacted, and an unconfigured installation never reaches
/// this code path (first-run initialization stays fully offline, §9.4).
/// </summary>
public sealed record MySqlDatabaseSettings
{
    /// <summary>Default MySQL TCP port.</summary>
    public const int DefaultPort = 3306;

    /// <summary>Default connect timeout in seconds.</summary>
    public const int DefaultConnectTimeoutSeconds = 15;

    /// <summary>Default command timeout in seconds.</summary>
    public const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>Host name or IP address of the user-configured server.</summary>
    public required string Host { get; init; }

    /// <summary>TCP port of the user-configured server (1–65535).</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Database name on the user-configured server.</summary>
    public required string Database { get; init; }

    /// <summary>Database user name. A credential: encrypted at rest (§9.1).</summary>
    public required string UserId { get; init; }

    /// <summary>Database password. A credential: encrypted at rest (§9.1).</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>Connect timeout in seconds (must be positive).</summary>
    public int ConnectTimeoutSeconds { get; init; } = DefaultConnectTimeoutSeconds;

    /// <summary>Default command timeout in seconds (must be positive).</summary>
    public int CommandTimeoutSeconds { get; init; } = DefaultCommandTimeoutSeconds;

    /// <summary>
    /// Validates the settings. The endpoint must be fully user-configured before any
    /// connection attempt; incomplete settings are rejected locally with zero network
    /// traffic.
    /// </summary>
    /// <exception cref="ArgumentException">A required value is missing or out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("The external database host must be configured by the user.", nameof(Host));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException($"The external database port must be between 1 and 65535 (got {Port}).", nameof(Port));
        }

        if (string.IsNullOrWhiteSpace(Database))
        {
            throw new ArgumentException("The external database name must be configured by the user.", nameof(Database));
        }

        if (string.IsNullOrWhiteSpace(UserId))
        {
            throw new ArgumentException("The external database user must be configured by the user.", nameof(UserId));
        }

        if (ConnectTimeoutSeconds < 1)
        {
            throw new ArgumentException("The connect timeout must be at least one second.", nameof(ConnectTimeoutSeconds));
        }

        if (CommandTimeoutSeconds < 1)
        {
            throw new ArgumentException("The command timeout must be at least one second.", nameof(CommandTimeoutSeconds));
        }
    }
}
