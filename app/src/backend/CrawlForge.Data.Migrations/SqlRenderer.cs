using CrawlForge.Data.Migrations.Dialects;
using CrawlForge.Data.Migrations.Schema;

namespace CrawlForge.Data.Migrations;

/// <summary>
/// Renders provider-agnostic migration operations into dialect-specific SQL
/// statements. The logical definitions are identical for every provider; only
/// the physical rendering differs (FS-CRAWL-002 §1.4 rule 6).
/// </summary>
public sealed class SqlRenderer
{
    private readonly SqlDialect _dialect;

    /// <summary>Creates a renderer for a dialect.</summary>
    /// <param name="dialect">The target dialect.</param>
    public SqlRenderer(SqlDialect dialect)
    {
        _dialect = dialect;
    }

    /// <summary>The dialect this renderer targets.</summary>
    public SqlDialect Dialect => _dialect;

    /// <summary>Renders one migration operation into zero or more SQL statements.</summary>
    /// <param name="operation">The logical operation.</param>
    /// <returns>Ordered SQL statements to execute inside the migration transaction.</returns>
    public IReadOnlyList<string> RenderOperation(MigrationOperation operation) => operation switch
    {
        CreateTableOperation create => RenderCreateTable(create.Table),
        AddColumnOperation addColumn => [_dialect.RenderAddColumn(addColumn.TableName, addColumn.Column)],
        CreateIndexOperation createIndex => [_dialect.RenderCreateIndex(createIndex.Index)],
        AddForeignKeyOperation addFk => RenderAddForeignKey(addFk.TableName, addFk.ForeignKey),
        _ => throw new InvalidOperationException($"Unknown migration operation '{operation.GetType().Name}'."),
    };

    /// <summary>Renders a complete migration into ordered SQL statements.</summary>
    /// <param name="migration">The logical migration.</param>
    /// <returns>Ordered SQL statements.</returns>
    public IReadOnlyList<string> RenderMigration(MigrationDefinition migration) =>
        migration.Operations.SelectMany(RenderOperation).ToList();

    /// <summary>
    /// Renders the idempotent ledger bootstrap (CREATE TABLE IF NOT EXISTS).
    /// The ledger exists before version 1 applies so every applied version can
    /// be recorded transactionally.
    /// </summary>
    /// <returns>The bootstrap statement.</returns>
    public string RenderLedgerBootstrap()
    {
        var ledger = SchemaCatalog.BaselineTables.Single(t => t.Name == SchemaCatalog.LedgerTableName);
        return _dialect.RenderCreateTable(ledger, ledger.ForeignKeys, ifNotExists: true);
    }

    private IReadOnlyList<string> RenderCreateTable(TableDefinition table)
    {
        // The ledger is bootstrapped before version 1 applies, so its creation
        // inside the baseline migration is idempotent by design; its unique
        // index is still created by the migration itself.
        var ifNotExists = table.Name == SchemaCatalog.LedgerTableName;
        var statements = new List<string>
        {
            _dialect.RenderCreateTable(table, table.ForeignKeys, ifNotExists),
        };
        statements.AddRange(table.Indexes.Select(_dialect.RenderCreateIndex));
        return statements;
    }

    private IReadOnlyList<string> RenderAddForeignKey(string tableName, ForeignKeyDefinition foreignKey)
    {
        if (_dialect.SupportsAlterTableAddForeignKey)
        {
            return _dialect.RenderAddForeignKey(tableName, foreignKey);
        }

        return RenderTableRebuildWithForeignKey(tableName);
    }

    /// <summary>
    /// The embedded dialect cannot ALTER TABLE ADD CONSTRAINT, so an additive
    /// relationship is applied through the standard table-rebuild procedure:
    /// create the table in its catalog shape AS OF THIS MIGRATION VERSION under
    /// a temporary name, copy the rows, drop the original, rename, and recreate
    /// its indexes. The only foreign-key rebuild rides version 3, so the
    /// version-3 shape (AdditiveVersionTables) is used — the FINAL catalog shape
    /// would reference columns that later versions add, which do not exist yet
    /// when the rebuild SELECT runs on a fresh database. Migrations apply before
    /// data ingestion, and the whole rebuild runs inside the migration
    /// transaction, so the swap is atomic.
    /// </summary>
    private IReadOnlyList<string> RenderTableRebuildWithForeignKey(string tableName)
    {
        var rebuildTable = SchemaCatalog.AdditiveVersionTables.Single(t => t.Name == tableName);
        var tempName = "__cf_rebuild_" + tableName;
        var tempTable = rebuildTable with { Name = tempName };
        var columns = string.Join(", ", rebuildTable.Columns.Select(c => _dialect.Quote(c.Name)));

        var statements = new List<string>
        {
            // Constraint names keep the original owning-table prefix.
            _dialect.RenderCreateTable(tempTable, rebuildTable.ForeignKeys, ifNotExists: false, constraintOwner: tableName),
            $"INSERT INTO {_dialect.Quote(tempName)} ({columns}) SELECT {columns} FROM {_dialect.Quote(tableName)};",
            $"DROP TABLE {_dialect.Quote(tableName)};",
            $"ALTER TABLE {_dialect.Quote(tempName)} RENAME TO {_dialect.Quote(tableName)};",
        };
        statements.AddRange(rebuildTable.Indexes.Select(_dialect.RenderCreateIndex));
        return statements;
    }
}
