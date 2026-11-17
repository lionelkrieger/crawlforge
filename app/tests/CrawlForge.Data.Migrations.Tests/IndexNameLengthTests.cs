using System.Reflection;
using CrawlForge.Data.Migrations.Schema;
using CrawlForge.Data.Migrations.Tests.Support;
using Xunit;

namespace CrawlForge.Data.Migrations.Tests;

/// <summary>
/// DEFECT-11 (D6) regression facts: every catalog identifier fits MySQL's
/// 64-character limit on BOTH dialects (the catalog is provider-agnostic, so
/// one name set serves SQLite and MySQL), the two formerly over-long indexes
/// carry the exact MySQL logical-schema short names, and the catalog helper
/// guard fails fast on any future over-long name.
/// </summary>
public class IndexNameLengthTests : IAsyncLifetime
{
    private readonly MigrationTestDatabase _db = new();

    /// <summary>Creates and migrates the per-test embedded database.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await _db.MigrateAsync();
    }

    /// <summary>Tears down the per-test database.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public Task DisposeAsync() => _db.DisposeAsync();

    /// <summary>Every catalog index name is at most 64 characters (both dialects).</summary>
    [Fact]
    public void EveryCatalogIndexName_FitsMySqlIdentifierLimit()
    {
        Assert.NotEmpty(SchemaCatalog.AllIndexes);
        foreach (var index in SchemaCatalog.AllIndexes)
        {
            Assert.True(
                index.Name.Length <= SchemaCatalog.MaxIdentifierLength,
                $"Index '{index.Name}' on '{index.TableName}' is {index.Name.Length} chars (> {SchemaCatalog.MaxIdentifierLength}).");
        }
    }

    /// <summary>Every catalog table name is at most 64 characters (both dialects).</summary>
    [Fact]
    public void EveryCatalogTableName_FitsMySqlIdentifierLimit()
    {
        foreach (var table in SchemaCatalog.AllTables)
        {
            Assert.True(
                table.Name.Length <= SchemaCatalog.MaxIdentifierLength,
                $"Table '{table.Name}' is {table.Name.Length} chars (> {SchemaCatalog.MaxIdentifierLength}).");
        }
    }

    /// <summary>Catalog index names are unique across the whole schema.</summary>
    [Fact]
    public void CatalogIndexNames_AreUnique()
    {
        var duplicates = SchemaCatalog.AllIndexes
            .GroupBy(i => i.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(duplicates);
    }

    /// <summary>
    /// The two D6 indexes carry the exact short names declared by the MySQL
    /// logical schema (MySqlLogicalSchema), so migration catalog and MySQL
    /// logical schema are in sync.
    /// </summary>
    [Fact]
    public void D6Indexes_UseTheMySqlLogicalSchemaShortNames()
    {
        var semanticUnique = SchemaCatalog.AllIndexes.Single(i =>
            i.TableName == "semantic_link_opportunities" && i.Unique);
        Assert.Equal("ux_semantic_link_opportunities_audit_source_target", semanticUnique.Name);
        Assert.Equal(
            ["audit_id", "source_url_id", "target_url_id"],
            semanticUnique.Columns.Select(c => c.Name));

        var ngramDf = SchemaCatalog.AllIndexes.Single(i =>
            i.TableName == "ngram_site_summaries" && !i.Unique);
        Assert.Equal("ix_ngram_site_summaries_audit_zone_order_df", ngramDf.Name);
        Assert.Equal(
            ["audit_id", "zone", "ngram_order", "document_frequency"],
            ngramDf.Columns.Select(c => c.Name));
        Assert.True(ngramDf.Columns[^1].Descending);
    }

    /// <summary>The embedded dialect physically creates the two short names.</summary>
    [Fact]
    public async Task EmbeddedDialect_PhysicallyCreatesTheShortNames()
    {
        await using var connection = await _db.OpenAsync();

        var semanticIndexes = await SchemaInspector.GetIndexesAsync(connection, "semantic_link_opportunities");
        Assert.Contains(semanticIndexes, i => i.Name == "ux_semantic_link_opportunities_audit_source_target");

        var ngramIndexes = await SchemaInspector.GetIndexesAsync(connection, "ngram_site_summaries");
        Assert.Contains(ngramIndexes, i => i.Name == "ix_ngram_site_summaries_audit_zone_order_df");

        // No over-long identifier survives anywhere in the physical schema.
        foreach (var table in SchemaCatalog.AllTables)
        {
            foreach (var index in await SchemaInspector.GetIndexesAsync(connection, table.Name))
            {
                Assert.True(index.Name.Length <= SchemaCatalog.MaxIdentifierLength);
            }
        }
    }

    /// <summary>
    /// The helper guard throws a clear error for over-long names and accepts
    /// names at the limit (reflection: the guard is a catalog implementation
    /// detail, exercised here without widening the public surface).
    /// </summary>
    [Fact]
    public void IdentifierGuard_ThrowsOnOverLongNames_AcceptsLimit()
    {
        var guard = typeof(SchemaCatalog).GetMethod(
            "ValidateIdentifierName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(guard);

        var atLimit = new string('a', SchemaCatalog.MaxIdentifierLength);
        Assert.Equal(atLimit, guard!.Invoke(null, [atLimit]));

        var overLimit = new string('a', SchemaCatalog.MaxIdentifierLength + 1);
        var ex = Assert.Throws<TargetInvocationException>(() => guard.Invoke(null, [overLimit]));
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("64", inner.Message, StringComparison.Ordinal);
        Assert.Contains("explicit short name", inner.Message, StringComparison.Ordinal);
    }
}
