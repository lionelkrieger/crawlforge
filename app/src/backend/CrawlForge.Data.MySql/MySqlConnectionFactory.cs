using MySqlConnector;

namespace CrawlForge.Data.MySql;

/// <summary>
/// Creates MySQL connections bound to the user-configured endpoint. The factory is
/// the single choke point for outbound database traffic: every connection it creates
/// targets exactly the server named in <see cref="MySqlDatabaseSettings"/>.
/// </summary>
public interface IMySqlConnectionFactory
{
    /// <summary>Creates a closed connection for the given user-configured settings.</summary>
    /// <param name="settings">User-configured endpoint and credentials.</param>
    /// <returns>A closed connection owned by the caller.</returns>
    MySqlConnection Create(MySqlDatabaseSettings settings);
}

/// <summary>
/// Default connection factory (FS-CRAWL-001 §2.4, network boundary). The connection
/// string carries exactly one server — the user-configured host and port — and never
/// a failover, load-balancing, or vendor endpoint. Pooling is disabled so no idle
/// connection is held open between operations (zero idle outbound traffic); timeouts
/// are bounded so an unreachable server can never hang the desktop backend.
/// </summary>
public sealed class MySqlConnectionFactory : IMySqlConnectionFactory
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentException">The settings are incomplete or out of range.</exception>
    public MySqlConnection Create(MySqlDatabaseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new MySqlConnection(BuildConnectionString(settings));
    }

    /// <summary>
    /// Builds the connection string for the user-configured endpoint. Exposed for
    /// boundary conformance tests: the result contains exactly one server, the
    /// configured database, and pooling disabled.
    /// </summary>
    /// <param name="settings">User-configured endpoint and credentials.</param>
    /// <returns>The MySQL connection string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentException">The settings are incomplete or out of range.</exception>
    public static string BuildConnectionString(MySqlDatabaseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        var builder = new MySqlConnectionStringBuilder
        {
            Server = settings.Host,
            Port = (uint)settings.Port,
            Database = settings.Database,
            UserID = settings.UserId,
            Password = settings.Password,
            ConnectionTimeout = (uint)settings.ConnectTimeoutSeconds,
            DefaultCommandTimeout = (uint)settings.CommandTimeoutSeconds,
            // Desktop lifetime: open on demand, close after use. No pooled idle
            // connections are kept open to the user's server between operations.
            Pooling = false,
        };

        return builder.ConnectionString;
    }
}
