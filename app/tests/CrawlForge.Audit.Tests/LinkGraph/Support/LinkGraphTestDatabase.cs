using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Sqlite;

namespace CrawlForge.Audit.Tests.LinkGraph.Support;

/// <summary>
/// Real persistence stack for link-graph integration tests: embedded SQLite on a
/// temporary local file, migrated through the production <see cref="SchemaMigrator"/>
/// (migration authority — never DDL generated from the mapping under test).
/// </summary>
public sealed class LinkGraphTestDatabase : IAsyncDisposable
{
    private LinkGraphTestDatabase(SqliteDbProvider provider, string directory)
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
    public static async Task<LinkGraphTestDatabase> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cf-linkgraph-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var provider = new SqliteDbProvider(new SqliteProviderOptions
        {
            DatabasePath = Path.Combine(directory, "linkgraph.db")
        });
        var migrator = new SchemaMigrator(provider);
        await migrator.MigrateAsync();
        return new LinkGraphTestDatabase(provider, directory);
    }

    /// <summary>
    /// Seeds the FK parent chain (project → configuration → audit) for an explicit
    /// audit identifier. Idempotent.
    /// </summary>
    /// <param name="auditId">The audit identifier to make available.</param>
    /// <returns>A task that completes when the chain is seeded.</returns>
    public async Task SeedAuditAsync(long auditId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await Provider.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO projects (id, name, created_at, updated_at, is_deleted) VALUES (1, 'linkgraph-test', @now, @now, 0)",
            ("@now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO crawl_configurations (id, project_id, name, seeds_json, rendering_mode, created_at, updated_at) VALUES (1, 1, 'linkgraph-test', '[]', 0, @now, @now)",
            ("@now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO audits (id, project_id, configuration_id, configuration_snapshot_json, status, phase, counters_json, engine_version, is_deleted) VALUES (@id, 1, 1, '{}', 1, 1, '{}', 'test', 0)",
            ("@id", auditId));
        await transaction.CommitAsync();
    }

    /// <summary>Seeds a url_records row and returns its identifier.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="canonicalUrl">Canonical URL key.</param>
    /// <param name="depth">Crawl depth.</param>
    /// <param name="source">Discovery source.</param>
    /// <returns>The new url_records identifier.</returns>
    public async Task<long> InsertUrlAsync(long auditId, string canonicalUrl, int depth = 0, DiscoverySource source = DiscoverySource.Link)
    {
        var uri = new Uri(canonicalUrl);
        var repository = new SqlRepository<UrlRecordDto>(Provider);
        var record = new UrlRecordDto(
            0, auditId, canonicalUrl, uri.Host, uri.AbsolutePath, depth,
            source, CrawlStatusClass.NotCrawled,
            null, null, null, null, null,
            RobotsState.Unknown, IndexabilityFlags.None, false);
        return (await repository.CreateAsync(record)).Id;
    }

    /// <summary>Seeds a link_edges row (additive zone fields included) and returns its identifier.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="sourceUrlId">Source URL reference.</param>
    /// <param name="targetUrlId">Target URL reference; null for external targets.</param>
    /// <param name="canonicalTargetUrl">Canonical target URL.</param>
    /// <param name="zone">Classified DOM zone, when present.</param>
    /// <param name="zoneEvidence">Zone evidence, when present.</param>
    /// <param name="flags">Relation flags.</param>
    /// <param name="isInternal">Internal/external marker.</param>
    /// <param name="weightedEquity">Per-edge weighted equity, when present.</param>
    /// <returns>The new link_edges identifier.</returns>
    public async Task<long> InsertEdgeAsync(
        long auditId,
        long sourceUrlId,
        long? targetUrlId,
        string canonicalTargetUrl,
        DomZone? zone = null,
        string? zoneEvidence = null,
        LinkRelationFlags flags = LinkRelationFlags.None,
        bool isInternal = true,
        double? weightedEquity = null)
    {
        var repository = new SqlRepository<LinkEdgeDto>(Provider);
        var edge = new LinkEdgeDto(0, auditId, sourceUrlId, canonicalTargetUrl, targetUrlId, "anchor", flags, isInternal)
        {
            DomZone = zone,
            ZoneEvidence = zoneEvidence,
            WeightedEquity = weightedEquity
        };
        return (await repository.CreateAsync(edge)).Id;
    }

    /// <summary>Seeds a metric_summaries row and returns its identifier.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="metricKey">Metric key.</param>
    /// <param name="dimensionsJson">Dimensions payload.</param>
    /// <param name="value">Numeric value.</param>
    /// <returns>The new metric_summaries identifier.</returns>
    public async Task<long> InsertMetricAsync(long auditId, string metricKey, string dimensionsJson, double value)
    {
        var repository = new SqlRepository<MetricSummaryDto>(Provider);
        return (await repository.CreateAsync(new MetricSummaryDto(0, auditId, metricKey, dimensionsJson, value))).Id;
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
