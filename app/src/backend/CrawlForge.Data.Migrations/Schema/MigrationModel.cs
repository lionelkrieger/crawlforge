namespace CrawlForge.Data.Migrations.Schema;

/// <summary>
/// One provider-agnostic operation inside a versioned migration. Operations are
/// rendered to dialect-specific SQL by the active dialect and executed inside a
/// transaction together with the migration-ledger insert.
/// </summary>
public abstract record MigrationOperation;

/// <summary>Creates a logical table together with its indexes.</summary>
/// <param name="Table">The table to create.</param>
public sealed record CreateTableOperation(TableDefinition Table) : MigrationOperation;

/// <summary>Adds an additive column to an existing table (precedence rule 3).</summary>
/// <param name="TableName">Target table name.</param>
/// <param name="Column">The additive column (always nullable).</param>
public sealed record AddColumnOperation(string TableName, ColumnDefinition Column) : MigrationOperation;

/// <summary>Creates an index over an existing table.</summary>
/// <param name="Index">The index to create.</param>
public sealed record CreateIndexOperation(IndexDefinition Index) : MigrationOperation;

/// <summary>
/// Adds an additive foreign-key constraint to an existing table. Rendered as a
/// table-level constraint where the dialect supports ALTER TABLE ADD CONSTRAINT;
/// dialects without that capability enforce the relationship through the
/// provider's foreign-key machinery at write time.
/// </summary>
/// <param name="TableName">Owning table name.</param>
/// <param name="ForeignKey">The additive relationship.</param>
public sealed record AddForeignKeyOperation(string TableName, ForeignKeyDefinition ForeignKey) : MigrationOperation;

/// <summary>
/// A versioned, provider-agnostic migration (FS-CRAWL-001 §2.4): a stable
/// version number, a stable name, and an ordered list of logical operations.
/// The same definition renders to the same logical schema on every dialect.
/// </summary>
/// <param name="Version">Monotonic schema version produced by this migration.</param>
/// <param name="Name">Stable migration name recorded in documentation.</param>
/// <param name="Operations">Ordered logical operations.</param>
public sealed record MigrationDefinition(
    int Version,
    string Name,
    IReadOnlyList<MigrationOperation> Operations);
