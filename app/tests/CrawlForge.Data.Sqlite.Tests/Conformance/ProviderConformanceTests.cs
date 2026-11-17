using System.Globalization;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Sqlite.Extensions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CrawlForge.Data.Sqlite.Tests.Conformance;

/// <summary>
/// Provider conformance suite: the logical case set shared with the MySQL provider
/// (precedence rule 6). Every case runs against a private temporary embedded file —
/// no external services, fully offline. Case ids match <see cref="ConformanceCaseCatalog"/>.
/// </summary>
public class ProviderConformanceTests
{
    [Fact(DisplayName = "CONF-01: Provider identity and dialect recorded in the migration ledger")]
    public async Task Conf01ProviderIdentity()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        Assert.Equal(DatabaseProviderKind.Sqlite, provider.Kind);
        Assert.Equal("sqlite", provider.Dialect);

        await using var connection = await provider.OpenConnectionAsync();
        await TestSql.ExecuteAsync(connection, "CREATE TABLE schema_migrations (id INTEGER PRIMARY KEY, version INTEGER NOT NULL, provider_dialect TEXT NOT NULL, applied_at TEXT NOT NULL)");
        await TestSql.ExecuteWithParameterAsync(connection, "INSERT INTO schema_migrations (version, provider_dialect, applied_at) VALUES (1, $dialect, '2026-11-14T00:00:00.0000000+00:00')", "$dialect", provider.Dialect);
        var recorded = await TestSql.ScalarAsync<string>(connection, "SELECT provider_dialect FROM schema_migrations WHERE version = 1");
        Assert.Equal(provider.Dialect, recorded);
    }

    [Fact(DisplayName = "CONF-02: Connection open, basic query, file created")]
    public async Task Conf02ConnectionOpen()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        var value = await TestSql.ScalarAsync<long>(connection, "SELECT 1");

        Assert.Equal(1L, value);
        Assert.True(File.Exists(temp.DatabasePath));
    }

    [Fact(DisplayName = "CONF-03: Write connections use WAL journal mode")]
    public async Task Conf03WalJournalMode()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        var journalMode = await TestSql.ScalarAsync<string>(connection, "PRAGMA journal_mode");

        Assert.Equal("wal", journalMode, StringComparer.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "CONF-04: Synchronous level matches the WAL-safe default profile")]
    public async Task Conf04SynchronousProfile()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        var synchronous = await TestSql.ScalarAsync<long>(connection, "PRAGMA synchronous");

        Assert.Equal(1L, synchronous); // NORMAL: the WAL-safe default.
    }

    [Fact(DisplayName = "CONF-05: Busy timeout matches the configured value")]
    public async Task Conf05BusyTimeout()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        var busyTimeout = await TestSql.ScalarAsync<long>(connection, "PRAGMA busy_timeout");

        Assert.Equal(5000L, busyTimeout);
    }

    [Fact(DisplayName = "CONF-06: Foreign keys are enforced")]
    public async Task Conf06ForeignKeysEnforced()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        var foreignKeys = await TestSql.ScalarAsync<long>(connection, "PRAGMA foreign_keys");
        Assert.Equal(1L, foreignKeys);

        await TestSql.ExecuteAsync(connection, "CREATE TABLE parent (id INTEGER PRIMARY KEY)");
        await TestSql.ExecuteAsync(connection, "CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent(id))");
        await TestSql.ExecuteAsync(connection, "INSERT INTO parent (id) VALUES (1)");
        await TestSql.ExecuteAsync(connection, "INSERT INTO child (id, parent_id) VALUES (1, 1)");

        await Assert.ThrowsAsync<SqliteException>(() =>
            TestSql.ExecuteAsync(connection, "INSERT INTO child (id, parent_id) VALUES (2, 999)"));
    }

    [Fact(DisplayName = "CONF-07: Multi-row writes are transactional")]
    public async Task Conf07TransactionalMultiRowWrites()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        await TestSql.ExecuteAsync(connection, "CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");

        await using (var rollbackTransaction = connection.BeginTransaction())
        {
            await TestSql.ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('a')");
            await TestSql.ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('b')");
            await rollbackTransaction.RollbackAsync();
        }

        var afterRollback = await TestSql.ScalarAsync<long>(connection, "SELECT COUNT(*) FROM items");
        Assert.Equal(0L, afterRollback);

        await using (var commitTransaction = connection.BeginTransaction())
        {
            await TestSql.ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('a')");
            await TestSql.ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('b')");
            await commitTransaction.CommitAsync();
        }

        var afterCommit = await TestSql.ScalarAsync<long>(connection, "SELECT COUNT(*) FROM items");
        Assert.Equal(2L, afterCommit);
    }

    [Fact(DisplayName = "CONF-08: Read connections reject writes and DDL")]
    public async Task Conf08ReadConnectionsRejectWrites()
    {
        using var temp = new TempDatabase();
        using var factory = temp.CreateFactory();

        await using (var writer = await factory.OpenWriteConnectionAsync())
        {
            await TestSql.ExecuteAsync(writer, "CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
            await TestSql.ExecuteAsync(writer, "INSERT INTO items (label) VALUES ('seed')");
        }

        await using var reader = await factory.OpenReadConnectionAsync();
        var count = await TestSql.ScalarAsync<long>(reader, "SELECT COUNT(*) FROM items");
        Assert.Equal(1L, count);

        await Assert.ThrowsAsync<SqliteException>(() =>
            TestSql.ExecuteAsync(reader, "INSERT INTO items (label) VALUES ('illegal')"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            TestSql.ExecuteAsync(reader, "UPDATE items SET label = 'illegal'"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            TestSql.ExecuteAsync(reader, "DELETE FROM items"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            TestSql.ExecuteAsync(reader, "CREATE TABLE illegal (id INTEGER)"));
    }

    [Fact(DisplayName = "CONF-09: Single-writer discipline serializes concurrent write leases")]
    public async Task Conf09SingleWriterDiscipline()
    {
        using var temp = new TempDatabase();
        using var factory = temp.CreateFactory();

        await using (var setup = await factory.OpenWriteConnectionAsync())
        {
            await TestSql.ExecuteAsync(setup, "CREATE TABLE writes (id INTEGER PRIMARY KEY)");
        }

        var active = 0;
        var maxActive = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var writer = await factory.OpenWriteConnectionAsync();
            var now = Interlocked.Increment(ref active);
            var observed = Volatile.Read(ref maxActive);
            while (now > observed)
            {
                var previous = Interlocked.CompareExchange(ref maxActive, now, observed);
                if (previous == observed)
                {
                    break;
                }

                observed = previous;
            }

            await TestSql.ExecuteAsync(writer, "INSERT INTO writes DEFAULT VALUES");
            await Task.Delay(25);
            Interlocked.Decrement(ref active);
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, Volatile.Read(ref maxActive));
        await using var verify = await factory.OpenReadConnectionAsync();
        var rowCount = await TestSql.ScalarAsync<long>(verify, "SELECT COUNT(*) FROM writes");
        Assert.Equal(8L, rowCount);
    }

    [Fact(DisplayName = "CONF-10: Concurrent readers succeed while a write lease is held")]
    public async Task Conf10ConcurrentReadersDuringWrite()
    {
        using var temp = new TempDatabase();
        using var factory = temp.CreateFactory();

        await using var writer = await factory.OpenWriteConnectionAsync();
        await TestSql.ExecuteAsync(writer, "CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
        await TestSql.ExecuteAsync(writer, "INSERT INTO items (label) VALUES ('one')");
        await TestSql.ExecuteAsync(writer, "INSERT INTO items (label) VALUES ('two')");

        var readTasks = Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var reader = await factory.OpenReadConnectionAsync();
            return await TestSql.ScalarAsync<long>(reader, "SELECT COUNT(*) FROM items");
        }).ToArray();

        var counts = await Task.WhenAll(readTasks);

        Assert.All(counts, count => Assert.Equal(2L, count));
    }

    [Fact(DisplayName = "CONF-11: Regular-expression predicates in queries")]
    public async Task Conf11RegexPredicates()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        await TestSql.ExecuteAsync(connection, "CREATE TABLE urls (id INTEGER PRIMARY KEY, url TEXT NOT NULL)");
        await TestSql.ExecuteAsync(connection, "INSERT INTO urls (url) VALUES ('https://example.com/a'), ('http://example.com/b'), ('https://other.org/c')");

        var httpsCount = await TestSql.ScalarAsync<long>(connection, "SELECT COUNT(*) FROM urls WHERE url REGEXP '^https://'");
        Assert.Equal(2L, httpsCount);

        var replaced = await TestSql.ScalarAsync<string>(connection, "SELECT regexp_replace('a1b22c', '[0-9]+', '#')");
        Assert.Equal("a#b#c", replaced);

        var extracted = await TestSql.ScalarAsync<string>(connection, "SELECT regexp_extract('key=value', '^([a-z]+)=', 1)");
        Assert.Equal("key", extracted);
    }

    [Fact(DisplayName = "CONF-12: In-database delimited-text import")]
    public async Task Conf12DelimitedTextImport()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();

        var field = await TestSql.ScalarAsync<string>(connection, "SELECT split_part('a|b|c', '|', 2)");
        Assert.Equal("b", field);

        var fieldCount = await TestSql.ScalarAsync<long>(connection, "SELECT split_count('x,y,z', ',')");
        Assert.Equal(3L, fieldCount);

        // In-database row import: the engine's native json_each expands the delimited payload.
        await TestSql.ExecuteAsync(connection, "CREATE TABLE imported (ordinal INTEGER NOT NULL, value TEXT NOT NULL)");
        await TestSql.ExecuteAsync(connection, "INSERT INTO imported (ordinal, value) SELECT key + 1, value FROM json_each(delimited_to_json('x,y,z', ','))");

        var rowCount = await TestSql.ScalarAsync<long>(connection, "SELECT COUNT(*) FROM imported");
        Assert.Equal(3L, rowCount);

        var second = await TestSql.ScalarAsync<string>(connection, "SELECT value FROM imported WHERE ordinal = 2");
        Assert.Equal("y", second);
    }

    [Fact(DisplayName = "CONF-13: In-database delimited-text export with quoting")]
    public async Task Conf13DelimitedTextExport()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();

        var csvLine = await TestSql.ScalarAsync<string>(connection, "SELECT csv_agg(column1) FROM (VALUES ('plain'), ('has,comma'), ('has\"quote'))");
        Assert.Equal("plain,\"has,comma\",\"has\"\"quote\"", csvLine);

        var customLine = await TestSql.ScalarAsync<string>(connection, "SELECT csv_agg(column1, '|') FROM (VALUES ('a'), ('b|c'))");
        Assert.Equal("a|\"b|c\"", customLine);

        var quoted = await TestSql.ScalarAsync<string>(connection, "SELECT csv_quote('a,b')");
        Assert.Equal("\"a,b\"", quoted);
    }

    [Fact(DisplayName = "CONF-14: Vector-similarity search over stored embeddings")]
    public async Task Conf14VectorSimilarity()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        await TestSql.ExecuteAsync(connection, "CREATE TABLE url_embeddings (id INTEGER PRIMARY KEY, embedding BLOB NOT NULL)");

        float[] near = [1f, 0f, 0f];
        float[] far = [0f, 1f, 0f];
        float[] query = [0.9f, 0.1f, 0f];
        await TestSql.ExecuteWithParameterAsync(connection, "INSERT INTO url_embeddings (id, embedding) VALUES (1, $vec)", "$vec", TestVectors.ToBlob(near));
        await TestSql.ExecuteWithParameterAsync(connection, "INSERT INTO url_embeddings (id, embedding) VALUES (2, $vec)", "$vec", TestVectors.ToBlob(far));

        // JSON encoding is accepted alongside the compact float32 BLOB encoding.
        var cosineJson = await TestSql.ScalarAsync<double>(connection, "SELECT vec_cosine('[1,0]', '[0,1]')");
        Assert.Equal(0.0, cosineJson, 10);

        var cosineSame = await TestSql.ScalarAsync<double>(connection, "SELECT vec_cosine('[1,2,3]', '[1,2,3]')");
        Assert.Equal(1.0, cosineSame, 10);

        var dot = await TestSql.ScalarAsync<double>(connection, "SELECT vec_dot('[1,2]', '[3,4]')");
        Assert.Equal(11.0, dot, 10);

        var dimension = await TestSql.ScalarAsync<long>(connection, "SELECT vec_dim('[1,2,3]')");
        Assert.Equal(3L, dimension);

        var norm = await TestSql.ScalarAsync<double>(connection, "SELECT vec_norm('[3,4]')");
        Assert.Equal(5.0, norm, 10);

        // Nearest-neighbor search over stored embeddings, ordered by L2 distance.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM url_embeddings ORDER BY vec_distance(embedding, $query) LIMIT 1";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$query";
        parameter.Value = TestVectors.ToBlob(query);
        command.Parameters.Add(parameter);
        var nearest = (long)(await command.ExecuteScalarAsync())!;
        Assert.Equal(1L, nearest);
    }

    [Fact(DisplayName = "CONF-15: Extended mathematical and statistical functions")]
    public async Task Conf15MathStatistics()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();

        var median = await TestSql.ScalarAsync<double>(connection, "SELECT median(column1) FROM (VALUES (1.0), (2.0), (3.0), (4.0), (5.0))");
        Assert.Equal(3.0, median, 10);

        var sampleVariance = await TestSql.ScalarAsync<double>(connection, "SELECT variance(column1) FROM (VALUES (1.0), (2.0), (3.0), (4.0), (5.0))");
        Assert.Equal(2.5, sampleVariance, 10);

        var sampleStdDev = await TestSql.ScalarAsync<double>(connection, "SELECT stddev(column1) FROM (VALUES (1.0), (2.0), (3.0), (4.0), (5.0))");
        Assert.Equal(Math.Sqrt(2.5), sampleStdDev, 10);

        var populationVariance = await TestSql.ScalarAsync<double>(connection, "SELECT var_pop(column1) FROM (VALUES (1.0), (2.0), (3.0), (4.0), (5.0))");
        Assert.Equal(2.0, populationVariance, 10);

        var quartile = await TestSql.ScalarAsync<double>(connection, "SELECT percentile(column1, 0.25) FROM (VALUES (1.0), (2.0), (3.0), (4.0), (5.0))");
        Assert.Equal(2.0, quartile, 10);

        // Native scalar math supplied by the bundled engine.
        var squareRoot = await TestSql.ScalarAsync<double>(connection, "SELECT sqrt(16)");
        Assert.Equal(4.0, squareRoot, 10);

        var naturalLog = await TestSql.ScalarAsync<double>(connection, "SELECT ln(2.718281828459045)");
        Assert.Equal(1.0, naturalLog, 10);
    }

    [Fact(DisplayName = "CONF-16: Capability probe reports all four families available")]
    public async Task Conf16CapabilityProbe()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        var report = await provider.GetCapabilitiesAsync();

        Assert.True(report.AllAvailable);
        Assert.Equal(4, report.Capabilities.Count);
        Assert.True(report.GetStatus(SqliteExtensionFamily.Regex).Available);
        Assert.True(report.GetStatus(SqliteExtensionFamily.DelimitedText).Available);
        Assert.True(report.GetStatus(SqliteExtensionFamily.VectorSimilarity).Available);
        Assert.True(report.GetStatus(SqliteExtensionFamily.MathStatistics).Available);
    }

    [Fact(DisplayName = "CONF-17: Fresh database without migration ledger is compatible")]
    public async Task Conf17FreshDatabaseCompatible()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        var result = await provider.TestConnectionAsync();

        Assert.True(result.IsReachable);
        Assert.True(result.AuthenticationOk);
        Assert.True(result.MigrationCompatible);
        Assert.NotNull(result.Message);
        Assert.Contains("no migration ledger", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "CONF-18: Ledger carrying this dialect is compatible")]
    public async Task Conf18SameDialectLedgerCompatible()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using (var connection = await provider.OpenConnectionAsync())
        {
            await TestSql.ExecuteAsync(connection, "CREATE TABLE schema_migrations (id INTEGER PRIMARY KEY, version INTEGER NOT NULL UNIQUE, provider_dialect TEXT NOT NULL, applied_at TEXT NOT NULL)");
            await TestSql.ExecuteWithParameterAsync(connection, "INSERT INTO schema_migrations (version, provider_dialect, applied_at) VALUES (1, $dialect, '2026-11-14T00:00:00.0000000+00:00')", "$dialect", provider.Dialect);
        }

        var result = await provider.TestConnectionAsync();

        Assert.True(result.IsReachable);
        Assert.True(result.AuthenticationOk);
        Assert.True(result.MigrationCompatible);
        Assert.Contains("1 applied version", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "CONF-19: Ledger carrying a foreign dialect is incompatible")]
    public async Task Conf19ForeignDialectLedgerIncompatible()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using (var connection = await provider.OpenConnectionAsync())
        {
            await TestSql.ExecuteAsync(connection, "CREATE TABLE schema_migrations (id INTEGER PRIMARY KEY, version INTEGER NOT NULL UNIQUE, provider_dialect TEXT NOT NULL, applied_at TEXT NOT NULL)");
            await TestSql.ExecuteAsync(connection, "INSERT INTO schema_migrations (version, provider_dialect, applied_at) VALUES (1, 'mysql', '2026-11-14T00:00:00.0000000+00:00')");
        }

        var result = await provider.TestConnectionAsync();

        Assert.True(result.IsReachable);
        Assert.True(result.AuthenticationOk);
        Assert.False(result.MigrationCompatible);
        Assert.Contains("mysql", result.Message, StringComparison.Ordinal);
        Assert.Contains("archive", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "CONF-20: Connection test verdict flags for success and unreachable paths")]
    public async Task Conf20ConnectionTestVerdicts()
    {
        using var temp = new TempDatabase();
        using (var provider = temp.CreateProvider())
        {
            var success = await provider.TestConnectionAsync();
            Assert.True(success.IsReachable);
            Assert.True(success.AuthenticationOk);
            Assert.True(success.MigrationCompatible);
            Assert.NotNull(success.Message);
        }

        // A file blocking the parent directory makes the database unreachable.
        var blockerPath = Path.Combine(temp.DirectoryPath, "blocker");
        Directory.CreateDirectory(temp.DirectoryPath);
        File.WriteAllText(blockerPath, "not a directory");
        var unreachableOptions = new SqliteProviderOptions { DatabasePath = Path.Combine(blockerPath, "nested.db") };
        using var unreachableProvider = new SqliteDbProvider(unreachableOptions);

        var failure = await unreachableProvider.TestConnectionAsync();

        Assert.False(failure.IsReachable);
        Assert.False(failure.AuthenticationOk);
        Assert.False(failure.MigrationCompatible);
        Assert.NotNull(failure.Message);
    }

    [Fact(DisplayName = "CONF-21: Surrogate integer keys auto-assign")]
    public async Task Conf21SurrogateIntegerKeys()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        await TestSql.ExecuteAsync(connection, "CREATE TABLE projects (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        await TestSql.ExecuteAsync(connection, "INSERT INTO projects (name) VALUES ('first')");
        await TestSql.ExecuteAsync(connection, "INSERT INTO projects (name) VALUES ('second')");
        await TestSql.ExecuteAsync(connection, "INSERT INTO projects (name) VALUES ('third')");

        var ids = new List<long>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM projects ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        Assert.Equal([1L, 2L, 3L], ids);
    }

    [Fact(DisplayName = "CONF-22: UTC timestamps round-trip without drift")]
    public async Task Conf22UtcTimestamps()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        await TestSql.ExecuteAsync(connection, "CREATE TABLE audits (id INTEGER PRIMARY KEY, started_at TEXT NOT NULL)");

        var instant = new DateTimeOffset(2026, 11, 14, 7, 30, 11, TimeSpan.Zero);
        var text = instant.ToString("O", CultureInfo.InvariantCulture);
        await TestSql.ExecuteWithParameterAsync(connection, "INSERT INTO audits (started_at) VALUES ($stamp)", "$stamp", text);

        var readBack = await TestSql.ScalarAsync<string>(connection, "SELECT started_at FROM audits WHERE id = 1");
        var roundTrip = DateTimeOffset.Parse(readBack, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        Assert.Equal(instant, roundTrip);
        Assert.Equal(TimeSpan.Zero, roundTrip.Offset);
    }

    [Fact(DisplayName = "CONF-23: JSON text payload columns round-trip and are queryable")]
    public async Task Conf23JsonPayloadColumns()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        await TestSql.ExecuteAsync(connection, "CREATE TABLE metric_summaries (id INTEGER PRIMARY KEY, dimensions_json TEXT NOT NULL)");

        const string payload = "{\"status_class\":\"2xx\",\"count\":42}";
        await TestSql.ExecuteWithParameterAsync(connection, "INSERT INTO metric_summaries (dimensions_json) VALUES ($json)", "$json", payload);

        var readBack = await TestSql.ScalarAsync<string>(connection, "SELECT dimensions_json FROM metric_summaries WHERE id = 1");
        Assert.Equal(payload, readBack);

        var statusClass = await TestSql.ScalarAsync<string>(connection, "SELECT json_extract(dimensions_json, '$.status_class') FROM metric_summaries WHERE id = 1");
        Assert.Equal("2xx", statusClass);

        var count = await TestSql.ScalarAsync<long>(connection, "SELECT json_extract(dimensions_json, '$.count') FROM metric_summaries WHERE id = 1");
        Assert.Equal(42L, count);
    }

    [Fact(DisplayName = "CONF-24: Soft delete with transactional pruning")]
    public async Task Conf24SoftDeleteAndPruning()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        await TestSql.ExecuteAsync(connection, "CREATE TABLE projects (id INTEGER PRIMARY KEY, name TEXT NOT NULL, is_deleted INTEGER NOT NULL DEFAULT 0)");
        await TestSql.ExecuteAsync(connection, "INSERT INTO projects (name) VALUES ('kept')");
        await TestSql.ExecuteAsync(connection, "INSERT INTO projects (name) VALUES ('removed')");
        await TestSql.ExecuteAsync(connection, "UPDATE projects SET is_deleted = 1 WHERE name = 'removed'");

        var visible = await TestSql.ScalarAsync<long>(connection, "SELECT COUNT(*) FROM projects WHERE is_deleted = 0");
        Assert.Equal(1L, visible);

        await using (var transaction = connection.BeginTransaction())
        {
            await TestSql.ExecuteAsync(connection, "DELETE FROM projects WHERE is_deleted = 1");
            await transaction.CommitAsync();
        }

        var total = await TestSql.ScalarAsync<long>(connection, "SELECT COUNT(*) FROM projects");
        Assert.Equal(1L, total);
    }

    [Fact(DisplayName = "CONF-25: Unique (audit, canonical URL) index rejects duplicates")]
    public async Task Conf25UniqueAuditUrlIndex()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using var connection = await provider.OpenConnectionAsync();
        await TestSql.ExecuteAsync(connection, "CREATE TABLE url_records (id INTEGER PRIMARY KEY, audit_id INTEGER NOT NULL, canonical_url TEXT NOT NULL)");
        await TestSql.ExecuteAsync(connection, "CREATE UNIQUE INDEX ux_url_records_audit_url ON url_records (audit_id, canonical_url)");
        await TestSql.ExecuteAsync(connection, "INSERT INTO url_records (audit_id, canonical_url) VALUES (1, 'https://example.com/')");

        // Same URL in another audit is allowed.
        await TestSql.ExecuteAsync(connection, "INSERT INTO url_records (audit_id, canonical_url) VALUES (2, 'https://example.com/')");

        // Duplicate within the same audit is rejected.
        await Assert.ThrowsAsync<SqliteException>(() =>
            TestSql.ExecuteAsync(connection, "INSERT INTO url_records (audit_id, canonical_url) VALUES (1, 'https://example.com/')"));
    }

    [Fact(DisplayName = "CONF-26: WAL size observability hook is passive and local")]
    public async Task Conf26WalObservability()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        Assert.Null(provider.WalMonitor.TryGetWalSizeBytes());

        await using (var connection = await provider.OpenConnectionAsync())
        {
            await TestSql.ExecuteAsync(connection, "CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
            await TestSql.ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('wal-content')");

            // Measure while the database is open: SQLite checkpoints and removes the
            // WAL sidecar when the last connection closes.
            var walSize = provider.WalMonitor.TryGetWalSizeBytes();
            Assert.NotNull(walSize);
            Assert.True(walSize > 0);
            Assert.Equal(new FileInfo(provider.WalMonitor.WalFilePath).Length, walSize);

            var asyncSize = await provider.WalMonitor.GetWalSizeBytesAsync();
            Assert.Equal(walSize, asyncSize);

            await TestSql.ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            var truncatedWhileOpen = provider.WalMonitor.TryGetWalSizeBytes();
            Assert.True(truncatedWhileOpen is null or 0);
        }

        var truncatedSize = provider.WalMonitor.TryGetWalSizeBytes();
        Assert.True(truncatedSize is null or 0);
    }

    [Fact(DisplayName = "CONF-27: First-run initialization uses only local file I/O")]
    public async Task Conf27OfflineInitialization()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();

        await using (var connection = await provider.OpenConnectionAsync())
        {
            await TestSql.ExecuteAsync(connection, "CREATE TABLE application_settings (id INTEGER PRIMARY KEY, database_provider INTEGER NOT NULL)");
            await TestSql.ExecuteAsync(connection, "INSERT INTO application_settings (id, database_provider) VALUES (1, 0)");
            var seeded = await TestSql.ScalarAsync<long>(connection, "SELECT database_provider FROM application_settings WHERE id = 1");
            Assert.Equal(0L, seeded);
        }

        // Only the database file and its WAL sidecars exist — no other artifacts.
        var files = Directory.GetFiles(temp.DirectoryPath);
        Assert.All(files, file => Assert.StartsWith(temp.DatabasePath, file, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "CONF-28: Baseline defaults reproduce the unconfigured profile")]
    public void Conf28BaselineDefaults()
    {
        var options = new SqliteProviderOptions();

        Assert.Null(options.DatabasePath);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), options.BusyTimeout);
        Assert.Equal(SqliteSynchronousMode.Normal, options.Synchronous);
        Assert.Null(options.NativeExtensionDirectory);

        var expectedDefault = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrawlForge",
            "crawlforge.db");
        Assert.Equal(expectedDefault, options.ResolveDatabasePath());
        Assert.Equal(expectedDefault, SqliteProviderOptions.GetDefaultDatabasePath());

        var explicitOptions = new SqliteProviderOptions { DatabasePath = @"E:\data\custom.db" };
        Assert.Equal(@"E:\data\custom.db", explicitOptions.ResolveDatabasePath());
    }
}
