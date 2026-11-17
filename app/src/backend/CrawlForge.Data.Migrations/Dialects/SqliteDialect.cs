using System.Data.Common;
using System.Globalization;
using CrawlForge.Data.Migrations.Schema;

namespace CrawlForge.Data.Migrations.Dialects;

/// <summary>
/// The embedded file-based dialect (SQLite-class, FS-CRAWL-001 §2.4 default
/// provider). Foreign keys are enforced per connection. The dialect has no
/// ALTER TABLE ADD CONSTRAINT, so additive relationships are applied through
/// the renderer's table-rebuild procedure. Timestamps are stored as canonical
/// fixed-format UTC text.
/// </summary>
public sealed class SqliteDialect : SqlDialect
{
    /// <inheritdoc />
    public override string Name => "sqlite";

    /// <inheritdoc />
    public override bool SupportsAlterTableAddForeignKey => false;

    /// <inheritdoc />
    public override IReadOnlyList<string> ConnectionInitializationStatements => ["PRAGMA foreign_keys = ON;"];

    /// <inheritdoc />
    public override string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <inheritdoc />
    public override async Task<bool> TableExistsAsync(DbConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> GetColumnNamesAsync(DbConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(tableName)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    /// <inheritdoc />
    public override DateTimeOffset ReadTimestamp(object value) => value switch
    {
        string text => ParseTimestampText(text),
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        DateTimeOffset instant => instant.ToUniversalTime(),
        _ => throw new InvalidOperationException($"Unsupported timestamp storage type '{value.GetType().Name}'."),
    };

    /// <inheritdoc />
    protected override string RenderType(ColumnDefinition column) => column.Type switch
    {
        LogicalType.Int64 => "INTEGER",
        LogicalType.Int32 => "INTEGER",
        LogicalType.Boolean => "INTEGER",
        LogicalType.Double => "REAL",
        LogicalType.Timestamp => "TEXT",
        LogicalType.Text => "TEXT",
        LogicalType.Json => "TEXT",
        LogicalType.Varchar => $"VARCHAR({column.Length})",
        _ => throw new InvalidOperationException($"Unknown logical type '{column.Type}'."),
    };

    /// <inheritdoc />
    protected override string CreateTableSuffix => string.Empty;

    /// <inheritdoc />
    protected override string RenderPrimaryKeyColumn(string quotedName) =>
        $"{quotedName} INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT";
}
