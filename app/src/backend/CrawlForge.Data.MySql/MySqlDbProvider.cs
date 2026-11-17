using System.Data.Common;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using MySqlConnector;

namespace CrawlForge.Data.MySql;

/// <summary>
/// <see cref="IDbProvider"/> implementation for the optional user-owned external
/// MySQL database (FS-CRAWL-001 §2.4). Implements the same logical schema behavior
/// as the embedded provider (provider agnosticism, precedence rule 6) through the
/// MySQL dialect, and confines every outbound connection strictly to the
/// user-configured server. The provider is never contacted unless the user has
/// configured it: a null-settings instance refuses operations locally.
/// </summary>
public sealed class MySqlDbProvider : IDbProvider
{
    private readonly MySqlDatabaseSettings? _settings;
    private readonly IMySqlConnectionFactory _connectionFactory;
    private readonly MySqlConnectionTester _tester;

    /// <summary>
    /// Creates a provider for the given user-configured endpoint, or an unconfigured
    /// provider when <paramref name="settings"/> is null.
    /// </summary>
    /// <param name="settings">User-configured endpoint, or null when unconfigured.</param>
    /// <param name="connectionFactory">Optional factory override (tests/composition).</param>
    /// <param name="probe">Optional endpoint probe override (tests/composition).</param>
    public MySqlDbProvider(
        MySqlDatabaseSettings? settings,
        IMySqlConnectionFactory? connectionFactory = null,
        IMySqlEndpointProbe? probe = null)
    {
        _settings = settings;
        _connectionFactory = connectionFactory ?? new MySqlConnectionFactory();
        _tester = new MySqlConnectionTester(probe ?? new MySqlConnectorEndpointProbe(_connectionFactory));
    }

    /// <inheritdoc />
    public DatabaseProviderKind Kind => DatabaseProviderKind.MySql;

    /// <inheritdoc />
    public string Dialect => MySqlDialect.DialectName;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The external database is not configured.</exception>
    /// <exception cref="ArgumentException">The configured settings are invalid.</exception>
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        MySqlDatabaseSettings settings = RequireConfigured();
        MySqlConnection connection = _connectionFactory.Create(settings);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegates to <see cref="MySqlConnectionTester"/>; an unconfigured provider
    /// returns a negative verdict without any network attempt.
    /// </remarks>
    public Task<DbConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        _tester.TestAsync(_settings, cancellationToken);

    private MySqlDatabaseSettings RequireConfigured()
    {
        if (_settings is null)
        {
            throw new InvalidOperationException(
                "The external MySQL database is not configured. The embedded provider remains the default; no external server is contacted.");
        }

        _settings.Validate();
        return _settings;
    }
}
