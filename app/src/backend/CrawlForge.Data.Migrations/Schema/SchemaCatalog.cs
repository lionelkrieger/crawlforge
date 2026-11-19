namespace CrawlForge.Data.Migrations.Schema;

/// <summary>
/// The complete provider-agnostic schema catalog (FS-CRAWL-001 §9.2/§9.3 plus
/// FS-CRAWL-002 Appendix A): 25 baseline tables, 14 additive tables, and the
/// <c>ngram_site_summaries</c> aggregate read model — 40 logical schema objects
/// in total. The catalog is the single authority consumed by the dialect
/// renderers, the migrator, and the coverage tests.
/// </summary>
/// <remarks>
/// <para>
/// Conventions (§9.1): surrogate integer keys named <c>id</c>; UTC timestamps;
/// JSON text columns for flexible payloads; snake_case names. Index names are
/// stable across dialects (<c>ix_</c> prefix, <c>ux_</c> for unique indexes).
/// </para>
/// <para>
/// The <c>url_embeddings</c> parent table is part of the baseline and is
/// activated by the local embedding pipeline without schema change
/// (FS-CRAWL-002 §12.7). Element groups, metric keys, and rule keys are open
/// string vocabularies carried by <c>url_elements.element_group</c>,
/// <c>metric_summaries.metric_key</c>, and <c>findings.rule_key</c>; the additive
/// vocabularies of Appendix A are supported by construction and proven by
/// round-trip tests against the locked contract constants.
/// </para>
/// </remarks>
public static partial class SchemaCatalog
{
    /// <summary>Name of the idempotent per-provider migration ledger (§9.2).</summary>
    public const string LedgerTableName = "schema_migrations";

    /// <summary>
    /// Maximum schema identifier length enforced on EVERY dialect (DEFECT-11/D6):
    /// MySQL caps identifiers at 64 characters, so the provider-agnostic catalog
    /// holds all index names to the same bound for cross-provider parity.
    /// </summary>
    public const int MaxIdentifierLength = 64;

    /// <summary>The schema version produced by the newest migration.</summary>
    public static int LatestVersion => Migrations[^1].Version;

    /// <summary>
    /// The 25 baseline tables of FS-CRAWL-001 §9.2 in catalog order, in their
    /// version-1 shape (before additive columns).
    /// </summary>
    public static IReadOnlyList<TableDefinition> BaselineTables => BaselineTablesLazy.Value;

    /// <summary>
    /// The 14 additive tables of FS-CRAWL-002 Appendix A in catalog order.
    /// </summary>
    public static IReadOnlyList<TableDefinition> AdditiveTables => AdditiveTablesLazy.Value;

    /// <summary>
    /// Aggregate read models materialized as precomputed tables (§8.1 pattern;
    /// FS-CRAWL-002 §1.4 rule 6). <c>ngram_site_summaries</c> is a materialized
    /// read model on every provider so dashboard reads stay constant-cost
    /// lookups instead of full scans over raw n-gram rows.
    /// </summary>
    public static IReadOnlyList<TableDefinition> ReadModelTables => ReadModelTablesLazy.Value;

    /// <summary>
    /// Additive columns per parent table (FS-CRAWL-002 Appendix A), applied by
    /// the additive-columns migration. All additive columns are nullable.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ColumnDefinition>> AdditiveColumnsByTable =>
        AdditiveColumnsLazy.Value;

    /// <summary>
    /// WP-10-A1 (D-021) capture-mode amendment columns (FS-CRAWL-001 §5.6.1,
    /// CR-029), applied by migration version 5 and merged into the final catalog
    /// shape. Separate from the version-3 additive set so existing databases
    /// receive the columns through the new ledger version.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ColumnDefinition>> CaptureModeColumnsByTable =>
        CaptureModeColumnsLazy.Value;

    /// <summary>
    /// All 40 logical schema objects in their final shape: baseline tables with
    /// additive columns merged, followed by the additive tables and the
    /// materialized read model.
    /// </summary>
    public static IReadOnlyList<TableDefinition> AllTables => AllTablesLazy.Value;

    /// <summary>
    /// The catalog shape AS OF migration version 3 (baseline tables + the
    /// version-3 additive columns and foreign keys, WITHOUT columns introduced
    /// by later versions). The embedded dialect's foreign-key table rebuild
    /// runs inside version 3 and must copy only columns that exist at that
    /// point — referencing a later version's column in the rebuild SELECT
    /// fails on a fresh database (WP-10-A1 integration lesson).
    /// </summary>
    public static IReadOnlyList<TableDefinition> AdditiveVersionTables => AdditiveVersionTablesLazy.Value;

    /// <summary>
    /// The ordered, versioned migrations that produce the complete catalog on
    /// any provider dialect.
    /// </summary>
    public static IReadOnlyList<MigrationDefinition> Migrations => MigrationsLazy.Value;

    /// <summary>
    /// Every index created by the migration sequence, in creation order:
    /// indexes owned by created tables plus standalone additive indexes
    /// (the <c>link_edges</c> dom-zone index). This is the authoritative
    /// required-index set for both dialects.
    /// </summary>
    public static IReadOnlyList<IndexDefinition> AllIndexes => AllIndexesLazy.Value;

    // Lazy initialization keeps partial-file declaration order irrelevant and
    // resolves cross-references (AllTables -> additive maps) on first access.
    private static readonly Lazy<IReadOnlyList<TableDefinition>> BaselineTablesLazy = new(BuildBaselineTables);
    private static readonly Lazy<IReadOnlyList<TableDefinition>> AdditiveTablesLazy = new(BuildAdditiveTables);
    private static readonly Lazy<IReadOnlyList<TableDefinition>> ReadModelTablesLazy = new(BuildReadModelTables);
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<ColumnDefinition>>> AdditiveColumnsLazy = new(BuildAdditiveColumns);
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<ColumnDefinition>>> CaptureModeColumnsLazy = new(BuildCaptureModeColumns);
    private static readonly Lazy<IReadOnlyList<TableDefinition>> AllTablesLazy = new(BuildAllTables);
    private static readonly Lazy<IReadOnlyList<TableDefinition>> AdditiveVersionTablesLazy = new(BuildAdditiveVersionTables);
    private static readonly Lazy<IReadOnlyList<MigrationDefinition>> MigrationsLazy = new(BuildMigrations);
    private static readonly Lazy<IReadOnlyList<IndexDefinition>> AllIndexesLazy = new(BuildAllIndexes);

    private static ColumnDefinition Col(string name, LogicalType type, bool nullable = false, int length = 0) =>
        new(name, type, nullable, length);

    private static ColumnDefinition Id() => Col(TableDefinition.PrimaryKeyName, LogicalType.Int64);

    private static ForeignKeyDefinition Fk(string column, string referencedTable) =>
        new(column, referencedTable, TableDefinition.PrimaryKeyName);

    private static IndexDefinition Idx(string table, params IndexColumn[] columns) =>
        Idx(SynthesizeIndexName("ix", table, columns), table, columns);

    private static IndexDefinition Ux(string table, params IndexColumn[] columns) =>
        Ux(SynthesizeIndexName("ux", table, columns), table, columns);

    // Explicit-name overloads (DEFECT-11/D6): used when the synthesized
    // {prefix}_{table}_{columns} name would exceed MaxIdentifierLength. The
    // explicit name must match the MySQL logical-schema short name so both
    // dialects stay in sync.
    private static IndexDefinition Idx(string name, string table, params IndexColumn[] columns) =>
        new(ValidateIdentifierName(name), table, columns, Unique: false);

    private static IndexDefinition Ux(string name, string table, params IndexColumn[] columns) =>
        new(ValidateIdentifierName(name), table, columns, Unique: true);

    private static string SynthesizeIndexName(string prefix, string table, IndexColumn[] columns) =>
        $"{prefix}_{table}_{string.Join("_", columns.Select(c => c.Name))}";

    // Name-length guard (DEFECT-11/D6): a synthesized or explicit identifier
    // longer than the MySQL 64-character limit fails fast at catalog
    // construction with a clear error instead of dying mid-migration on the
    // external provider.
    private static string ValidateIdentifierName(string name) =>
        name.Length <= MaxIdentifierLength
            ? name
            : throw new InvalidOperationException(
                $"Schema identifier '{name}' is {name.Length} characters; the maximum is {MaxIdentifierLength} "
                + "(MySQL identifier limit, enforced on every dialect for cross-provider parity). "
                + "Give the index an explicit short name aligned with the MySQL logical schema.");

    private static IndexColumn Asc(string name) => new(name);

    private static IndexColumn Desc(string name) => new(name, Descending: true);

    private static TableDefinition Table(
        string name,
        ColumnDefinition[] columns,
        ForeignKeyDefinition[]? foreignKeys = null,
        IndexDefinition[]? indexes = null) =>
        new(name, [Id(), .. columns], foreignKeys ?? [], indexes ?? []);

    private static IReadOnlyList<TableDefinition> BuildAllTables()
    {
        var result = new List<TableDefinition>(BaselineTables.Count + AdditiveTables.Count + ReadModelTables.Count);
        foreach (var baseline in BaselineTables)
        {
            var merged = baseline;
            if (AdditiveColumnsByTable.TryGetValue(baseline.Name, out var additive))
            {
                merged = merged with { Columns = [.. merged.Columns, .. additive] };
            }

            if (CaptureModeColumnsByTable.TryGetValue(baseline.Name, out var captureColumns))
            {
                merged = merged with { Columns = [.. merged.Columns, .. captureColumns] };
            }

            if (AdditiveForeignKeysByTable.TryGetValue(baseline.Name, out var additiveKeys))
            {
                merged = merged with { ForeignKeys = [.. merged.ForeignKeys, .. additiveKeys] };
            }

            result.Add(merged);
        }

        result.AddRange(AdditiveTables);
        result.AddRange(ReadModelTables);
        return result;
    }

    private static IReadOnlyList<TableDefinition> BuildAdditiveVersionTables()
    {
        // Version-3 shape only: baseline + additive columns + additive foreign
        // keys. Later-version column sets (e.g. CaptureModeColumnsByTable,
        // version 5) are deliberately excluded — see AdditiveVersionTables.
        var result = new List<TableDefinition>(BaselineTables.Count + AdditiveTables.Count + ReadModelTables.Count);
        foreach (var baseline in BaselineTables)
        {
            var merged = baseline;
            if (AdditiveColumnsByTable.TryGetValue(baseline.Name, out var additive))
            {
                merged = merged with { Columns = [.. merged.Columns, .. additive] };
            }

            if (AdditiveForeignKeysByTable.TryGetValue(baseline.Name, out var additiveKeys))
            {
                merged = merged with { ForeignKeys = [.. merged.ForeignKeys, .. additiveKeys] };
            }

            result.Add(merged);
        }

        result.AddRange(AdditiveTables);
        result.AddRange(ReadModelTables);
        return result;
    }

    private static IReadOnlyList<IndexDefinition> BuildAllIndexes()
    {
        var indexes = new List<IndexDefinition>();
        foreach (var migration in Migrations)
        {
            foreach (var operation in migration.Operations)
            {
                switch (operation)
                {
                    case CreateTableOperation create:
                        indexes.AddRange(create.Table.Indexes);
                        break;
                    case CreateIndexOperation createIndex:
                        indexes.Add(createIndex.Index);
                        break;
                }
            }
        }

        return indexes;
    }
}
