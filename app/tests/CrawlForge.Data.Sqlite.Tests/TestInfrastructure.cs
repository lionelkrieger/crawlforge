using System.Data.Common;
using CrawlForge.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Sqlite.Tests;

/// <summary>
/// Private temporary embedded database for one test: unique directory, unique file,
/// no external services, fully offline. Cleanup clears SQLite connection pools so
/// Windows file handles are released before the directory is deleted.
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "crawlforge-sqlite-tests", Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(DirectoryPath, "test.db");
        Options = new SqliteProviderOptions { DatabasePath = DatabasePath };
    }

    public string DirectoryPath { get; }

    public string DatabasePath { get; }

    public SqliteProviderOptions Options { get; }

    public SqliteDbProvider CreateProvider() => new(Options);

    public SqliteConnectionFactory CreateFactory() => new(Options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of transient test artifacts.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of transient test artifacts.
        }
    }
}

/// <summary>Constant-SQL helpers for the test suites.</summary>
internal static class TestSql
{
    public static async Task<T> ScalarAsync<T>(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return (T)result!;
    }

    public static async Task<T?> ScalarOrNullAsync<T>(DbConnection connection, string sql)
        where T : struct
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is null || result is DBNull ? null : (T)result;
    }

    public static async Task<string?> ScalarStringOrNullAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is null || result is DBNull ? null : (string)result;
    }

    public static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task ExecuteWithParameterAsync(DbConnection connection, string sql, string parameterName, object value)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Float32 little-endian BLOB encoding helpers for vector tests.</summary>
internal static class TestVectors
{
    public static byte[] ToBlob(ReadOnlySpan<float> values)
    {
        var blob = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(i * sizeof(float), sizeof(float)), values[i]);
        }

        return blob;
    }
}
