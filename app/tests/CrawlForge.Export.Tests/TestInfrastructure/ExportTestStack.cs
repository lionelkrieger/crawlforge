using System.Collections.Concurrent;
using System.Data.Common;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Contracts.Security;
using CrawlForge.Data.Migrations;
using CrawlForge.Data.Sqlite;
using CrawlForge.Export.Delivery;
using CrawlForge.Security.Secrets;

namespace CrawlForge.Export.Tests.TestInfrastructure;

/// <summary>
/// Real-stack fixture: temporary SQLite database migrated through the locked
/// SchemaMigrator, a real AppEntropySecretProtector over a local entropy key,
/// and a recording hub. No network is ever opened by the fixture.
/// </summary>
public sealed class ExportTestStack : IAsyncDisposable
{
    private readonly string _directory;

    /// <summary>The migrated database provider.</summary>
    public SqliteDbProvider Provider { get; }

    /// <summary>Real secret protector (local app-entropy key file).</summary>
    public AppEntropySecretProtector Protector { get; }

    /// <summary>Recording hub capturing every published event.</summary>
    public RecordingHub Hub { get; } = new();

    /// <summary>Creates the fixture and migrates the schema.</summary>
    public ExportTestStack()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cf-export-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Provider = new SqliteDbProvider(new SqliteProviderOptions { DatabasePath = Path.Combine(_directory, "test.db") });
        new SchemaMigrator(Provider).MigrateAsync().GetAwaiter().GetResult();
        Protector = new AppEntropySecretProtector(new EntropyKeyStore(Path.Combine(_directory, "entropy.key")));
    }

    /// <summary>Seeds project → configuration → audit; returns the audit id.</summary>
    /// <param name="extractionRulesJson">Optional custom extraction rules payload.</param>
    /// <returns>The audit identifier.</returns>
    public async Task<long> SeedAuditAsync(string? extractionRulesJson = null)
    {
        var projectId = await InsertAsync("INSERT INTO `projects` (`name`, `created_at`, `updated_at`, `is_deleted`) VALUES ('p', '2026-01-01 00:00:00', '2026-01-01 00:00:00', 0)");
        var configId = await InsertAsync(
            "INSERT INTO `crawl_configurations` (`project_id`, `name`, `seeds_json`, `scope_rules_json`, `limit_overrides_json`, `rendering_mode`, `custom_extraction_rules_json`, `created_at`, `updated_at`) VALUES (@p, 'c', '[]', '[]', NULL, 0, @rules, '2026-01-01 00:00:00', '2026-01-01 00:00:00')",
            ("@p", projectId), ("@rules", (object?)extractionRulesJson ?? DBNull.Value));
        return await InsertAsync(
            "INSERT INTO `audits` (`project_id`, `configuration_id`, `configuration_snapshot_json`, `status`, `phase`, `started_at`, `finished_at`, `counters_json`, `engine_version`, `is_deleted`) VALUES (@p, @c, '{}', 3, 4, '2026-01-01 00:00:00', '2026-01-01 01:00:00', '{}', 'test', 0)",
            ("@p", projectId), ("@c", configId));
    }

    /// <summary>Seeds one URL record.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="url">Canonical URL.</param>
    /// <param name="indexable">Indexability flag.</param>
    /// <param name="statusClass">Crawl status class.</param>
    /// <param name="path">URL path (sitemap home detection).</param>
    /// <returns>The URL record identifier.</returns>
    public Task<long> SeedUrlAsync(long auditId, string url, bool indexable = true, CrawlStatusClass statusClass = CrawlStatusClass.Success, string? path = null)
        => InsertAsync(
            "INSERT INTO `url_records` (`audit_id`, `canonical_url`, `host`, `path`, `depth`, `discovery_source`, `status_class`, `final_status_code`, `redirect_target_url`, `content_type`, `size_bytes`, `timing_json`, `robots_state`, `indexability_flags`, `is_indexable`) VALUES (@a, @u, 'example.com', @path, 0, 0, @sc, 200, NULL, 'text/html', 100, NULL, 1, 0, @idx)",
            ("@a", auditId), ("@u", url), ("@path", path ?? new Uri(url).AbsolutePath), ("@sc", (int)statusClass), ("@idx", indexable ? 1 : 0));

    /// <summary>Seeds one finding.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="ruleKey">Rule key.</param>
    /// <param name="severity">Severity.</param>
    /// <param name="urlId">Optional URL reference.</param>
    /// <param name="evidenceJson">Evidence payload.</param>
    /// <returns>The finding identifier.</returns>
    public Task<long> SeedFindingAsync(long auditId, string ruleKey, FindingSeverity severity, long? urlId = null, string evidenceJson = "{}")
        => InsertAsync(
            "INSERT INTO `findings` (`audit_id`, `rule_key`, `category`, `severity`, `url_id`, `evidence_json`, `recommendation_key`, `is_suppressed`) VALUES (@a, @r, 1, @s, @u, @e, NULL, 0)",
            ("@a", auditId), ("@r", ruleKey), ("@s", (int)severity), ("@u", (object?)urlId ?? DBNull.Value), ("@e", evidenceJson));

    /// <summary>Seeds one internal link edge.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="sourceUrlId">Source URL.</param>
    /// <param name="targetUrl">Canonical target.</param>
    /// <param name="anchorText">Anchor text.</param>
    /// <returns>The edge identifier.</returns>
    public Task<long> SeedLinkAsync(long auditId, long sourceUrlId, string targetUrl, string? anchorText)
        => InsertAsync(
            "INSERT INTO `link_edges` (`audit_id`, `source_url_id`, `canonical_target_url`, `target_url_id`, `anchor_text`, `relation_flags`, `is_internal`) VALUES (@a, @s, @t, NULL, @anchor, 0, 1)",
            ("@a", auditId), ("@s", sourceUrlId), ("@t", targetUrl), ("@anchor", (object?)anchorText ?? DBNull.Value));

    /// <summary>Seeds one metric summary.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="metricKey">Metric key.</param>
    /// <param name="value">Metric value.</param>
    /// <returns>The metric row identifier.</returns>
    public Task<long> SeedMetricAsync(long auditId, string metricKey, double value)
        => InsertAsync(
            "INSERT INTO `metric_summaries` (`audit_id`, `metric_key`, `dimensions_json`, `metric_value`) VALUES (@a, @k, '{}', @v)",
            ("@a", auditId), ("@k", metricKey), ("@v", value));

    /// <summary>Seeds one extracted value.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="urlId">URL reference.</param>
    /// <param name="ruleName">Rule name.</param>
    /// <param name="ordinal">Value ordinal.</param>
    /// <param name="valueJson">Typed payload (null = invalid).</param>
    /// <param name="valueType">Configured value type.</param>
    /// <param name="isInvalid">Invalid flag.</param>
    /// <returns>The value row identifier.</returns>
    public Task<long> SeedExtractedAsync(long auditId, long urlId, string ruleName, int ordinal, string? valueJson, ExtractedValueType valueType = ExtractedValueType.Text, bool isInvalid = false)
        => InsertAsync(
            "INSERT INTO `extracted_content` (`audit_id`, `url_id`, `rule_name`, `ordinal`, `typed_value_json`, `value_type`, `is_invalid`, `is_truncated`) VALUES (@a, @u, @r, @o, @v, @t, @i, 0)",
            ("@a", auditId), ("@u", urlId), ("@r", ruleName), ("@o", ordinal), ("@v", (object?)valueJson ?? DBNull.Value), ("@t", (int)valueType), ("@i", isInvalid ? 1 : 0));

    /// <summary>Seeds one snippet result.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="urlId">URL reference.</param>
    /// <param name="snippetKey">Snippet key.</param>
    /// <param name="columnsJson">Typed column definitions.</param>
    /// <param name="rowsJson">Row payload.</param>
    /// <returns>The snippet row identifier.</returns>
    public Task<long> SeedSnippetAsync(long auditId, long urlId, string snippetKey, string columnsJson, string rowsJson)
        => InsertAsync(
            "INSERT INTO `snippet_results` (`audit_id`, `url_id`, `snippet_key`, `columns_json`, `rows_json`) VALUES (@a, @u, @k, @c, @r)",
            ("@a", auditId), ("@u", urlId), ("@k", snippetKey), ("@c", columnsJson), ("@r", rowsJson));

    /// <summary>Seeds one export destination (never seeded by the app itself).</summary>
    /// <param name="class">Destination class.</param>
    /// <param name="endpointJson">Endpoint details.</param>
    /// <param name="encryptedCredentials">Protected credentials, or null.</param>
    /// <param name="enabled">Enabled flag.</param>
    /// <returns>The destination identifier.</returns>
    public Task<long> SeedDestinationAsync(ExportDestinationClass @class, string endpointJson, string? encryptedCredentials = null, bool enabled = true)
        => InsertAsync(
            "INSERT INTO `export_destinations` (`name`, `destination_class`, `endpoint_details_json`, `encrypted_credentials_json`, `is_enabled`) VALUES ('dest', @c, @e, @creds, @en)",
            ("@c", (int)@class), ("@e", endpointJson), ("@creds", (object?)encryptedCredentials ?? DBNull.Value), ("@en", enabled ? 1 : 0));

    /// <summary>Executes a non-query statement.</summary>
    /// <param name="sql">SQL text.</param>
    /// <param name="parameters">Named parameters.</param>
    /// <returns>A task that completes when executed.</returns>
    public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads a scalar value.</summary>
    /// <param name="sql">SQL text.</param>
    /// <param name="parameters">Named parameters.</param>
    /// <returns>The scalar result.</returns>
    public async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteScalarAsync();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private async Task<long> InsertAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql + "; SELECT last_insert_rowid();";
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
}

/// <summary>Recording hub double: captures every event, never opens sockets.</summary>
public sealed class RecordingHub : IRealtimeHub
{
    /// <summary>All published events in order.</summary>
    public ConcurrentQueue<HubEvent> Events { get; } = new();

    /// <inheritdoc/>
    public Task PublishAsync(HubEvent hubEvent, CancellationToken cancellationToken = default)
    {
        Events.Enqueue(hubEvent);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Recording HTTP handler double (criterion #14): captures every request so
/// tests can prove zero outbound traffic or user-endpoint-only transfers.
/// </summary>
public sealed class RecordingHttpHandler : HttpMessageHandler
{
    /// <summary>All requests in order.</summary>
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Response factory; defaults to 200 OK.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK);

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Task.FromResult(Responder(request));
    }
}

/// <summary>
/// Scripted transport double for state-machine tests: fails a configured
/// number of attempts, then succeeds; records every send.
/// </summary>
public sealed class ScriptedTransport : IExportTransport
{
    /// <summary>Number of leading send attempts that throw.</summary>
    public int FailuresBeforeSuccess { get; set; }

    /// <summary>Recorded send file paths.</summary>
    public ConcurrentQueue<string> Sends { get; } = new();

    /// <summary>Recorded test probes.</summary>
    public int TestCalls { get; private set; }

    /// <summary>Credentials received on the last call (decrypted at point of use).</summary>
    public DestinationCredentials? LastCredentials { get; private set; }

    /// <inheritdoc/>
    public Task<DestinationProbe> TestAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, CancellationToken cancellationToken)
    {
        TestCalls++;
        LastCredentials = credentials;
        return Task.FromResult(new DestinationProbe(true, true, "scripted"));
    }

    /// <inheritdoc/>
    public Task SendAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, string filePath, CancellationToken cancellationToken)
    {
        LastCredentials = credentials;
        Sends.Enqueue(filePath);
        if (Sends.Count <= FailuresBeforeSuccess)
        {
            throw new IOException($"scripted failure {Sends.Count}");
        }

        return Task.CompletedTask;
    }
}
