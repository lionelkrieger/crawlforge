using Xunit;

namespace CrawlForge.E2E.Tests.Fixtures;

/// <summary>
/// WP-08-A1: Fact that runs only when a live MySQL server is configured through
/// the environment; otherwise it is skipped gracefully (the build machine has
/// no MySQL server). Same convention as CrawlForge.Data.MySql.Tests
/// LiveServerFactAttribute: set CF_MYSQL_TEST_HOST, and optionally
/// CF_MYSQL_TEST_PORT, CF_MYSQL_TEST_USER, CF_MYSQL_TEST_PASSWORD. The dedicated
/// crawlforge_e2e database is always used (reset per test). The v1.1 sign-off
/// ran these facts against a live MySQL 8.0.28 at 127.0.0.1:3306; every
/// assertion is preserved unchanged when a server is configured.
/// </summary>
public sealed class LiveMySqlParityFactAttribute : FactAttribute
{
    /// <summary>Creates the fact, skipping it when no live server is configured.</summary>
    public LiveMySqlParityFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CF_MYSQL_TEST_HOST")))
        {
            Skip = "No live MySQL server configured. Set CF_MYSQL_TEST_HOST (optionally CF_MYSQL_TEST_PORT, CF_MYSQL_TEST_USER, CF_MYSQL_TEST_PASSWORD) to enable the MySQL parity E2E facts.";
        }
    }
}