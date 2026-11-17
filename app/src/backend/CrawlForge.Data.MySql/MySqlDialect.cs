using System.Globalization;

namespace CrawlForge.Data.MySql;

/// <summary>
/// MySQL dialect adaptation for the provider-agnostic logical schema (FS-CRAWL-001
/// §2.4, precedence rule 6). Renders the dialect-specific SQL forms the logical
/// schema requires: identifier quoting, paging, upserts, regular-expression
/// predicates, in-database delimited-text import/export, vector similarity over
/// stored embeddings, and extended statistical functions. Every generated statement
/// is plain SQL text; values always travel as parameters, never concatenated.
/// </summary>
public static class MySqlDialect
{
    /// <summary>Dialect identifier recorded in the migration ledger.</summary>
    public const string DialectName = "mysql";

    /// <summary>
    /// Quotes a logical identifier with MySQL backticks. Internal backticks are
    /// doubled. SQLite accepts the same backtick form, which keeps shared read-model
    /// SQL provider-neutral.
    /// </summary>
    /// <param name="identifier">Logical identifier (table, column, index name).</param>
    /// <returns>The quoted identifier.</returns>
    /// <exception cref="ArgumentException">The identifier is null, empty, or whitespace.</exception>
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    /// <summary>
    /// Quotes a string literal with MySQL escaping (backslash and single quote).
    /// Prefer parameters for values; this helper exists for DDL-adjacent literals
    /// such as file paths in delimited-text statements.
    /// </summary>
    /// <param name="value">Literal value.</param>
    /// <returns>The quoted literal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static string QuoteLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    }

    /// <summary>Renders MySQL paging (LIMIT/OFFSET) for read-model list queries.</summary>
    /// <param name="limit">Maximum row count (non-negative).</param>
    /// <param name="offset">Row offset (non-negative).</param>
    /// <returns>The paging clause.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Limit or offset is negative.</exception>
    public static string PagingSql(int limit, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"LIMIT {limit} OFFSET {offset}");
    }

    /// <summary>
    /// Renders a regular-expression predicate (required database capability, §2.4).
    /// MySQL 8.0 supplies <c>REGEXP_LIKE</c> natively; the pattern travels as a
    /// parameter.
    /// </summary>
    /// <param name="columnExpression">Column or expression to test.</param>
    /// <param name="patternParameterName">Parameter carrying the pattern (must start with '@').</param>
    /// <returns>The predicate expression.</returns>
    /// <exception cref="ArgumentException">An argument is invalid.</exception>
    public static string RegexPredicateSql(string columnExpression, string patternParameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(patternParameterName);
        if (!patternParameterName.StartsWith('@'))
        {
            throw new ArgumentException("The pattern parameter name must start with '@'.", nameof(patternParameterName));
        }

        return $"REGEXP_LIKE({columnExpression}, {patternParameterName})";
    }

    /// <summary>
    /// Renders an idempotent upsert for aggregate writes: INSERT with an
    /// ON DUPLICATE KEY UPDATE fallback on the logical unique key. Used by
    /// precomputed read models (§8.1 pattern) so refreshes never duplicate rows.
    /// </summary>
    /// <param name="table">Logical table name.</param>
    /// <param name="keyColumns">Columns forming the logical unique key.</param>
    /// <param name="valueColumns">Value columns refreshed on conflict.</param>
    /// <returns>The upsert statement with one parameter per column.</returns>
    /// <exception cref="ArgumentException">An argument is invalid.</exception>
    public static string UpsertSql(string table, IReadOnlyList<string> keyColumns, IReadOnlyList<string> valueColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(valueColumns);
        if (keyColumns.Count == 0)
        {
            throw new ArgumentException("At least one key column is required.", nameof(keyColumns));
        }

        if (valueColumns.Count == 0)
        {
            throw new ArgumentException("At least one value column is required.", nameof(valueColumns));
        }

        List<string> allColumns = [.. keyColumns, .. valueColumns];
        string columns = string.Join(", ", allColumns.Select(QuoteIdentifier));
        string parameters = string.Join(", ", allColumns.Select(c => "@" + c));
        string updates = string.Join(
            ", ",
            valueColumns.Select(c => $"{QuoteIdentifier(c)} = VALUES({QuoteIdentifier(c)})"));

        return $"INSERT INTO {QuoteIdentifier(table)} ({columns}) VALUES ({parameters}) ON DUPLICATE KEY UPDATE {updates}";
    }

    /// <summary>
    /// Renders in-database delimited-text import (required capability, §2.4):
    /// LOAD DATA LOCAL INFILE. The client must explicitly enable
    /// <c>AllowLoadLocalInfile</c> on the connection; the default factory keeps it
    /// disabled so a user-owned server can never pull local files unprompted.
    /// </summary>
    /// <param name="table">Target logical table name.</param>
    /// <param name="localFilePath">Local delimited file path.</param>
    /// <param name="fieldDelimiter">Field delimiter character.</param>
    /// <returns>The import statement.</returns>
    /// <exception cref="ArgumentException">An argument is invalid.</exception>
    public static string DelimitedImportSql(string table, string localFilePath, char fieldDelimiter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"LOAD DATA LOCAL INFILE {QuoteLiteral(localFilePath)} INTO TABLE {QuoteIdentifier(table)} FIELDS TERMINATED BY {QuoteLiteral(fieldDelimiter.ToString())} ENCLOSED BY '\"' LINES TERMINATED BY '\\n'");
    }

    /// <summary>
    /// Renders in-database delimited-text export (required capability, §2.4):
    /// a SELECT statement extended with INTO OUTFILE on the server. The file is
    /// written by the user's own server, never by a vendor service.
    /// </summary>
    /// <param name="selectSql">Complete SELECT statement without INTO clause.</param>
    /// <param name="serverFilePath">Destination file path on the server.</param>
    /// <param name="fieldDelimiter">Field delimiter character.</param>
    /// <returns>The export statement.</returns>
    /// <exception cref="ArgumentException">An argument is invalid.</exception>
    public static string DelimitedExportSql(string selectSql, string serverFilePath, char fieldDelimiter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectSql);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverFilePath);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{selectSql} INTO OUTFILE {QuoteLiteral(serverFilePath)} FIELDS TERMINATED BY {QuoteLiteral(fieldDelimiter.ToString())} ENCLOSED BY '\"' LINES TERMINATED BY '\\n'");
    }

    /// <summary>
    /// Renders cosine similarity over two stored embedding vectors (required
    /// capability, §2.4). MySQL has no native vector type, so embeddings are stored
    /// as JSON arrays and expanded with <c>JSON_TABLE</c> (MySQL 8.0 native). The
    /// expression returns NULL for a zero-length vector via NULLIF. Both arguments
    /// are SQL expressions yielding JSON arrays (parameters or columns).
    /// </summary>
    /// <param name="vectorAExpression">SQL expression yielding the first JSON array.</param>
    /// <param name="vectorBExpression">SQL expression yielding the second JSON array.</param>
    /// <returns>A scalar subquery computing cosine similarity.</returns>
    /// <exception cref="ArgumentException">An argument is invalid.</exception>
    public static string CosineSimilaritySql(string vectorAExpression, string vectorBExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorAExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorBExpression);

        return
            "(SELECT SUM(`a`.`v` * `b`.`v`) / NULLIF(SQRT(SUM(`a`.`v` * `a`.`v`)) * SQRT(SUM(`b`.`v` * `b`.`v`)), 0) " +
            $"FROM JSON_TABLE({vectorAExpression}, '$[*]' COLUMNS (`ord` FOR ORDINALITY, `v` DOUBLE PATH '$')) AS `a` " +
            $"JOIN JSON_TABLE({vectorBExpression}, '$[*]' COLUMNS (`ord` FOR ORDINALITY, `v` DOUBLE PATH '$')) AS `b` ON `b`.`ord` = `a`.`ord`)";
    }

    /// <summary>Renders sample standard deviation (extended statistical capability, §2.4).</summary>
    /// <param name="columnExpression">Numeric column or expression.</param>
    /// <returns>The aggregate expression.</returns>
    /// <exception cref="ArgumentException">The argument is invalid.</exception>
    public static string StandardDeviationSql(string columnExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnExpression);
        return $"STDDEV_SAMP({columnExpression})";
    }

    /// <summary>Renders sample variance (extended statistical capability, §2.4).</summary>
    /// <param name="columnExpression">Numeric column or expression.</param>
    /// <returns>The aggregate expression.</returns>
    /// <exception cref="ArgumentException">The argument is invalid.</exception>
    public static string VarianceSql(string columnExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnExpression);
        return $"VAR_SAMP({columnExpression})";
    }

    /// <summary>
    /// UTC timestamp expression for the UTC-timestamp convention (§9.1). MySQL
    /// DATETIME carries no zone; every stored timestamp is written as UTC.
    /// </summary>
    public static string UtcNowSql => "UTC_TIMESTAMP(6)";

    /// <summary>
    /// Maps a logical column type to its MySQL rendering (§9.1 conventions:
    /// surrogate integer keys, UTC timestamps, JSON text columns). Both providers
    /// implement the same logical types; this is the MySQL dialect half.
    /// </summary>
    /// <param name="kind">Logical column type.</param>
    /// <param name="nullable">Whether the column admits NULL.</param>
    /// <returns>The MySQL type clause.</returns>
    public static string MapColumnType(MySqlLogicalColumnType kind, bool nullable = false)
    {
        string nullability = nullable ? " NULL" : " NOT NULL";
        return kind switch
        {
            MySqlLogicalColumnType.SurrogateKey => "BIGINT NOT NULL AUTO_INCREMENT",
            MySqlLogicalColumnType.ForeignKey => "BIGINT" + nullability,
            MySqlLogicalColumnType.Integer => "INT" + nullability,
            MySqlLogicalColumnType.BigInt => "BIGINT" + nullability,
            MySqlLogicalColumnType.Double => "DOUBLE" + nullability,
            MySqlLogicalColumnType.Boolean => "TINYINT(1)" + nullability,
            MySqlLogicalColumnType.Text => "VARCHAR(255)" + nullability,
            MySqlLogicalColumnType.LongText => "LONGTEXT" + nullability,
            MySqlLogicalColumnType.Json => "JSON" + nullability,
            MySqlLogicalColumnType.TimestampUtc => "DATETIME(6)" + nullability,
            MySqlLogicalColumnType.Blob => "LONGBLOB" + nullability,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown logical column type."),
        };
    }
}

/// <summary>
/// Provider-neutral logical column types (FS-CRAWL-001 §9.1 conventions). Each
/// provider maps them to its own physical types; the logical meaning is identical.
/// </summary>
public enum MySqlLogicalColumnType
{
    /// <summary>Surrogate integer key (auto-increment).</summary>
    SurrogateKey = 0,

    /// <summary>Surrogate key reference to another table.</summary>
    ForeignKey = 1,

    /// <summary>32-bit integer.</summary>
    Integer = 2,

    /// <summary>64-bit integer.</summary>
    BigInt = 3,

    /// <summary>Double-precision floating point.</summary>
    Double = 4,

    /// <summary>Boolean flag.</summary>
    Boolean = 5,

    /// <summary>Bounded text (255 characters).</summary>
    Text = 6,

    /// <summary>Unbounded text.</summary>
    LongText = 7,

    /// <summary>JSON payload column.</summary>
    Json = 8,

    /// <summary>UTC timestamp with microsecond precision.</summary>
    TimestampUtc = 9,

    /// <summary>Binary payload.</summary>
    Blob = 10,
}
