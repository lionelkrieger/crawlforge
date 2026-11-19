namespace CrawlForge.Data.Migrations.Schema;

/// <summary>
/// Versioned migration definitions. Every migration is a provider-agnostic
/// logical definition rendered per dialect; the ledger records each applied
/// version per provider, making re-application a no-op (criterion #9).
/// </summary>
public static partial class SchemaCatalog
{
    /// <summary>
    /// Additive foreign keys introduced after the baseline, keyed by owning
    /// table. The content-scope profile reference can only be constrained once
    /// the additive <c>content_scope_profiles</c> table exists.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ForeignKeyDefinition>> AdditiveForeignKeysByTable =>
        AdditiveForeignKeysLazy.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<ForeignKeyDefinition>>> AdditiveForeignKeysLazy = new(
        () => new Dictionary<string, IReadOnlyList<ForeignKeyDefinition>>(StringComparer.Ordinal)
        {
            ["crawl_configurations"] = [Fk("content_scope_profile_id", "content_scope_profiles")],
        });

    private static IReadOnlyList<MigrationDefinition> BuildMigrations()
    {
        // Version 1 — the 25 baseline tables of FS-CRAWL-001 §9.2 with all §9.3
        // relationships and required indexes. The migration ledger itself is
        // bootstrapped by the migrator before this migration applies.
        var baselineOperations = new List<MigrationOperation>();
        foreach (var table in BaselineTables)
        {
            baselineOperations.Add(new CreateTableOperation(table));
        }

        // Version 2 — the 14 additive tables of FS-CRAWL-002 Appendix A with
        // their required indexes.
        var additiveTableOperations = new List<MigrationOperation>();
        foreach (var table in AdditiveTables)
        {
            additiveTableOperations.Add(new CreateTableOperation(table));
        }

        // Version 3 — additive columns on the parent tables, the additive
        // link_edges dom-zone index, and the additive content-scope relationship.
        var additiveColumnOperations = new List<MigrationOperation>();
        foreach (var (tableName, columns) in AdditiveColumnsByTable)
        {
            foreach (var column in columns)
            {
                additiveColumnOperations.Add(new AddColumnOperation(tableName, column));
            }
        }

        foreach (var (tableName, foreignKeys) in AdditiveForeignKeysByTable)
        {
            foreach (var foreignKey in foreignKeys)
            {
                additiveColumnOperations.Add(new AddForeignKeyOperation(tableName, foreignKey));
            }
        }

        additiveColumnOperations.Add(new CreateIndexOperation(
            Idx("link_edges", Asc("audit_id"), Asc("dom_zone"))));

        // Version 4 — the ngram_site_summaries aggregate, materialized as a
        // precomputed read model on every provider (§8.1 pattern; rule 6).
        var readModelOperations = new List<MigrationOperation>();
        foreach (var table in ReadModelTables)
        {
            readModelOperations.Add(new CreateTableOperation(table));
        }

        // Version 5 — WP-10-A1 (D-021) capture-mode amendment columns:
        // crawl_configurations.capture_mode/capture_settings_json/
        // redirect_checks_json + url_records.capture_source (FS-CRAWL-001
        // §5.6.1, CR-029). A NEW version because version 3 is already recorded
        // in existing ledgers; fresh databases apply it after version 3, and
        // the version-3 set deliberately excludes these columns (no duplicate
        // ALTER on any path). All nullable: null keeps baseline behaviour.
        var captureModeOperations = new List<MigrationOperation>();
        foreach (var (tableName, columns) in CaptureModeColumnsByTable)
        {
            foreach (var column in columns)
            {
                captureModeOperations.Add(new AddColumnOperation(tableName, column));
            }
        }

        return
        [
            new MigrationDefinition(1, "baseline-schema", baselineOperations),
            new MigrationDefinition(2, "additive-tables", additiveTableOperations),
            new MigrationDefinition(3, "additive-columns", additiveColumnOperations),
            new MigrationDefinition(4, "ngram-site-summaries-read-model", readModelOperations),
            new MigrationDefinition(5, "capture-mode-columns", captureModeOperations),
        ];
    }
}
