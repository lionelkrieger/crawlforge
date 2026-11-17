using System.Data.Common;
using System.Globalization;
using System.Text;
using CrawlForge.Data.Migrations.Schema;

namespace CrawlForge.Data.Migrations.Dialects;

/// <summary>
/// A provider dialect (FS-CRAWL-001 §2.4): renders the same provider-agnostic
/// logical schema definitions into dialect-specific SQL. Both dialects produce
/// the identical logical schema version — 40 objects, identical columns,
/// indexes, and relationships — with dialect-specific physical types.
/// </summary>
public abstract class SqlDialect
{
    /// <summary>
    /// The canonical fixed-format UTC representation used for timestamp
    /// parameters on every provider (lexicographically sortable, deterministic).
    /// </summary>
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.ffffff";

    /// <summary>Dialect identifier recorded in the migration ledger.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Whether the dialect supports ALTER TABLE ADD CONSTRAINT for additive
    /// foreign keys. The embedded dialect does not; the renderer applies the
    /// standard table-rebuild procedure there instead.
    /// </summary>
    public abstract bool SupportsAlterTableAddForeignKey { get; }

    /// <summary>
    /// Statements executed on every freshly opened migration connection before
    /// any schema work (for example, enabling foreign-key enforcement).
    /// </summary>
    public abstract IReadOnlyList<string> ConnectionInitializationStatements { get; }

    /// <summary>Quotes an identifier for the dialect.</summary>
    /// <param name="identifier">Table, column, or index name.</param>
    /// <returns>The quoted identifier.</returns>
    public abstract string Quote(string identifier);

    /// <summary>Checks whether a table exists, without creating anything.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="tableName">Table name to probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the table exists.</returns>
    public abstract Task<bool> TableExistsAsync(DbConnection connection, string tableName, CancellationToken cancellationToken);

    /// <summary>Lists the physical column names of a table in declaration order.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="tableName">Table name to probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered column names (empty when the table does not exist).</returns>
    public abstract Task<IReadOnlyList<string>> GetColumnNamesAsync(DbConnection connection, string tableName, CancellationToken cancellationToken);

    /// <summary>Converts a stored timestamp value to a UTC instant.</summary>
    /// <param name="value">Raw provider value.</param>
    /// <returns>The UTC timestamp.</returns>
    public abstract DateTimeOffset ReadTimestamp(object value);

    /// <summary>Converts a UTC instant to a dialect parameter value.</summary>
    /// <param name="value">UTC timestamp.</param>
    /// <returns>Parameter value accepted by the provider.</returns>
    public virtual object WriteTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>Renders the physical type of a logical column.</summary>
    /// <param name="column">The logical column.</param>
    /// <returns>Dialect-specific type name.</returns>
    protected abstract string RenderType(ColumnDefinition column);

    /// <summary>Dialect-specific CREATE TABLE suffix (storage engine, charset).</summary>
    protected abstract string CreateTableSuffix { get; }

    /// <summary>Renders the surrogate primary-key column definition.</summary>
    /// <param name="quotedName">Quoted primary-key column name.</param>
    /// <returns>Column-level primary-key fragment.</returns>
    protected abstract string RenderPrimaryKeyColumn(string quotedName);

    /// <summary>
    /// Renders an optional table-level primary-key constraint (dialects that
    /// cannot express AUTO_INCREMENT at column level use this).
    /// </summary>
    /// <param name="quotedName">Quoted primary-key column name.</param>
    /// <returns>Table-level constraint, or null when not needed.</returns>
    protected virtual string? RenderTableLevelPrimaryKey(string quotedName) => null;

    /// <summary>
    /// Renders a CREATE TABLE statement for a logical table, including its
    /// indexes' owning constraints and foreign keys.
    /// </summary>
    /// <param name="table">The logical table.</param>
    /// <param name="effectiveForeignKeys">Foreign keys to declare inline.</param>
    /// <param name="ifNotExists">Emit IF NOT EXISTS (ledger bootstrap only).</param>
    /// <param name="constraintOwner">
    /// Owning-table name used for foreign-key constraint naming; defaults to the
    /// rendered table name. Table-rebuild rendering passes the original name so
    /// constraint names stay stable across the swap.
    /// </param>
    /// <returns>The CREATE TABLE statement.</returns>
    public string RenderCreateTable(
        TableDefinition table,
        IReadOnlyList<ForeignKeyDefinition> effectiveForeignKeys,
        bool ifNotExists,
        string? constraintOwner = null)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ");
        if (ifNotExists)
        {
            sb.Append("IF NOT EXISTS ");
        }

        sb.Append(Quote(table.Name)).Append(" (");
        var parts = new List<string>
        {
            RenderPrimaryKeyColumn(Quote(TableDefinition.PrimaryKeyName)),
        };

        foreach (var column in table.Columns.Where(c => c.Name != TableDefinition.PrimaryKeyName))
        {
            parts.Add($"{Quote(column.Name)} {RenderType(column)} {(column.Nullable ? "NULL" : "NOT NULL")}");
        }

        if (RenderTableLevelPrimaryKey(Quote(TableDefinition.PrimaryKeyName)) is { } tablePk)
        {
            parts.Add(tablePk);
        }

        var owner = constraintOwner ?? table.Name;
        foreach (var fk in effectiveForeignKeys)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"CONSTRAINT {Quote($"fk_{owner}_{fk.Column}")} FOREIGN KEY ({Quote(fk.Column)}) REFERENCES {Quote(fk.ReferencedTable)} ({Quote(fk.ReferencedColumn)})"));
        }

        sb.Append(string.Join(", ", parts)).Append(')').Append(CreateTableSuffix).Append(';');
        return sb.ToString();
    }

    /// <summary>Renders an additive ALTER TABLE ADD COLUMN statement.</summary>
    /// <param name="tableName">Target table.</param>
    /// <param name="column">The additive column (always nullable).</param>
    /// <returns>The ALTER TABLE statement.</returns>
    public string RenderAddColumn(string tableName, ColumnDefinition column) =>
        $"ALTER TABLE {Quote(tableName)} ADD COLUMN {Quote(column.Name)} {RenderType(column)} {(column.Nullable ? "NULL" : "NOT NULL")};";

    /// <summary>Renders a CREATE INDEX statement (unique and descending aware).</summary>
    /// <param name="index">The logical index.</param>
    /// <returns>The CREATE INDEX statement.</returns>
    public string RenderCreateIndex(IndexDefinition index)
    {
        var columns = string.Join(", ", index.Columns.Select(c =>
            Quote(c.Name) + (c.Descending ? " DESC" : string.Empty)));
        var unique = index.Unique ? "UNIQUE " : string.Empty;
        return $"CREATE {unique}INDEX {Quote(index.Name)} ON {Quote(index.TableName)} ({columns});";
    }

    /// <summary>
    /// Renders an additive foreign-key constraint as ALTER TABLE ADD CONSTRAINT.
    /// Dialects without that capability return no statements; the renderer
    /// applies the table-rebuild procedure for them instead.
    /// </summary>
    /// <param name="tableName">Owning table.</param>
    /// <param name="foreignKey">The additive relationship.</param>
    /// <returns>Zero or one ALTER TABLE statements.</returns>
    public IReadOnlyList<string> RenderAddForeignKey(string tableName, ForeignKeyDefinition foreignKey)
    {
        if (!SupportsAlterTableAddForeignKey)
        {
            return [];
        }

        return
        [
            $"ALTER TABLE {Quote(tableName)} ADD CONSTRAINT {Quote($"fk_{tableName}_{foreignKey.Column}")} FOREIGN KEY ({Quote(foreignKey.Column)}) REFERENCES {Quote(foreignKey.ReferencedTable)} ({Quote(foreignKey.ReferencedColumn)});",
        ];
    }

    /// <summary>Parses the canonical UTC timestamp text representation.</summary>
    /// <param name="text">Fixed-format UTC timestamp text.</param>
    /// <returns>The UTC instant.</returns>
    protected static DateTimeOffset ParseTimestampText(string text) =>
        DateTimeOffset.ParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
