namespace CrawlForge.Data.Migrations.Schema;

/// <summary>
/// Provider-agnostic logical column types (FS-CRAWL-001 §9.1 conventions,
/// FS-CRAWL-002 §1.4 rule 6). Each dialect maps a logical type to its physical
/// storage type; the logical schema is identical on every provider.
/// </summary>
public enum LogicalType
{
    /// <summary>64-bit signed integer (surrogate keys, counters).</summary>
    Int64,

    /// <summary>32-bit signed integer (enum ordinals, small counters).</summary>
    Int32,

    /// <summary>Boolean flag.</summary>
    Boolean,

    /// <summary>Double-precision floating point (rates, scores, similarities).</summary>
    Double,

    /// <summary>UTC timestamp, stored in a canonical fixed-format UTC representation.</summary>
    Timestamp,

    /// <summary>Unbounded text (never part of an index).</summary>
    Text,

    /// <summary>JSON text payload for flexible structured data (§9.1).</summary>
    Json,

    /// <summary>Bounded character string; indexable in every dialect.</summary>
    Varchar,
}

/// <summary>
/// One logical column of a schema object. Columns are additive-only once
/// published (precedence rule 3): names are never renamed or repurposed.
/// </summary>
/// <param name="Name">Stable snake_case column name.</param>
/// <param name="Type">Provider-agnostic logical type.</param>
/// <param name="Nullable">Whether the column admits NULL.</param>
/// <param name="Length">Character bound for <see cref="LogicalType.Varchar"/>; zero otherwise.</param>
public sealed record ColumnDefinition(
    string Name,
    LogicalType Type,
    bool Nullable,
    int Length = 0);

/// <summary>
/// A foreign-key relationship between two logical schema objects
/// (FS-CRAWL-001 §9.3). Foreign keys are enforced on every provider (§2.4).
/// </summary>
/// <param name="Column">Referencing column on the owning table.</param>
/// <param name="ReferencedTable">Referenced table name.</param>
/// <param name="ReferencedColumn">Referenced column (surrogate key).</param>
public sealed record ForeignKeyDefinition(
    string Column,
    string ReferencedTable,
    string ReferencedColumn);

/// <summary>
/// One column of an index, with an optional descending sort direction
/// (required for the DF-descending and similarity-descending read paths).
/// </summary>
/// <param name="Name">Indexed column name.</param>
/// <param name="Descending">Whether the column is indexed in descending order.</param>
public sealed record IndexColumn(string Name, bool Descending = false);

/// <summary>
/// A named index over a logical table (FS-CRAWL-001 §9.3 and FS-CRAWL-002
/// Appendix A required indexes). Index names are stable across dialects.
/// </summary>
/// <param name="Name">Stable index name.</param>
/// <param name="TableName">Owning table name.</param>
/// <param name="Columns">Ordered index columns.</param>
/// <param name="Unique">Whether the index enforces uniqueness.</param>
public sealed record IndexDefinition(
    string Name,
    string TableName,
    IReadOnlyList<IndexColumn> Columns,
    bool Unique);

/// <summary>
/// A logical table: columns, surrogate primary key, relationships, and indexes.
/// Every table uses a surrogate integer key named <c>id</c> (§9.1).
/// </summary>
/// <param name="Name">Stable snake_case table name.</param>
/// <param name="Columns">Ordered column definitions (surrogate key first).</param>
/// <param name="ForeignKeys">Foreign-key relationships of the table.</param>
/// <param name="Indexes">Indexes owned by the table.</param>
public sealed record TableDefinition(
    string Name,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<ForeignKeyDefinition> ForeignKeys,
    IReadOnlyList<IndexDefinition> Indexes)
{
    /// <summary>Name of the surrogate primary-key column shared by every table.</summary>
    public const string PrimaryKeyName = "id";
}
