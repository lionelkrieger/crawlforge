using System.Text.RegularExpressions;
using CrawlForge.Data.Migrations.Dialects;
using CrawlForge.Data.Migrations.Schema;
using Xunit;

namespace CrawlForge.Data.Migrations.Tests;

/// <summary>
/// Structural verification of the MySQL dialect (no MySQL server exists on
/// this machine; live parity is exercised in phase 07). Asserts that the
/// rendered DDL covers all 40 logical objects, every required index and
/// additive column, uses MySQL physical types, and stays within InnoDB
/// index-key limits — and that both dialects render the same logical schema.
/// </summary>
public class MySqlDialectStructureTests
{
    private static readonly SqlDialect MySql = new MySqlDialect();
    private static readonly SqlDialect Sqlite = new SqliteDialect();

    /// <summary>The rendered MySQL DDL creates all 40 catalog objects.</summary>
    [Fact]
    public void RenderedDdlCoversAllFortyObjects()
    {
        var ddl = RenderAll(MySql);
        foreach (var table in SchemaCatalog.AllTables)
        {
            // The ledger creation is idempotent (bootstrapped before version 1).
            var prefix = table.Name == SchemaCatalog.LedgerTableName
                ? "CREATE TABLE IF NOT EXISTS "
                : "CREATE TABLE ";
            Assert.Contains($"{prefix}`{table.Name}`", ddl, StringComparison.Ordinal);
        }

        Assert.Equal(40, SchemaCatalog.AllTables.Count);
    }

    /// <summary>The MySQL rendering uses MySQL physical types and engine options, never SQLite syntax.</summary>
    [Fact]
    public void RenderedDdlUsesMySqlPhysicalTypes()
    {
        var ddl = RenderAll(MySql);
        Assert.Contains("BIGINT NOT NULL AUTO_INCREMENT", ddl, StringComparison.Ordinal);
        Assert.Contains("TINYINT(1)", ddl, StringComparison.Ordinal);
        Assert.Contains("DATETIME(6)", ddl, StringComparison.Ordinal);
        Assert.Contains("LONGTEXT", ddl, StringComparison.Ordinal);
        Assert.Contains("ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci", ddl, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (`id`)", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOINCREMENT", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("PRAGMA", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("INTEGER", ddl, StringComparison.Ordinal);
    }

    /// <summary>Every required index is rendered, with uniqueness and descending directions preserved.</summary>
    [Fact]
    public void RenderedDdlContainsEveryRequiredIndex()
    {
        var ddl = RenderAll(MySql);
        foreach (var index in SchemaCatalog.AllIndexes)
        {
            var unique = index.Unique ? "UNIQUE " : string.Empty;
            Assert.Contains($"CREATE {unique}INDEX `{index.Name}` ON `{index.TableName}`", ddl, StringComparison.Ordinal);
            foreach (var column in index.Columns.Where(c => c.Descending))
            {
                Assert.Contains($"`{column.Name}` DESC", ddl, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Every additive column from Appendix A is rendered as an ALTER TABLE ADD COLUMN.</summary>
    [Fact]
    public void RenderedDdlContainsEveryAdditiveColumn()
    {
        var ddl = RenderAll(MySql);
        foreach (var (tableName, columns) in SchemaCatalog.AdditiveColumnsByTable)
        {
            foreach (var column in columns)
            {
                Assert.Contains($"ALTER TABLE `{tableName}` ADD COLUMN `{column.Name}`", ddl, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>The additive content-scope relationship is added via ALTER TABLE ADD CONSTRAINT on MySQL.</summary>
    [Fact]
    public void AdditiveForeignKeyIsAddedViaAlterConstraint()
    {
        var ddl = RenderAll(MySql);
        Assert.Contains(
            "ALTER TABLE `crawl_configurations` ADD CONSTRAINT `fk_crawl_configurations_content_scope_profile_id` " +
            "FOREIGN KEY (`content_scope_profile_id`) REFERENCES `content_scope_profiles` (`id`)",
            ddl,
            StringComparison.Ordinal);

        // The embedded dialect declares the same relationship inline instead.
        var sqliteDdl = RenderAll(Sqlite);
        Assert.Contains(
            "CONSTRAINT \"fk_crawl_configurations_content_scope_profile_id\" FOREIGN KEY (\"content_scope_profile_id\") " +
            "REFERENCES \"content_scope_profiles\" (\"id\")",
            sqliteDdl,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ADD CONSTRAINT", sqliteDdl, StringComparison.Ordinal);
    }

    /// <summary>The ledger bootstrap is idempotent (CREATE TABLE IF NOT EXISTS) on both dialects.</summary>
    [Fact]
    public void LedgerBootstrapIsIdempotentOnBothDialects()
    {
        var mysqlRenderer = new SqlRenderer(MySql);
        var sqliteRenderer = new SqlRenderer(Sqlite);
        Assert.Contains("CREATE TABLE IF NOT EXISTS `schema_migrations`", mysqlRenderer.RenderLedgerBootstrap(), StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"schema_migrations\"", sqliteRenderer.RenderLedgerBootstrap(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every composite index fits the InnoDB 3072-byte key limit under utf8mb4:
    /// indexable strings are bounded VARCHAR columns, never unbounded TEXT.
    /// </summary>
    [Fact]
    public void EveryIndexFitsInnoDbKeyLimit()
    {
        var tablesByName = SchemaCatalog.AllTables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var index in SchemaCatalog.AllIndexes)
        {
            var table = tablesByName[index.TableName];
            var width = 0;
            foreach (var indexColumn in index.Columns)
            {
                var column = table.Columns.Single(c => c.Name == indexColumn.Name);
                width += column.Type switch
                {
                    LogicalType.Varchar => column.Length * 4,
                    LogicalType.Int64 => 8,
                    LogicalType.Int32 => 4,
                    LogicalType.Boolean => 1,
                    LogicalType.Double => 8,
                    LogicalType.Timestamp => 8,
                    _ => throw new InvalidOperationException($"Unbounded type '{column.Type}' indexed by '{index.Name}'."),
                };
            }

            Assert.True(width <= 3072, $"Index '{index.Name}' exceeds the InnoDB key limit ({width} bytes).");
        }
    }

    /// <summary>Both dialects render the identical logical object, index, and additive-column sets.</summary>
    [Fact]
    public void BothDialectsRenderTheSameLogicalSchema()
    {
        var mysqlDdl = RenderAll(MySql);
        var sqliteDdl = RenderAll(Sqlite);

        Assert.Equal(ExtractNames(mysqlDdl, "CREATE TABLE `"), ExtractNames(sqliteDdl, "CREATE TABLE \""));
        Assert.Equal(ExtractIndexNames(mysqlDdl, '`'), ExtractIndexNames(sqliteDdl, '"'));
        Assert.Equal(ExtractAddedColumns(mysqlDdl, '`'), ExtractAddedColumns(sqliteDdl, '"'));
    }

    /// <summary>Both dialects serialize UTC timestamps to the same canonical fixed-format text.</summary>
    [Fact]
    public void TimestampSerializationIsDialectInvariant()
    {
        var instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(MySql.WriteTimestamp(instant), Sqlite.WriteTimestamp(instant));
        Assert.Equal("2026-01-01 00:00:00.000000", MySql.WriteTimestamp(instant));
    }

    private static string RenderAll(SqlDialect dialect)
    {
        var renderer = new SqlRenderer(dialect);
        var statements = new List<string> { renderer.RenderLedgerBootstrap() };
        foreach (var migration in SchemaCatalog.Migrations)
        {
            statements.AddRange(renderer.RenderMigration(migration));
        }

        return string.Join("\n", statements);
    }

    private static List<string> ExtractNames(string ddl, string prefix)
    {
        var names = new List<string>();
        var index = 0;
        while ((index = ddl.IndexOf(prefix, index, StringComparison.Ordinal)) >= 0)
        {
            var start = index + prefix.Length;
            var end = ddl.IndexOfAny(['`', '"'], start);
            var name = ddl[start..end];
            // The embedded dialect's table-rebuild swap uses transient names;
            // they are physical machinery, not logical schema objects.
            if (!name.StartsWith("__cf_rebuild_", StringComparison.Ordinal))
            {
                names.Add(name);
            }

            index = end;
        }

        return names.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private static List<string> ExtractIndexNames(string ddl, char quote)
    {
        var pattern = $"CREATE (?:UNIQUE )?INDEX {quote}([a-z0-9_]+){quote}";
        return Regex.Matches(ddl, pattern)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> ExtractAddedColumns(string ddl, char quote)
    {
        var pattern = $"ADD COLUMN {quote}([a-z0-9_]+){quote}";
        return Regex.Matches(ddl, pattern)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}
