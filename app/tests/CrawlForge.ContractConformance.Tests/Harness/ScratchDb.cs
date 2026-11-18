using Microsoft.Data.Sqlite;

namespace CrawlForge.ContractConformance.Tests.Harness;

/// <summary>
/// Read-only inspector of the harness's OWN scratch database (never the real
/// %APPDATA%\CrawlForge data). Reading the scratch DB for conformance inspection
/// is explicitly allowed by the WP-08-03 brief; it is not an acceptance entry
/// point. Connections are read-only, non-pooled, and opened per query so the
/// live backend's WAL writer is never blocked.
/// </summary>
public sealed class ScratchDb
{
    private readonly string _connectionString;

    /// <summary>Creates an inspector for the scratch crawlforge.db.</summary>
    /// <param name="databasePath">Full path of the scratch SQLite file.</param>
    public ScratchDb(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
    }

    /// <summary>The inspected database file.</summary>
    public string DatabasePath { get; }

    /// <summary>All user table names (sqlite_master).</summary>
    /// <returns>Table names in creation order.</returns>
    public IReadOnlyList<string> TableNames() =>
        Query("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
            reader => reader.GetString(0));

    /// <summary>Column names of one table (PRAGMA table_info).</summary>
    /// <param name="table">Table name.</param>
    /// <returns>Column names in ordinal order.</returns>
    public IReadOnlyList<string> Columns(string table)
    {
        // PRAGMA does not accept parameters; the table name comes from the
        // locked schema catalog / sqlite_master, never from user input.
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>Runs a read-only query and materializes each row.</summary>
    /// <typeparam name="T">Row projection type.</typeparam>
    /// <param name="sql">SQL text (read-only).</param>
    /// <param name="project">Row projection.</param>
    /// <param name="parameters">Optional named parameters.</param>
    /// <returns>Projected rows.</returns>
    public IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> project, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(project(reader));
        }

        return rows;
    }

    /// <summary>Scalar query (first column of the first row, or null).</summary>
    /// <param name="sql">SQL text (read-only).</param>
    /// <param name="parameters">Optional named parameters.</param>
    /// <returns>The scalar value.</returns>
    public object? Scalar(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }
}
