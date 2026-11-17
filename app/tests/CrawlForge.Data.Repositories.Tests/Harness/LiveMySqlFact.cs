using System.Text.RegularExpressions;
using Xunit;

namespace CrawlForge.Data.Repositories.Tests.Harness;

/// <summary>
/// Fact that runs only when a live MySQL server is configured through
/// CF_MYSQL_TEST_HOST (optionally CF_MYSQL_TEST_PORT, CF_MYSQL_TEST_DATABASE,
/// CF_MYSQL_TEST_USER, CF_MYSQL_TEST_PASSWORD); otherwise skipped gracefully
/// (repo live-server convention).
/// </summary>
public sealed class LiveMySqlFactAttribute : FactAttribute
{
    /// <summary>Creates the fact, skipping when no live server is configured.</summary>
    public LiveMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CF_MYSQL_TEST_HOST")))
        {
            Skip = "No live MySQL server configured. Set CF_MYSQL_TEST_HOST (optionally CF_MYSQL_TEST_PORT, CF_MYSQL_TEST_DATABASE, CF_MYSQL_TEST_USER, CF_MYSQL_TEST_PASSWORD) to enable the D12 live archive-revert proof.";
        }
    }
}

/// <summary>Live-server configuration for the D12 scratch-database archive proof.</summary>
internal sealed record LiveMySqlSettings(string Host, int Port, string Database, string User, string Password)
{
    public string ServerConnectionString =>
        $"Server={Host};Port={Port};User ID={User};Password={Password};Pooling=false;ConnectionTimeout=10;DefaultCommandTimeout=120";

    public string DatabaseConnectionString =>
        $"Server={Host};Port={Port};Database={Database};User ID={User};Password={Password};Pooling=false;ConnectionTimeout=10;DefaultCommandTimeout=120";

    public static LiveMySqlSettings Create()
    {
        var host = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_HOST") ?? "127.0.0.1";
        var portText = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_PORT");
        var port = int.TryParse(portText, out var parsed) ? parsed : 3306;
        var database = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_DATABASE") ?? "crawlforge_d12";
        var user = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_USER") ?? "root";
        var password = Environment.GetEnvironmentVariable("CF_MYSQL_TEST_PASSWORD") ?? string.Empty;

        // The scratch database name is interpolated into DDL: restrict it to a
        // safe identifier alphabet (test hygiene, never user input in product).
        if (!Regex.IsMatch(database, "^[A-Za-z0-9_]+$"))
        {
            throw new InvalidOperationException($"Scratch database name '{database}' must match ^[A-Za-z0-9_]+$.");
        }

        return new LiveMySqlSettings(host, port, database, user, password);
    }
}
