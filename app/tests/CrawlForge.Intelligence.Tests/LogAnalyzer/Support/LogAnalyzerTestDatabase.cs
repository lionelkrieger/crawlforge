using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;

namespace CrawlForge.Intelligence.Tests.LogAnalyzer.Support;

/// <summary>
/// Real persistence stack for log-analyzer integration tests: embedded SQLite
/// on a temporary local file, migrated through the production SchemaMigrator
/// (migration authority). Local-only, no network.
/// </summary>
public sealed class LogAnalyzerTestDatabase : IAsyncDisposable
{
    private LogAnalyzerTestDatabase(SqliteDbProvider provider, string directory)
    {
        Provider = provider;
        DirectoryPath = directory;
    }

    /// <summary>The migrated SQLite provider.</summary>
    public SqliteDbProvider Provider { get; }

    /// <summary>Temporary local directory holding the database file.</summary>
    public string DirectoryPath { get; }

    /// <summary>Creates a fresh migrated database in a unique temporary directory.</summary>
    /// <returns>The initialized test database.</returns>
    public static async Task<LogAnalyzerTestDatabase> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cf-loganalyzer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(directory, "loganalyzer.db")
        });
        var migrator = new SchemaMigrator(provider);
        await migrator.MigrateAsync();
        return new LogAnalyzerTestDatabase(provider, directory);
    }

    /// <summary>Seeds the project → configuration → audit FK chain. Idempotent.</summary>
    /// <param name="projectId">Project identifier.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="auditStatus">Audit lifecycle status (default Running=1).</param>
    /// <returns>A task that completes when the chain is seeded.</returns>
    public async Task SeedProjectAndAuditAsync(long projectId, long auditId, int auditStatus = 1)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await Provider.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO projects (id, name, created_at, updated_at, is_deleted) VALUES (@id, 'log-test', @now, @now, 0)",
            ("@id", projectId), ("@now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO crawl_configurations (id, project_id, name, seeds_json, rendering_mode, created_at, updated_at) VALUES (@id, @project, 'log-test', '[]', 0, @now, @now)",
            ("@id", projectId), ("@project", projectId), ("@now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO audits (id, project_id, configuration_id, configuration_snapshot_json, status, phase, counters_json, engine_version, is_deleted) VALUES (@id, @project, @config, '{}', @status, 1, '{}', 'test', 0)",
            ("@id", auditId), ("@project", projectId), ("@config", projectId), ("@status", auditStatus));
        await transaction.CommitAsync();
    }

    /// <summary>Seeds only a project (no audit). Idempotent.</summary>
    /// <param name="projectId">Project identifier.</param>
    /// <returns>A task that completes when the project is seeded.</returns>
    public async Task SeedProjectAsync(long projectId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR IGNORE INTO projects (id, name, created_at, updated_at, is_deleted) VALUES (@id, 'log-test', @now, @now, 0)";
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "@id";
        idParameter.Value = projectId;
        command.Parameters.Add(idParameter);
        var nowParameter = command.CreateParameter();
        nowParameter.ParameterName = "@now";
        nowParameter.Value = now;
        command.Parameters.Add(nowParameter);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds a url_records row and returns its identifier.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="canonicalUrl">Canonical URL key.</param>
    /// <param name="depth">Crawl depth.</param>
    /// <param name="isIndexable">Indexability marker.</param>
    /// <param name="statusClass">Crawl status class.</param>
    /// <returns>The new url_records identifier.</returns>
    public async Task<long> InsertUrlAsync(
        long auditId,
        string canonicalUrl,
        int depth = 1,
        bool isIndexable = true,
        CrawlStatusClass statusClass = CrawlStatusClass.Success)
    {
        var uri = new Uri(canonicalUrl);
        var repository = new SqlRepository<UrlRecordDto>(Provider);
        var record = new UrlRecordDto(
            0, auditId, canonicalUrl, uri.Host, uri.AbsolutePath, depth,
            DiscoverySource.Link, statusClass,
            statusClass == CrawlStatusClass.Success ? 200 : 404,
            null, null, null, null,
            RobotsState.Allowed, IndexabilityFlags.None, isIndexable);
        return (await repository.CreateAsync(record)).Id;
    }

    /// <summary>Seeds an internal link_edges row.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="sourceUrlId">Source URL reference.</param>
    /// <param name="targetUrlId">Target URL reference.</param>
    /// <param name="canonicalTargetUrl">Canonical target URL.</param>
    /// <returns>The new link_edges identifier.</returns>
    public async Task<long> InsertEdgeAsync(long auditId, long sourceUrlId, long targetUrlId, string canonicalTargetUrl)
    {
        var repository = new SqlRepository<LinkEdgeDto>(Provider);
        var edge = new LinkEdgeDto(0, auditId, sourceUrlId, canonicalTargetUrl, targetUrlId, "anchor", LinkRelationFlags.None, true);
        return (await repository.CreateAsync(edge)).Id;
    }

    /// <summary>Writes a local log file into the test directory.</summary>
    /// <param name="name">File name.</param>
    /// <param name="content">File content.</param>
    /// <returns>The full local path.</returns>
    public string WriteLogFile(string name, string content)
    {
        var path = Path.Combine(DirectoryPath, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Writes gzip-compressed local log content.</summary>
    /// <param name="name">File name.</param>
    /// <param name="content">Uncompressed content.</param>
    /// <returns>The full local path.</returns>
    public string WriteGzipLogFile(string name, string content)
    {
        var path = Path.Combine(DirectoryPath, name);
        using var file = File.Create(path);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal);
        using var writer = new StreamWriter(gzip);
        writer.Write(content);
        return path;
    }

    private static async Task ExecuteAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Provider.Dispose();
        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temporary directory.
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Locates the bundled bot catalogue asset from the test assembly location
/// (local disk only).
/// </summary>
public static class TestAssets
{
    /// <summary>Path to the bundled bot-catalogue.json.</summary>
    /// <returns>The asset path.</returns>
    /// <exception cref="FileNotFoundException">The asset cannot be located.</exception>
    public static string BotCataloguePath() =>
        CrawlForge.Intelligence.LogAnalyzer.BotCatalogue.FindDefaultAssetPath()
        ?? throw new FileNotFoundException("The bundled bot catalogue asset was not found.");
}
