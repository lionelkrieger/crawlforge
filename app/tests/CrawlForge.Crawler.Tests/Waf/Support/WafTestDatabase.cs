using CrawlForge.Crawler.Waf;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Sqlite;
using Xunit;

namespace CrawlForge.Crawler.Tests.Waf.Support;

/// <summary>
/// Per-test fixture over the real completed persistence stack: the phase-01
/// <c>SchemaMigrator</c> on a temporary <c>SqliteDbProvider</c> database plus a
/// <c>CompressedContentStore</c> for stored-body re-detection. Everything is
/// local; no test performs any network access.
/// </summary>
public sealed class WafTestDatabase : IAsyncLifetime
{
    private readonly string _rootDirectory;

    /// <summary>Creates the fixture paths (nothing is opened yet).</summary>
    public WafTestDatabase()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), "cf-wp-02-04-tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>The migrated embedded database provider.</summary>
    public SqliteDbProvider Provider { get; private set; } = null!;

    /// <summary>The compressed content store over the same database.</summary>
    public CompressedContentStore ContentStore { get; private set; } = null!;

    /// <summary>The effective signature catalogue (bundled asset + user entries).</summary>
    public WafSignatureCatalogue Catalogue { get; private set; } = null!;

    /// <summary>Local path of the bundled catalogue asset in the repository.</summary>
    public static string BundledAssetPath { get; } = ResolveBundledAssetPath();

    /// <summary>Creates the temporary database, applies all migrations, and opens the stores.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_rootDirectory);
        Provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(_rootDirectory, "crawlforge.db"),
        });
        var migrator = new SchemaMigrator(Provider);
        await migrator.MigrateAsync();
        ContentStore = new CompressedContentStore(Path.Combine(_rootDirectory, "content"), Provider);
        Catalogue = new WafSignatureCatalogue(Provider, BundledAssetPath);
    }

    /// <summary>Disposes the provider and removes the temporary directory.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public Task DisposeAsync()
    {
        Provider.Dispose();
        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the per-test temporary directory.
        }

        return Task.CompletedTask;
    }

    /// <summary>Inserts the parent graph: project 1, configuration 1, audit 1.</summary>
    /// <returns>A task that completes when the graph exists.</returns>
    public async Task InsertGraphAsync()
    {
        await ExecuteAsync(
            "INSERT INTO projects (id, name, created_at, updated_at, is_deleted) " +
            $"VALUES (1, 'Waf Test Project', '{Timestamp}', '{Timestamp}', 0);");
        await ExecuteAsync(
            "INSERT INTO crawl_configurations (id, project_id, name, seeds_json, rendering_mode, created_at, updated_at) " +
            $"VALUES (1, 1, 'Waf Test Configuration', '[\"https://example.com/\"]', 0, '{Timestamp}', '{Timestamp}');");
        await ExecuteAsync(
            "INSERT INTO audits (id, project_id, configuration_id, configuration_snapshot_json, status, phase, counters_json, engine_version, is_deleted) " +
            $"VALUES (1, 1, 1, '{{}}', 1, 1, '{{}}', '0.0.1', 0);");
    }

    /// <summary>Inserts a URL record under audit 1 with a success classification.</summary>
    /// <param name="id">URL surrogate key.</param>
    /// <param name="canonicalUrl">Canonical URL (unique within the audit).</param>
    /// <returns>A task that completes when the row exists.</returns>
    public Task InsertUrlAsync(long id, string canonicalUrl) =>
        ExecuteAsync(
            "INSERT INTO url_records (id, audit_id, canonical_url, host, path, depth, discovery_source, status_class, robots_state, indexability_flags, is_indexable) " +
            $"VALUES ({id}, 1, '{canonicalUrl}', 'example.com', '/page-{id}', 0, 0, 1, 1, 0, 1);");

    /// <summary>Opens a connection to the test database.</summary>
    /// <returns>An open connection owned by the caller.</returns>
    public Task<System.Data.Common.DbConnection> OpenAsync() => Provider.OpenConnectionAsync();

    /// <summary>Executes a non-query statement.</summary>
    /// <param name="sql">Statement text.</param>
    /// <returns>A task that completes when the statement ran.</returns>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Executes a scalar query.</summary>
    /// <param name="sql">Statement text.</param>
    /// <returns>The first column of the first row, or null.</returns>
    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    /// <summary>Counts rows of a table.</summary>
    /// <param name="tableName">Table name.</param>
    /// <returns>The row count.</returns>
    public async Task<long> CountRowsAsync(string tableName)
    {
        var value = await ScalarAsync($"SELECT COUNT(*) FROM {tableName};");
        return Convert.ToInt64(value);
    }

    /// <summary>Fixed UTC timestamp used by every fixture insert.</summary>
    public const string Timestamp = "2026-01-01 00:00:00.000000";

    private static string ResolveBundledAssetPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrawlForge.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Could not locate the repository root (CrawlForge.sln) from the test output directory.");
        }

        return Path.Combine(directory.FullName, "assets", "waf-signatures", "waf-catalogue.json");
    }
}

/// <summary>
/// Deterministic clock for timestamp assertions.
/// </summary>
public sealed class FixedTimeProvider : TimeProvider
{
    /// <summary>The fixed instant returned by every call.</summary>
    public static DateTimeOffset FixedInstant { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => FixedInstant;
}
