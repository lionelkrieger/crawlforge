using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Data.MySql;

/// <summary>
/// Connection-test action for the optional external MySQL database (criterion #18;
/// REST surface: POST /api/settings/database/test). Reports reachability, credential
/// validity, and migration compatibility before first use, contacting only the
/// user-configured server. An unconfigured installation produces a negative verdict
/// with zero network traffic.
/// </summary>
/// <remarks>
/// Migration compatibility is gated on the server version: the logical schema and
/// its dialect adaptation require MySQL 8.0 features (common table expressions,
/// window functions, <c>JSON_TABLE</c>, <c>REGEXP_LIKE</c>, descending indexes).
/// The version gate is the provider-level compatibility check; the versioned
/// migrations themselves run through <see cref="ISchemaMigrator"/> at startup.
/// </remarks>
public sealed class MySqlConnectionTester
{
    /// <summary>
    /// Minimum supported MySQL server version. 8.0 supplies every capability the
    /// logical schema needs natively (FS-CRAWL-001 §2.4 required capabilities).
    /// </summary>
    public static Version MinimumServerVersion { get; } = new(8, 0, 0);

    private readonly IMySqlEndpointProbe _probe;

    /// <summary>Creates a tester over the given endpoint probe.</summary>
    /// <param name="probe">Probe contacting only the user-configured server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> is null.</exception>
    public MySqlConnectionTester(IMySqlEndpointProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    /// <summary>
    /// Runs the connection test and maps the probe outcome to the locked verdict
    /// contract <see cref="DbConnectionTestResult"/>.
    /// </summary>
    /// <param name="settings">User-configured settings, or null when unconfigured.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connection test verdict.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled the test.</exception>
    public async Task<DbConnectionTestResult> TestAsync(MySqlDatabaseSettings? settings, CancellationToken cancellationToken = default)
    {
        if (settings is null)
        {
            return new DbConnectionTestResult(
                false, false, false,
                "The external MySQL database is not configured; no connection attempt was made.");
        }

        try
        {
            settings.Validate();
        }
        catch (ArgumentException ex)
        {
            return new DbConnectionTestResult(false, false, false, ex.Message);
        }

        MySqlProbeOutcome outcome = await _probe.ProbeAsync(settings, cancellationToken).ConfigureAwait(false);

        if (!outcome.IsReachable)
        {
            return new DbConnectionTestResult(
                false, false, false,
                $"The configured server is unreachable. {outcome.ErrorDetail}");
        }

        if (!outcome.AuthenticationOk)
        {
            return new DbConnectionTestResult(
                true, false, false,
                $"The configured server rejected the credentials. {outcome.ErrorDetail}");
        }

        if (!outcome.DatabaseAccessible)
        {
            return new DbConnectionTestResult(
                true, true, false,
                $"Authenticated, but the configured database is not usable. {outcome.ErrorDetail}");
        }

        if (!TryParseServerVersion(outcome.ServerVersion, out Version? version) || version is null)
        {
            return new DbConnectionTestResult(
                true, true, false,
                "The server version could not be determined; migration compatibility cannot be confirmed.");
        }

        if (version < MinimumServerVersion)
        {
            return new DbConnectionTestResult(
                true, true, false,
                $"MySQL {version} is below the minimum supported version {MinimumServerVersion} required by the schema migrations.");
        }

        return new DbConnectionTestResult(
            true, true, true,
            $"Connected to MySQL {version}; database '{settings.Database}' accepts connections and is migration-compatible.");
    }

    /// <summary>
    /// Parses the leading numeric portion of a MySQL version banner (for example
    /// <c>8.0.36-0ubuntu0.22.04.1</c> yields <c>8.0.36</c>). Distribution and
    /// MariaDB compatibility prefixes never produce a version at or above the
    /// MySQL 8.0 gate unless the server really is MySQL 8.0+.
    /// </summary>
    /// <param name="serverVersion">Raw server version banner.</param>
    /// <param name="version">Parsed version, when successful.</param>
    /// <returns>True when a version could be parsed.</returns>
    public static bool TryParseServerVersion(string? serverVersion, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            return false;
        }

        int end = 0;
        while (end < serverVersion.Length && (char.IsAsciiDigit(serverVersion[end]) || serverVersion[end] == '.'))
        {
            end++;
        }

        string numeric = serverVersion[..end].TrimEnd('.');
        if (numeric.Length == 0)
        {
            return false;
        }

        // Normalize to at least major.minor.build so Version comparison against the
        // three-component minimum is exact (Version(8,0) would sort below 8.0.0).
        string[] parts = numeric.Split('.');
        if (parts.Length == 1)
        {
            numeric = parts[0] + ".0.0";
        }
        else if (parts.Length == 2)
        {
            numeric = parts[0] + "." + parts[1] + ".0";
        }

        return Version.TryParse(numeric, out version);
    }
}
