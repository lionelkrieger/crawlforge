using System.Data.Common;
using System.Globalization;
using CrawlForge.Data.Migrations.Schema;

namespace CrawlForge.Data.Migrations.Dialects;

/// <summary>
/// The optional user-owned external client-server dialect (MySQL-class,
/// FS-CRAWL-001 §2.4). InnoDB with utf8mb4; foreign keys are enforced natively
/// and additive relationships are added through ALTER TABLE ADD CONSTRAINT.
/// Indexable strings use bounded VARCHAR columns so composite indexes stay
/// within the InnoDB index-key limit; JSON payloads use LONGTEXT. Timestamps
/// use DATETIME(6) in UTC. No vendor-hosted database exists or is contacted.
/// </summary>
public sealed class MySqlDialect : SqlDialect
{
    /// <inheritdoc />
    public override string Name => "mysql";

    /// <inheritdoc />
    public override bool SupportsAlterTableAddForeignKey => true;

    /// <inheritdoc />
    public override IReadOnlyList<string> ConnectionInitializationStatements => [];

    /// <inheritdoc />
    public override string Quote(string identifier) => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

    /// <inheritdoc />
    public override async Task<bool> TableExistsAsync(DbConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @name;";
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
        command.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @name ORDER BY ordinal_position;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <inheritdoc />
    public override DateTimeOffset ReadTimestamp(object value) => value switch
    {
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        string text => ParseTimestampText(text),
        DateTimeOffset instant => instant.ToUniversalTime(),
        _ => throw new InvalidOperationException($"Unsupported timestamp storage type '{value.GetType().Name}'."),
    };

    /// <inheritdoc />
    protected override string RenderType(ColumnDefinition column) => column.Type switch
    {
        LogicalType.Int64 => "BIGINT",
        LogicalType.Int32 => "INT",
        LogicalType.Boolean => "TINYINT(1)",
        LogicalType.Double => "DOUBLE",
        LogicalType.Timestamp => "DATETIME(6)",
        LogicalType.Text => "TEXT",
        LogicalType.Json => "LONGTEXT",
        LogicalType.Varchar => $"VARCHAR({column.Length})",
        _ => throw new InvalidOperationException($"Unknown logical type '{column.Type}'."),
    };

    /// <inheritdoc />
    protected override string CreateTableSuffix => " ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";

    /// <inheritdoc />
    protected override string RenderPrimaryKeyColumn(string quotedName) =>
        $"{quotedName} BIGINT NOT NULL AUTO_INCREMENT";

    /// <inheritdoc />
    protected override string? RenderTableLevelPrimaryKey(string quotedName) =>
        $"PRIMARY KEY ({quotedName})";
}
