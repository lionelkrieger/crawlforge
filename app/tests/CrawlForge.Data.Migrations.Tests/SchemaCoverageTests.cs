using System.Data.Common;
using CrawlForge.Data.Migrations.Schema;
using CrawlForge.Data.Migrations.Tests.Support;
using Xunit;

namespace CrawlForge.Data.Migrations.Tests;

/// <summary>
/// Live schema coverage on the embedded dialect: exactly the 40 catalog
/// objects (25 baseline tables + 14 additive tables + the materialized
/// ngram_site_summaries read model), every column, every required index, and
/// every relationship from the unified catalog — enforced, not just declared.
/// </summary>
public class SchemaCoverageTests : IAsyncLifetime
{
    private readonly MigrationTestDatabase _db = new();

    /// <summary>Creates and migrates the per-test database.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await _db.MigrateAsync();
    }

    /// <summary>Tears down the per-test database.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public Task DisposeAsync() => _db.DisposeAsync();

    /// <summary>The live schema contains exactly the 40 catalog objects and no views or extras.</summary>
    [Fact]
    public async Task SchemaContainsExactlyFortyCatalogObjects()
    {
        await using var connection = await _db.OpenAsync();
        var tables = await SchemaInspector.GetTableNamesAsync(connection);
        var expected = SchemaCatalog.AllTables.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(40, tables.Count);
        Assert.Equal(25, SchemaCatalog.BaselineTables.Count);
        Assert.Equal(14, SchemaCatalog.AdditiveTables.Count);
        Assert.Single(SchemaCatalog.ReadModelTables);
        Assert.Equal(expected, tables);
        Assert.Empty(await SchemaInspector.GetViewNamesAsync(connection));
    }

    /// <summary>Every table carries exactly the catalog columns, in order, with matching nullability.</summary>
    [Fact]
    public async Task EveryTableMatchesCatalogColumns()
    {
        await using var connection = await _db.OpenAsync();
        foreach (var table in SchemaCatalog.AllTables)
        {
            var columns = await SchemaInspector.GetColumnsAsync(connection, table.Name);
            Assert.Equal(table.Columns.Select(c => c.Name), columns.Select(c => c.Name));
            foreach (var (expected, actual) in table.Columns.Zip(columns))
            {
                Assert.Equal(!expected.Nullable, actual.NotNull);
            }

            var primaryKey = columns.Single(c => c.IsPrimaryKey);
            Assert.Equal(TableDefinition.PrimaryKeyName, primaryKey.Name);
        }
    }

    /// <summary>Every required index exists with the catalog shape (columns, order, uniqueness, direction).</summary>
    [Fact]
    public async Task EveryRequiredIndexMatchesCatalogShape()
    {
        await using var connection = await _db.OpenAsync();
        foreach (var table in SchemaCatalog.AllTables)
        {
            var expectedIndexes = SchemaCatalog.AllIndexes.Where(i => i.TableName == table.Name).ToList();
            var actualIndexes = await SchemaInspector.GetIndexesAsync(connection, table.Name);
            Assert.Equal(
                expectedIndexes.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal),
                actualIndexes.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));

            foreach (var expected in expectedIndexes)
            {
                var actual = actualIndexes.Single(i => i.Name == expected.Name);
                Assert.Equal(expected.Unique, actual.Unique);
                Assert.Equal(expected.Columns.Select(c => c.Name), actual.Columns.Select(c => c.Name));
                Assert.Equal(expected.Columns.Select(c => c.Descending), actual.Columns.Select(c => c.Descending));
            }
        }
    }

    /// <summary>Every catalog relationship is declared, including the additive content-scope reference.</summary>
    [Fact]
    public async Task EveryRelationshipIsDeclared()
    {
        await using var connection = await _db.OpenAsync();
        foreach (var table in SchemaCatalog.AllTables)
        {
            var expected = table.ForeignKeys
                .Select(f => $"{f.Column}->{f.ReferencedTable}.{f.ReferencedColumn}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            var actual = (await SchemaInspector.GetForeignKeysAsync(connection, table.Name))
                .Select(f => $"{f.Column}->{f.ReferencedTable}.{f.ReferencedColumn}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(expected, actual);
        }

        // The additive profile reference is part of the final relationship set.
        Assert.Contains(
            SchemaCatalog.AllTables.Single(t => t.Name == "crawl_configurations").ForeignKeys,
            fk => fk.Column == "content_scope_profile_id" && fk.ReferencedTable == "content_scope_profiles");
    }

    /// <summary>Foreign keys are enforced: dangling audit references are rejected.</summary>
    [Fact]
    public async Task ForeignKeysAreEnforced()
    {
        await Assert.ThrowsAnyAsync<DbException>(() => _db.ExecuteAsync(
            "INSERT INTO url_records (audit_id, canonical_url, host, path, depth, discovery_source, status_class, robots_state, indexability_flags, is_indexable) " +
            "VALUES (999, 'https://example.com/orphan', 'example.com', '/orphan', 0, 0, 1, 0, 0, 1);"));

        await TestGraph.InsertFullGraphAsync(_db);
        await Assert.ThrowsAnyAsync<DbException>(() => _db.ExecuteAsync(
            "INSERT INTO link_edges (audit_id, source_url_id, canonical_target_url, relation_flags, is_internal) " +
            "VALUES (1, 999, 'https://example.com/page-2', 0, 1);"));

        // Additive relationship: the content-scope profile reference is enforced too.
        await Assert.ThrowsAnyAsync<DbException>(() => _db.ExecuteAsync(
            "UPDATE crawl_configurations SET content_scope_profile_id = 999 WHERE id = 1;"));
    }

    /// <summary>The unique (audit, canonical URL) constraint is enforced.</summary>
    [Fact]
    public async Task UniqueAuditCanonicalUrlIsEnforced()
    {
        await TestGraph.InsertFullGraphAsync(_db);
        await Assert.ThrowsAnyAsync<DbException>(() => TestGraph.InsertUrlAsync(_db, 3, "https://example.com/page-1"));
    }

    /// <summary>Additive unique constraints are enforced (n-grams, redirect matrix, WAF detections).</summary>
    [Fact]
    public async Task AdditiveUniqueConstraintsAreEnforced()
    {
        await TestGraph.InsertFullGraphAsync(_db);

        await _db.ExecuteAsync(
            "INSERT INTO ngram_frequencies (audit_id, url_id, zone, ngram_order, term, term_count, term_frequency) " +
            "VALUES (1, 1, 2, 1, 'crawler', 3, 0.1);");
        await Assert.ThrowsAnyAsync<DbException>(() => _db.ExecuteAsync(
            "INSERT INTO ngram_frequencies (audit_id, url_id, zone, ngram_order, term, term_count, term_frequency) " +
            "VALUES (1, 1, 2, 1, 'crawler', 5, 0.2);"));

        await _db.ExecuteAsync(
            "INSERT INTO redirect_mappings (audit_id, source_url, expected_target_url, ordinal) " +
            "VALUES (1, 'https://example.com/old', 'https://example.com/page-1', 0);");
        await Assert.ThrowsAnyAsync<DbException>(() => _db.ExecuteAsync(
            "INSERT INTO redirect_mappings (audit_id, source_url, expected_target_url, ordinal) " +
            "VALUES (1, 'https://example.com/old', 'https://example.com/page-2', 1);"));

        await _db.ExecuteAsync(
            "INSERT INTO waf_detections (audit_id, url_id, provider, challenge_class, evidence_json, resolved_by_rendering, detected_at) " +
            $"VALUES (1, 1, 1, 2, '{{}}', 0, '{TestGraph.Timestamp}');");
        await Assert.ThrowsAnyAsync<DbException>(() => _db.ExecuteAsync(
            "INSERT INTO waf_detections (audit_id, url_id, provider, challenge_class, evidence_json, resolved_by_rendering, detected_at) " +
            $"VALUES (1, 1, 2, 1, '{{}}', 1, '{TestGraph.Timestamp}');"));
    }

    /// <summary>
    /// ngram_site_summaries is a materialized read model (a precomputed table,
    /// not a live view) on the embedded dialect, with the DF-descending index.
    /// </summary>
    [Fact]
    public async Task NgramSiteSummariesIsMaterializedReadModel()
    {
        await using var connection = await _db.OpenAsync();
        Assert.Contains("ngram_site_summaries", await SchemaInspector.GetTableNamesAsync(connection));
        Assert.DoesNotContain("ngram_site_summaries", await SchemaInspector.GetViewNamesAsync(connection));

        await TestGraph.InsertFullGraphAsync(_db);
        await _db.ExecuteAsync(
            "INSERT INTO ngram_site_summaries (audit_id, zone, ngram_order, term, document_frequency, total_frequency, mean_term_frequency, primary_term_count) " +
            "VALUES (1, 2, 1, 'crawler', 2, 9, 0.15, 1);");
        var stored = await _db.ScalarAsync(
            "SELECT total_frequency FROM ngram_site_summaries WHERE audit_id = 1 AND zone = 2 AND ngram_order = 1 AND term = 'crawler';");
        Assert.Equal(9L, Convert.ToInt64(stored));
    }

    /// <summary>The three descending read-path indexes are physically descending.</summary>
    [Fact]
    public async Task DescendingReadPathIndexesExist()
    {
        await using var connection = await _db.OpenAsync();
        var descending = new (string Table, string Index, string Column)[]
        {
            ("ngram_site_summaries", "ix_ngram_site_summaries_audit_zone_order_df", "document_frequency"),
            ("log_url_aggregates", "ix_log_url_aggregates_log_import_id_search_bot_hits", "search_bot_hits"),
            ("semantic_link_opportunities", "ix_semantic_link_opportunities_audit_id_similarity", "similarity"),
        };

        foreach (var (table, indexName, column) in descending)
        {
            var indexes = await SchemaInspector.GetIndexesAsync(connection, table);
            var index = indexes.Single(i => i.Name == indexName);
            var last = index.Columns[^1];
            Assert.Equal(column, last.Name);
            Assert.True(last.Descending);
        }
    }

    /// <summary>The activated url_embeddings parent table accepts pipeline rows without schema change.</summary>
    [Fact]
    public async Task ActivatedUrlEmbeddingsAcceptsRows()
    {
        await TestGraph.InsertFullGraphAsync(_db);
        await _db.ExecuteAsync(
            "INSERT INTO url_embeddings (audit_id, url_id, embedding_payload_json, model_tag) " +
            "VALUES (1, 1, '{\"values\":[0.1,0.2,0.3]}', 'bundled-onnx-test');");
        var modelTag = await _db.ScalarAsync("SELECT model_tag FROM url_embeddings WHERE audit_id = 1 AND url_id = 1;");
        Assert.Equal("bundled-onnx-test", modelTag);
    }
}
