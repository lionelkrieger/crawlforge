using MySqlConnector;
using Xunit;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Outbound-boundary conformance (network boundary, orchestrator note 4): every
/// connection targets exactly the user-configured server, no other destination is
/// ever introduced, and no idle connection is retained (pooling disabled).
/// </summary>
public sealed class BoundaryTests
{
    private static MySqlDatabaseSettings ValidSettings() => new()
    {
        Host = "db.example.invalid",
        Port = 3307,
        Database = "crawlforge",
        UserId = "cf_user",
        Password = "cf_password",
        ConnectTimeoutSeconds = 9,
        CommandTimeoutSeconds = 21,
    };

    /// <summary>The connection string carries exactly the user-configured endpoint and credentials.</summary>
    [Fact]
    public void Connection_String_Contains_Only_The_User_Configured_Server()
    {
        string connectionString = MySqlConnectionFactory.BuildConnectionString(ValidSettings());
        var parsed = new MySqlConnectionStringBuilder(connectionString);

        Assert.Equal("db.example.invalid", parsed.Server);
        Assert.Equal(3307u, parsed.Port);
        Assert.Equal("crawlforge", parsed.Database);
        Assert.Equal("cf_user", parsed.UserID);
        Assert.Equal("cf_password", parsed.Password);
        Assert.Equal(9u, parsed.ConnectionTimeout);
        Assert.Equal(21u, parsed.DefaultCommandTimeout);
    }

    /// <summary>No failover or secondary server list is ever introduced.</summary>
    [Fact]
    public void Connection_String_Has_A_Single_Server()
    {
        string connectionString = MySqlConnectionFactory.BuildConnectionString(ValidSettings());
        var parsed = new MySqlConnectionStringBuilder(connectionString);

        Assert.DoesNotContain(",", parsed.Server, StringComparison.Ordinal);
        Assert.Equal("db.example.invalid", parsed.Server);
    }

    /// <summary>Pooling is disabled so no idle outbound connection is held between operations.</summary>
    [Fact]
    public void Pooling_Is_Disabled_For_Zero_Idle_Outbound()
    {
        string connectionString = MySqlConnectionFactory.BuildConnectionString(ValidSettings());
        var parsed = new MySqlConnectionStringBuilder(connectionString);

        Assert.False(parsed.Pooling);
    }

    /// <summary>The factory creates a closed connection bound to the configured endpoint.</summary>
    [Fact]
    public void Factory_Creates_Closed_Connection_For_Configured_Endpoint()
    {
        var factory = new MySqlConnectionFactory();

        using MySqlConnection connection = factory.Create(ValidSettings());

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        var parsed = new MySqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal("db.example.invalid", parsed.Server);
    }

    /// <summary>Incomplete or out-of-range settings are rejected before any connection attempt.</summary>
    [Theory]
    [InlineData("", 3306, "db", "user")]
    [InlineData(" ", 3306, "db", "user")]
    [InlineData("host", 0, "db", "user")]
    [InlineData("host", 65536, "db", "user")]
    [InlineData("host", 3306, "", "user")]
    [InlineData("host", 3306, "db", "")]
    public void Invalid_Settings_Are_Rejected(string host, int port, string database, string userId)
    {
        var settings = new MySqlDatabaseSettings
        {
            Host = host,
            Port = port,
            Database = database,
            UserId = userId,
        };

        Assert.Throws<ArgumentException>(() => settings.Validate());
        Assert.Throws<ArgumentException>(() => MySqlConnectionFactory.BuildConnectionString(settings));
    }

    /// <summary>Non-positive timeouts are rejected.</summary>
    [Theory]
    [InlineData(0, 30)]
    [InlineData(15, 0)]
    public void NonPositive_Timeouts_Are_Rejected(int connectTimeout, int commandTimeout)
    {
        MySqlDatabaseSettings settings = ValidSettings() with
        {
            ConnectTimeoutSeconds = connectTimeout,
            CommandTimeoutSeconds = commandTimeout,
        };

        Assert.Throws<ArgumentException>(() => settings.Validate());
    }

    /// <summary>Defaults match the documented MySQL conventions.</summary>
    [Fact]
    public void Defaults_Are_MySql_Conventions()
    {
        var settings = new MySqlDatabaseSettings
        {
            Host = "db.example.invalid",
            Database = "crawlforge",
            UserId = "cf_user",
        };

        Assert.Equal(3306, settings.Port);
        Assert.Equal(15, settings.ConnectTimeoutSeconds);
        Assert.Equal(30, settings.CommandTimeoutSeconds);
        Assert.Equal(string.Empty, settings.Password);
        settings.Validate();
    }
}
