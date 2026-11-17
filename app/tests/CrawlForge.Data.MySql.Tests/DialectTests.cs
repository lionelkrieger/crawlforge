using Xunit;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Dialect conformance: the MySQL rendering of every required database capability
/// (§2.4) and schema convention (§9.1) — quoting, paging, upserts, regex predicates,
/// delimited-text import/export, vector similarity, statistics, and type mapping.
/// </summary>
public sealed class DialectTests
{
    /// <summary>Identifiers are backtick-quoted with internal backticks doubled.</summary>
    [Fact]
    public void Identifier_Quoting()
    {
        Assert.Equal("`url_records`", MySqlDialect.QuoteIdentifier("url_records"));
        Assert.Equal("`odd``name`", MySqlDialect.QuoteIdentifier("odd`name"));
        Assert.Throws<ArgumentException>(() => MySqlDialect.QuoteIdentifier(" "));
    }

    /// <summary>String literals escape backslashes and single quotes.</summary>
    [Fact]
    public void Literal_Quoting()
    {
        Assert.Equal("'plain'", MySqlDialect.QuoteLiteral("plain"));
        Assert.Equal("'it\\'s'", MySqlDialect.QuoteLiteral("it's"));
        Assert.Equal("'a\\\\b'", MySqlDialect.QuoteLiteral("a\\b"));
    }

    /// <summary>Paging renders LIMIT/OFFSET with invariant numbers.</summary>
    [Fact]
    public void Paging_Rendering()
    {
        Assert.Equal("LIMIT 50 OFFSET 100", MySqlDialect.PagingSql(50, 100));
        Assert.Equal("LIMIT 0 OFFSET 0", MySqlDialect.PagingSql(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MySqlDialect.PagingSql(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MySqlDialect.PagingSql(10, -5));
    }

    /// <summary>Regex predicates use native REGEXP_LIKE with a parameterized pattern.</summary>
    [Fact]
    public void Regex_Predicate_Rendering()
    {
        Assert.Equal(
            "REGEXP_LIKE(`canonical_url`, @pattern)",
            MySqlDialect.RegexPredicateSql("`canonical_url`", "@pattern"));
        Assert.Throws<ArgumentException>(() => MySqlDialect.RegexPredicateSql("`x`", "pattern"));
    }

    /// <summary>Upserts render INSERT ... ON DUPLICATE KEY UPDATE with one parameter per column.</summary>
    [Fact]
    public void Upsert_Rendering()
    {
        string sql = MySqlDialect.UpsertSql(
            "metric_summaries",
            keyColumns: ["audit_id", "metric_key"],
            valueColumns: ["metric_value"]);

        Assert.StartsWith("INSERT INTO `metric_summaries`", sql, StringComparison.Ordinal);
        Assert.Contains("(`audit_id`, `metric_key`, `metric_value`)", sql, StringComparison.Ordinal);
        Assert.Contains("VALUES (@audit_id, @metric_key, @metric_value)", sql, StringComparison.Ordinal);
        Assert.Contains("ON DUPLICATE KEY UPDATE `metric_value` = VALUES(`metric_value`)", sql, StringComparison.Ordinal);
    }

    /// <summary>Upserts require both key and value columns.</summary>
    [Fact]
    public void Upsert_Validates_Columns()
    {
        Assert.Throws<ArgumentException>(() => MySqlDialect.UpsertSql("t", [], ["v"]));
        Assert.Throws<ArgumentException>(() => MySqlDialect.UpsertSql("t", ["k"], []));
    }

    /// <summary>Delimited import renders LOAD DATA LOCAL INFILE with the quoted local path.</summary>
    [Fact]
    public void Delimited_Import_Rendering()
    {
        string sql = MySqlDialect.DelimitedImportSql("url_records", @"C:\data\urls.csv", ',');

        Assert.Contains("LOAD DATA LOCAL INFILE 'C:\\\\data\\\\urls.csv'", sql, StringComparison.Ordinal);
        Assert.Contains("INTO TABLE `url_records`", sql, StringComparison.Ordinal);
        Assert.Contains("FIELDS TERMINATED BY ','", sql, StringComparison.Ordinal);
        Assert.Contains("LINES TERMINATED BY '\\n'", sql, StringComparison.Ordinal);
    }

    /// <summary>Delimited export renders SELECT ... INTO OUTFILE on the user's server.</summary>
    [Fact]
    public void Delimited_Export_Rendering()
    {
        string sql = MySqlDialect.DelimitedExportSql("SELECT `canonical_url` FROM `url_records`", "/tmp/urls.csv", ';');

        Assert.StartsWith("SELECT `canonical_url` FROM `url_records` INTO OUTFILE", sql, StringComparison.Ordinal);
        Assert.Contains("'/tmp/urls.csv'", sql, StringComparison.Ordinal);
        Assert.Contains("FIELDS TERMINATED BY ';'", sql, StringComparison.Ordinal);
    }

    /// <summary>Vector similarity expands JSON arrays with JSON_TABLE and guards zero division.</summary>
    [Fact]
    public void Cosine_Similarity_Rendering()
    {
        string sql = MySqlDialect.CosineSimilaritySql("@vectorA", "@vectorB");

        Assert.Contains("JSON_TABLE(@vectorA, '$[*]'", sql, StringComparison.Ordinal);
        Assert.Contains("JSON_TABLE(@vectorB, '$[*]'", sql, StringComparison.Ordinal);
        Assert.Contains("FOR ORDINALITY", sql, StringComparison.Ordinal);
        Assert.Contains("NULLIF(", sql, StringComparison.Ordinal);
        Assert.Contains("SQRT(", sql, StringComparison.Ordinal);
        Assert.Contains("`b`.`ord` = `a`.`ord`", sql, StringComparison.Ordinal);
    }

    /// <summary>Statistical aggregates use MySQL's sample stddev/variance functions.</summary>
    [Fact]
    public void Statistical_Function_Rendering()
    {
        Assert.Equal("STDDEV_SAMP(`response_ms`)", MySqlDialect.StandardDeviationSql("`response_ms`"));
        Assert.Equal("VAR_SAMP(`page_weight`)", MySqlDialect.VarianceSql("`page_weight`"));
    }

    /// <summary>UTC timestamps use microsecond-precision UTC_TIMESTAMP.</summary>
    [Fact]
    public void Utc_Timestamp_Rendering()
    {
        Assert.Equal("UTC_TIMESTAMP(6)", MySqlDialect.UtcNowSql);
    }

    /// <summary>Logical column types map to the MySQL physical conventions.</summary>
    [Theory]
    [InlineData(MySqlLogicalColumnType.SurrogateKey, false, "BIGINT NOT NULL AUTO_INCREMENT")]
    [InlineData(MySqlLogicalColumnType.ForeignKey, false, "BIGINT NOT NULL")]
    [InlineData(MySqlLogicalColumnType.ForeignKey, true, "BIGINT NULL")]
    [InlineData(MySqlLogicalColumnType.Integer, false, "INT NOT NULL")]
    [InlineData(MySqlLogicalColumnType.BigInt, false, "BIGINT NOT NULL")]
    [InlineData(MySqlLogicalColumnType.Double, false, "DOUBLE NOT NULL")]
    [InlineData(MySqlLogicalColumnType.Boolean, false, "TINYINT(1) NOT NULL")]
    [InlineData(MySqlLogicalColumnType.Text, false, "VARCHAR(255) NOT NULL")]
    [InlineData(MySqlLogicalColumnType.LongText, true, "LONGTEXT NULL")]
    [InlineData(MySqlLogicalColumnType.Json, false, "JSON NOT NULL")]
    [InlineData(MySqlLogicalColumnType.TimestampUtc, false, "DATETIME(6) NOT NULL")]
    [InlineData(MySqlLogicalColumnType.Blob, false, "LONGBLOB NOT NULL")]
    public void Column_Type_Mapping(MySqlLogicalColumnType kind, bool nullable, string expected)
    {
        Assert.Equal(expected, MySqlDialect.MapColumnType(kind, nullable));
    }
}
