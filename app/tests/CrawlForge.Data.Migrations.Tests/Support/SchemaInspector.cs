using System.Text;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Migrations.Tests.Support;

/// <summary>One physical column of a live SQLite table.</summary>
/// <param name="Name">Column name.</param>
/// <param name="TypeName">Declared type name.</param>
/// <param name="NotNull">Whether the column rejects NULL.</param>
/// <param name="IsPrimaryKey">Whether the column is the primary key.</param>
public sealed record ColumnInfo(string Name, string TypeName, bool NotNull, bool IsPrimaryKey);

/// <summary>One column of a live index, with its sort direction.</summary>
/// <param name="Name">Column name.</param>
/// <param name="Descending">Whether the column is indexed descending.</param>
public sealed record IndexColumnInfo(string Name, bool Descending);

/// <summary>One live index of a table.</summary>
/// <param name="Name">Index name.</param>
/// <param name="Unique">Whether the index enforces uniqueness.</param>
/// <param name="Columns">Ordered key columns.</param>
public sealed record IndexInfo(string Name, bool Unique, IReadOnlyList<IndexColumnInfo> Columns);

/// <summary>One live foreign-key relationship of a table.</summary>
/// <param name="Column">Referencing column.</param>
/// <param name="ReferencedTable">Referenced table.</param>
/// <param name="ReferencedColumn">Referenced column.</param>
public sealed record ForeignKeyInfo(string Column, string ReferencedTable, string ReferencedColumn);

/// <summary>
/// Read-only introspection of the live SQLite schema (sqlite_master and
/// PRAGMA probes) used by the coverage and double-apply assertions.
/// </summary>
public static class SchemaInspector
{
    /// <summary>Lists user table names, excluding SQLite internal bookkeeping.</summary>
    /// <param name="connection">An open connection.</param>
    /// <returns>Ordered table names.</returns>
    public static async Task<IReadOnlyList<string>> GetTableNamesAsync(SqliteConnection connection) =>
        await GetObjectNamesAsync(connection, "table");

    /// <summary>Lists view names.</summary>
    /// <param name="connection">An open connection.</param>
    /// <returns>Ordered view names.</returns>
    public static async Task<IReadOnlyList<string>> GetViewNamesAsync(SqliteConnection connection) =>
        await GetObjectNamesAsync(connection, "view");

    /// <summary>Reads the physical columns of a table in declaration order.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="table">Table name.</param>
    /// <returns>Ordered column facts.</returns>
    public static async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(SqliteConnection connection, string table)
    {
        var columns = new List<ColumnInfo>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(new ColumnInfo(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) == 1,
                reader.GetInt64(5) == 1));
        }

        return columns;
    }

    /// <summary>Reads the explicit indexes of a table (auto-indexes excluded).</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="table">Table name.</param>
    /// <returns>Index facts with key columns and directions.</returns>
    public static async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(SqliteConnection connection, string table)
    {
        var indexes = new List<IndexInfo>();
        await using (var listCommand = connection.CreateCommand())
        {
            listCommand.CommandText = $"PRAGMA index_list(\"{table}\");";
            await using var listReader = await listCommand.ExecuteReaderAsync();
            while (await listReader.ReadAsync())
            {
                var name = listReader.GetString(1);
                var unique = listReader.GetInt64(2) == 1;
                var origin = listReader.GetString(3);
                if (origin != "c")
                {
                    // Skip unique-constraint auto-indexes and PK auto-indexes;
                    // explicit CREATE INDEX statements have origin 'c'.
                    continue;
                }

                indexes.Add(new IndexInfo(name, unique, await GetIndexColumnsAsync(connection, name)));
            }
        }

        return indexes;
    }

    /// <summary>Reads the foreign keys declared on a table.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="table">Table name.</param>
    /// <returns>Foreign-key facts.</returns>
    public static async Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(SqliteConnection connection, string table)
    {
        var keys = new List<ForeignKeyInfo>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add(new ForeignKeyInfo(reader.GetString(3), reader.GetString(2), reader.GetString(4)));
        }

        return keys;
    }

    /// <summary>
    /// Captures a normalized DDL snapshot of every user object (tables and
    /// indexes) for byte-level double-apply comparison.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <returns>The deterministic schema snapshot text.</returns>
    public static async Task<string> GetSchemaSnapshotAsync(SqliteConnection connection)
    {
        var snapshot = new StringBuilder();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT type, name, sql FROM sqlite_master " +
            "WHERE name NOT LIKE 'sqlite_%' AND name <> 'sqlite_sequence' " +
            "ORDER BY type, name;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            snapshot.Append(reader.GetString(0)).Append('|')
                .Append(reader.GetString(1)).Append('|')
                .Append(reader.IsDBNull(2) ? string.Empty : reader.GetString(2)).Append('\n');
        }

        return snapshot.ToString();
    }

    private static async Task<IReadOnlyList<string>> GetObjectNamesAsync(SqliteConnection connection, string kind)
    {
        var names = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT name FROM sqlite_master WHERE type = '{kind}' " +
            "AND name NOT LIKE 'sqlite_%' AND name <> 'sqlite_sequence' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<IReadOnlyList<IndexColumnInfo>> GetIndexColumnsAsync(SqliteConnection connection, string indexName)
    {
        var columns = new List<IndexColumnInfo>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_xinfo(\"{indexName}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // index_xinfo: seqno, cid, name, desc, coll, key. Only key columns.
            if (reader.GetInt64(5) != 1 || reader.IsDBNull(2))
            {
                continue;
            }

            columns.Add(new IndexColumnInfo(reader.GetString(2), reader.GetInt64(3) == 1));
        }

        return columns;
    }
}
