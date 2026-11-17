using System.Data.Common;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Xunit;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Provider contract conformance: kind/dialect, caller-owned open connections,
/// connection-test delegation, and the unconfigured-provider guard that keeps the
/// default installation fully offline (§9.4).
/// </summary>
public sealed class ProviderTests
{
    private static MySqlDatabaseSettings ValidSettings() => new()
    {
        Host = "db.example.invalid",
        Database = "crawlforge",
        UserId = "cf_user",
        Password = "cf_password",
    };

    /// <summary>The provider identifies as the MySQL dialect of the external kind.</summary>
    [Fact]
    public void Provider_Kind_And_Dialect_Are_MySql()
    {
        var provider = new MySqlDbProvider(ValidSettings());

        Assert.Equal(DatabaseProviderKind.MySql, provider.Kind);
        Assert.Equal("mysql", provider.Dialect);
    }

    /// <summary>An unconfigured provider refuses to open connections without touching the factory.</summary>
    [Fact]
    public async Task Unconfigured_Provider_Open_Throws_Without_Factory_Call()
    {
        var factory = new CountingConnectionFactory();
        var provider = new MySqlDbProvider(settings: null, connectionFactory: factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenConnectionAsync());

        Assert.Equal(0, factory.Calls);
    }

    /// <summary>An unconfigured provider's connection test never probes the network.</summary>
    [Fact]
    public async Task Unconfigured_Provider_Test_Reports_Not_Configured_Without_Probe()
    {
        var probe = new RecordingProbe();
        var provider = new MySqlDbProvider(settings: null, probe: probe);

        DbConnectionTestResult result = await provider.TestConnectionAsync();

        Assert.False(result.IsReachable);
        Assert.False(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
        Assert.Contains("not configured", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, probe.Calls);
    }

    /// <summary>Opening a connection goes through the factory with the user's settings and attempts the configured endpoint only.</summary>
    [Fact]
    public async Task Open_Uses_Factory_And_Attempts_The_Configured_Endpoint()
    {
        // A closed loopback port stands in for an unreachable user server: the open
        // attempt must fail fast, proving the provider really dials the endpoint.
        int closedPort = ClosedLoopbackEndpoint.GetClosedPort();
        MySqlDatabaseSettings settings = ValidSettings() with
        {
            Host = "127.0.0.1",
            Port = closedPort,
            ConnectTimeoutSeconds = 2,
        };
        var factory = new CountingConnectionFactory();
        var provider = new MySqlDbProvider(settings, connectionFactory: factory);

        await Assert.ThrowsAnyAsync<Exception>(() => provider.OpenConnectionAsync());

        Assert.Equal(1, factory.Calls);
        Assert.Same(settings, factory.LastSettings);
    }

    /// <summary>The provider's connection test delegates to the tester/probe pipeline.</summary>
    [Fact]
    public async Task Test_Delegates_To_Probe_Pipeline()
    {
        var probe = new RecordingProbe { Outcome = MySqlProbeOutcome.Success("8.0.36") };
        var provider = new MySqlDbProvider(ValidSettings(), probe: probe);

        DbConnectionTestResult result = await provider.TestConnectionAsync();

        Assert.True(result.IsReachable);
        Assert.True(result.AuthenticationOk);
        Assert.True(result.MigrationCompatible);
        Assert.Equal(1, probe.Calls);
    }

    /// <summary>The real probe against an unreachable endpoint yields an unreachable verdict, never an exception.</summary>
    [Fact]
    public async Task Real_Probe_Against_Closed_Endpoint_Reports_Unreachable()
    {
        int closedPort = ClosedLoopbackEndpoint.GetClosedPort();
        MySqlDatabaseSettings settings = ValidSettings() with
        {
            Host = "127.0.0.1",
            Port = closedPort,
            ConnectTimeoutSeconds = 2,
        };
        var provider = new MySqlDbProvider(settings);

        DbConnectionTestResult result = await provider.TestConnectionAsync();

        Assert.False(result.IsReachable, "Expected unreachable verdict; got: " + result.Message);
        Assert.False(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
        Assert.NotNull(result.Message);
    }

    /// <summary>Invalid settings surface as a negative verdict, not an exception, from the test action.</summary>
    [Fact]
    public async Task Test_With_Invalid_Settings_Reports_Negative_Verdict()
    {
        MySqlDatabaseSettings settings = ValidSettings() with { Port = 0 };
        var provider = new MySqlDbProvider(settings);

        DbConnectionTestResult result = await provider.TestConnectionAsync();

        Assert.False(result.IsReachable);
        Assert.False(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
    }
}
