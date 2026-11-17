using MySqlConnector;

namespace CrawlForge.Data.MySql;

/// <summary>
/// Live endpoint probe built on MySqlConnector. Opens one connection to the
/// user-configured server (TCP reachability and the authentication handshake happen
/// inside <c>OpenAsync</c>), verifies the configured database with a trivial query,
/// and disposes the connection immediately — no idle connection is retained.
/// Server error codes are classified into reachability / authentication / database
/// verdict inputs; transport failures become unreachable outcomes.
/// </summary>
public sealed class MySqlConnectorEndpointProbe : IMySqlEndpointProbe
{
    // MySQL server error numbers that mean "the server was reached but rejected the
    // credentials or the connection before authentication completed".
    private static readonly int[] AuthenticationDeniedNumbers =
    [
        1045, // ER_ACCESS_DENIED_ERROR (bad user/password)
        1698, // ER_ACCESS_DENIED_NO_PASSWORD_ERROR (auth plugin refusal)
        1524, // ER_PLUGIN_IS_NOT_LOADED
        1129, // ER_HOST_IS_BLOCKED
        1130, // ER_HOST_NOT_PRIVILEGED
        1040, // ER_CON_COUNT_ERROR (server connection limit)
        1203, // ER_MAX_USER_CONNECTIONS
        1226, // ER_USER_LIMIT_REACHED
    ];

    // MySQL server error numbers that mean "credentials were accepted at the server
    // level but the configured database is missing or forbidden".
    private static readonly int[] DatabaseDeniedNumbers =
    [
        1044, // ER_DBACCESS_DENIED_ERROR
        1049, // ER_BAD_DB_ERROR (unknown database)
    ];

    private readonly IMySqlConnectionFactory _connectionFactory;

    /// <summary>Creates a probe that opens connections through the given factory.</summary>
    /// <param name="connectionFactory">Factory bound to the user-configured endpoint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connectionFactory"/> is null.</exception>
    public MySqlConnectorEndpointProbe(IMySqlConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the probe.</exception>
    public async Task<MySqlProbeOutcome> ProbeAsync(MySqlDatabaseSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        MySqlConnection? connection = null;
        try
        {
            connection = _connectionFactory.Create(settings);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            string serverVersion = connection.ServerVersion;

            // A successful handshake already selected the configured database; the
            // trivial query confirms the session can actually run statements.
            using (MySqlCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            return MySqlProbeOutcome.Success(serverVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MySqlException ex)
        {
            return ClassifyMySqlError(ex);
        }
        catch (Exception ex)
        {
            // Transport-level failures (DNS, refused, timed out, TLS) mean the
            // endpoint is not usable; the tester reports an unreachable verdict.
            return MySqlProbeOutcome.Unreachable(ex.Message);
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static MySqlProbeOutcome ClassifyMySqlError(MySqlException exception)
    {
        // Connect-phase failures carry no server error packet and mean the endpoint
        // is unreachable:
        //  - Number 0: no server error code at all;
        //  - 2000-2999: the MySQL client-error block (CR_* codes such as 2003
        //    "unable to connect to host", raised for transport failures);
        //  - 1042 (ER_BAD_HOST_ERROR): MySqlConnector also raises this number for
        //    "Connect Timeout expired" — a transport timeout, never an auth verdict.
        if (exception.Number == 0 || exception.Number is >= 2000 and <= 2999 || exception.Number == 1042)
        {
            return MySqlProbeOutcome.Unreachable(exception.Message);
        }

        if (DatabaseDeniedNumbers.Contains(exception.Number))
        {
            return new MySqlProbeOutcome(true, true, null, false, exception.Message);
        }

        if (AuthenticationDeniedNumbers.Contains(exception.Number))
        {
            return MySqlProbeOutcome.AuthenticationFailed(exception.Message);
        }

        // Any other server error packet proves the server was reached, but neither
        // authentication nor database usability can be confirmed.
        return new MySqlProbeOutcome(true, false, null, false, exception.Message);
    }
}
