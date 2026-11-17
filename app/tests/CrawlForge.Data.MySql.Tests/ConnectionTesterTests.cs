using CrawlForge.Contracts.Persistence;
using Xunit;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Connection-test verdict correctness (criterion #18), proven without a live
/// server: the full reachability / credential / migration-compatibility matrix
/// against fake probe outcomes, plus unconfigured and invalid-settings guards.
/// </summary>
public sealed class ConnectionTesterTests
{
    private static MySqlDatabaseSettings ValidSettings() => new()
    {
        Host = "db.example.invalid",
        Port = 3306,
        Database = "crawlforge",
        UserId = "cf_user",
        Password = "cf_password",
    };

    /// <summary>An unconfigured installation yields a negative verdict with zero probe calls.</summary>
    [Fact]
    public async Task Unconfigured_Settings_Produce_Negative_Verdict_Without_Network()
    {
        var probe = new RecordingProbe();
        var tester = new MySqlConnectionTester(probe);

        DbConnectionTestResult result = await tester.TestAsync(settings: null);

        Assert.False(result.IsReachable);
        Assert.False(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
        Assert.NotNull(result.Message);
        Assert.Equal(0, probe.Calls);
    }

    /// <summary>Invalid settings are rejected locally before any probe call.</summary>
    [Fact]
    public async Task Invalid_Settings_Produce_Negative_Verdict_Without_Network()
    {
        var probe = new RecordingProbe();
        var tester = new MySqlConnectionTester(probe);
        MySqlDatabaseSettings invalid = ValidSettings() with { Host = " " };

        DbConnectionTestResult result = await tester.TestAsync(invalid);

        Assert.False(result.IsReachable);
        Assert.False(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
        Assert.Equal(0, probe.Calls);
    }

    /// <summary>An unreachable endpoint reports all three verdict flags false.</summary>
    [Fact]
    public async Task Unreachable_Endpoint_Reports_All_False()
    {
        var probe = new RecordingProbe { Outcome = MySqlProbeOutcome.Unreachable("Connection refused") };
        var tester = new MySqlConnectionTester(probe);

        DbConnectionTestResult result = await tester.TestAsync(ValidSettings());

        Assert.False(result.IsReachable);
        Assert.False(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
        Assert.Contains("unreachable", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, probe.Calls);
    }

    /// <summary>Rejected credentials report reachable but not authenticated.</summary>
    [Fact]
    public async Task Bad_Credentials_Report_Reachable_Not_Authenticated()
    {
        var probe = new RecordingProbe
        {
            Outcome = MySqlProbeOutcome.AuthenticationFailed("Access denied for user 'cf_user'"),
        };
        var tester = new MySqlConnectionTester(probe);

        DbConnectionTestResult result = await tester.TestAsync(ValidSettings());

        Assert.True(result.IsReachable);
        Assert.False(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
        Assert.Contains("credentials", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A missing or forbidden database reports authenticated but not migration-compatible.</summary>
    [Fact]
    public async Task Unusable_Database_Reports_Authenticated_Not_Compatible()
    {
        var probe = new RecordingProbe
        {
            Outcome = new MySqlProbeOutcome(true, true, null, false, "Unknown database 'crawlforge'"),
        };
        var tester = new MySqlConnectionTester(probe);

        DbConnectionTestResult result = await tester.TestAsync(ValidSettings());

        Assert.True(result.IsReachable);
        Assert.True(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
        Assert.Contains("database", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An undeterminable server version blocks migration compatibility.</summary>
    [Fact]
    public async Task Unknown_Server_Version_Blocks_Compatibility()
    {
        var probe = new RecordingProbe
        {
            Outcome = new MySqlProbeOutcome(true, true, null, true, null),
        };
        var tester = new MySqlConnectionTester(probe);

        DbConnectionTestResult result = await tester.TestAsync(ValidSettings());

        Assert.True(result.IsReachable);
        Assert.True(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
    }

    /// <summary>Servers below the MySQL 8.0 migration gate are incompatible.</summary>
    [Theory]
    [InlineData("5.7.44")]
    [InlineData("5.5.5-10.11.6-MariaDB")]
    [InlineData("8.0")]
    public async Task Below_Minimum_Version_Is_Incompatible(string serverVersion)
    {
        // "8.0" normalizes to 8.0.0 and is compatible; the other two are below the gate.
        var probe = new RecordingProbe { Outcome = MySqlProbeOutcome.Success(serverVersion) };
        var tester = new MySqlConnectionTester(probe);

        DbConnectionTestResult result = await tester.TestAsync(ValidSettings());

        Assert.True(result.IsReachable);
        Assert.True(result.AuthenticationOk);
        if (serverVersion == "8.0")
        {
            Assert.True(result.MigrationCompatible);
        }
        else
        {
            Assert.False(result.MigrationCompatible);
            Assert.Contains("minimum", result.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A healthy MySQL 8+ endpoint reports the full positive verdict.</summary>
    [Theory]
    [InlineData("8.0.36")]
    [InlineData("8.0.36-0ubuntu0.22.04.1")]
    [InlineData("8.4.0")]
    [InlineData("9.1.0")]
    public async Task Healthy_Endpoint_Reports_Full_Positive_Verdict(string serverVersion)
    {
        var probe = new RecordingProbe { Outcome = MySqlProbeOutcome.Success(serverVersion) };
        var tester = new MySqlConnectionTester(probe);

        DbConnectionTestResult result = await tester.TestAsync(ValidSettings());

        Assert.True(result.IsReachable);
        Assert.True(result.AuthenticationOk);
        Assert.True(result.MigrationCompatible);
        Assert.Contains("crawlforge", result.Message, StringComparison.Ordinal);
        Assert.Equal(ValidSettings().Host, probe.LastSettings!.Host);
    }

    /// <summary>Genuine caller cancellation propagates instead of becoming a verdict.</summary>
    [Fact]
    public async Task Cancellation_Propagates()
    {
        var probe = new RecordingProbe { ThrowOnProbe = new OperationCanceledException() };
        var tester = new MySqlConnectionTester(probe);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => tester.TestAsync(ValidSettings(), cts.Token));
    }

    /// <summary>Version banners parse to their leading numeric portion.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not-a-version", false)]
    [InlineData("8", true)]
    [InlineData("8.0", true)]
    [InlineData("8.0.36", true)]
    [InlineData("8.0.36-log", true)]
    [InlineData("5.5.5-10.11.6-MariaDB", true)]
    public void Server_Version_Banner_Parsing(string? banner, bool expectedParse)
    {
        bool parsed = MySqlConnectionTester.TryParseServerVersion(banner, out Version? version);

        Assert.Equal(expectedParse, parsed);
        if (parsed)
        {
            Assert.NotNull(version);
        }
    }

    /// <summary>Two-component banners normalize so 8.0 equals the 8.0.0 gate.</summary>
    [Fact]
    public void Two_Component_Version_Normalizes_To_Three()
    {
        Assert.True(MySqlConnectionTester.TryParseServerVersion("8.0", out Version? version));
        Assert.Equal(new Version(8, 0, 0), version);
        Assert.False(version < MySqlConnectionTester.MinimumServerVersion);
    }
}
