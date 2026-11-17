using Xunit;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Fact that runs only when a live MySQL server is configured through environment
/// variables; otherwise it is skipped gracefully (the build machine has no MySQL
/// server). Set CF_MYSQL_TEST_HOST, and optionally CF_MYSQL_TEST_PORT,
/// CF_MYSQL_TEST_DATABASE, CF_MYSQL_TEST_USER, CF_MYSQL_TEST_PASSWORD.
/// </summary>
public sealed class LiveServerFactAttribute : FactAttribute
{
    /// <summary>Creates the fact, skipping it when no live server is configured.</summary>
    public LiveServerFactAttribute()
    {
        if (!LiveServerSettings.IsConfigured)
        {
            Skip = "No live MySQL server configured. Set CF_MYSQL_TEST_HOST (optionally CF_MYSQL_TEST_PORT, CF_MYSQL_TEST_DATABASE, CF_MYSQL_TEST_USER, CF_MYSQL_TEST_PASSWORD) to enable live-server facts.";
        }
    }
}

/// <summary>Reads the optional live-server configuration from environment variables.</summary>
internal static class LiveServerSettings
{
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CF_MYSQL_TEST_HOST"));

    public static MySqlDatabaseSettings Create()
    {
        string host = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_HOST") ?? "127.0.0.1";
        string? portText = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_PORT");
        int port = int.TryParse(portText, out int parsed) ? parsed : MySqlDatabaseSettings.DefaultPort;

        return new MySqlDatabaseSettings
        {
            Host = host,
            Port = port,
            Database = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_DATABASE") ?? "crawlforge_test",
            UserId = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_USER") ?? "root",
            Password = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_PASSWORD") ?? string.Empty,
            ConnectTimeoutSeconds = 10,
            CommandTimeoutSeconds = 30,
        };
    }
}
