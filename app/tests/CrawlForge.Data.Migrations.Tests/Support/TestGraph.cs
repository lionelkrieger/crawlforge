namespace CrawlForge.Data.Migrations.Tests.Support;

/// <summary>
/// Deterministic parent-row inserts (project → configuration → audit → URL)
/// used by the vocabulary round-trip tests. All values are fixed constants;
/// timestamps use the canonical fixed-format UTC representation.
/// </summary>
public static class TestGraph
{
    /// <summary>Fixed UTC timestamp used by every test insert.</summary>
    public const string Timestamp = "2026-01-01 00:00:00.000000";

    /// <summary>Inserts the full parent graph: project 1, configuration 1, audit 1, URLs 1 and 2.</summary>
    /// <param name="db">The migrated test database.</param>
    /// <returns>A task that completes when the graph exists.</returns>
    public static async Task InsertFullGraphAsync(MigrationTestDatabase db)
    {
        await InsertProjectAsync(db);
        await InsertConfigurationAsync(db);
        await InsertAuditAsync(db);
        await InsertUrlAsync(db, 1, "https://example.com/page-1");
        await InsertUrlAsync(db, 2, "https://example.com/page-2");
    }

    /// <summary>Inserts project 1.</summary>
    /// <param name="db">The migrated test database.</param>
    /// <returns>A task that completes when the row exists.</returns>
    public static Task InsertProjectAsync(MigrationTestDatabase db) =>
        db.ExecuteAsync(
            "INSERT INTO projects (id, name, created_at, updated_at, is_deleted) " +
            $"VALUES (1, 'Test Project', '{Timestamp}', '{Timestamp}', 0);");

    /// <summary>Inserts configuration 1 under project 1.</summary>
    /// <param name="db">The migrated test database.</param>
    /// <returns>A task that completes when the row exists.</returns>
    public static Task InsertConfigurationAsync(MigrationTestDatabase db) =>
        db.ExecuteAsync(
            "INSERT INTO crawl_configurations (id, project_id, name, seeds_json, rendering_mode, created_at, updated_at) " +
            $"VALUES (1, 1, 'Test Configuration', '[\"https://example.com/\"]', 0, '{Timestamp}', '{Timestamp}');");

    /// <summary>Inserts audit 1 under project 1 / configuration 1.</summary>
    /// <param name="db">The migrated test database.</param>
    /// <returns>A task that completes when the row exists.</returns>
    public static Task InsertAuditAsync(MigrationTestDatabase db) =>
        db.ExecuteAsync(
            "INSERT INTO audits (id, project_id, configuration_id, configuration_snapshot_json, status, phase, counters_json, engine_version, is_deleted) " +
            $"VALUES (1, 1, 1, '{{}}', 1, 1, '{{}}', '0.0.1', 0);");

    /// <summary>Inserts a URL record under audit 1.</summary>
    /// <param name="db">The migrated test database.</param>
    /// <param name="id">URL surrogate key.</param>
    /// <param name="canonicalUrl">Canonical URL (unique within the audit).</param>
    /// <returns>A task that completes when the row exists.</returns>
    public static Task InsertUrlAsync(MigrationTestDatabase db, long id, string canonicalUrl) =>
        db.ExecuteAsync(
            "INSERT INTO url_records (id, audit_id, canonical_url, host, path, depth, discovery_source, status_class, robots_state, indexability_flags, is_indexable) " +
            $"VALUES ({id}, 1, '{canonicalUrl}', 'example.com', '/page-{id}', 0, 0, 1, 1, 0, 1);");
}
