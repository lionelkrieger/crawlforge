using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Sqlite.Extensions;

/// <summary>
/// Availability of one required capability family on the embedded provider.
/// </summary>
/// <param name="Family">The capability family.</param>
/// <param name="Available">Whether the family passed its probe.</param>
/// <param name="Detail">Human-readable probe detail (registered surface or failure reason).</param>
public sealed record SqliteCapabilityStatus(
    SqliteExtensionFamily Family,
    bool Available,
    string? Detail);

/// <summary>
/// Provider-capability probe verdict for the four required families
/// (FS-CRAWL-001 §2.4). Produced by executing representative SQL against a scratch
/// in-memory database with the bundled extensions loaded.
/// </summary>
/// <param name="Capabilities">Per-family probe results, one per required family.</param>
public sealed record SqliteCapabilityReport(IReadOnlyList<SqliteCapabilityStatus> Capabilities)
{
    /// <summary>
    /// Errors from loading locally bundled native extensions during the probe, when any.
    /// The managed families remain available regardless; native bundles are optional.
    /// </summary>
    public IReadOnlyList<string> NativeExtensionErrors { get; init; } = [];
    /// <summary>Whether every required capability family is available.</summary>
    public bool AllAvailable
    {
        get
        {
            foreach (var status in Capabilities)
            {
                if (!status.Available)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Returns the probe status of one family.</summary>
    /// <param name="family">The capability family to look up.</param>
    /// <returns>The family's probe status.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The family is not part of the report.</exception>
    public SqliteCapabilityStatus GetStatus(SqliteExtensionFamily family)
    {
        foreach (var status in Capabilities)
        {
            if (status.Family == family)
            {
                return status;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(family), family, "The capability family is not part of this report.");
    }
}

/// <summary>
/// Provider-capability probe: loads the bundled extensions onto a scratch in-memory
/// connection and verifies each required family with representative SQL, including the
/// <c>REGEXP</c> operator wiring, the delimited-text import surface over json_each,
/// vector similarity over JSON payloads, and the native engine's scalar math functions.
/// </summary>
public static class SqliteCapabilityProbe
{
    /// <summary>Runs the capability probe with the given loader configuration.</summary>
    /// <param name="loader">The extension loader whose surface is probed.</param>
    /// <returns>The per-family capability report.</returns>
    public static SqliteCapabilityReport Probe(SqliteExtensionLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var loadReport = loader.Load(connection);

        var statuses = new List<SqliteCapabilityStatus>(4)
        {
            ProbeFamily(connection, SqliteExtensionFamily.Regex, ProbeRegex),
            ProbeFamily(connection, SqliteExtensionFamily.DelimitedText, ProbeDelimitedText),
            ProbeFamily(connection, SqliteExtensionFamily.VectorSimilarity, ProbeVectorSimilarity),
            ProbeFamily(connection, SqliteExtensionFamily.MathStatistics, ProbeMathStatistics),
        };

        return new SqliteCapabilityReport(statuses)
        {
            NativeExtensionErrors = loadReport.NativeExtensionErrors,
        };
    }

    private static SqliteCapabilityStatus ProbeFamily(
        SqliteConnection connection,
        SqliteExtensionFamily family,
        Func<SqliteConnection, string> probe)
    {
        try
        {
            var detail = probe(connection);
            return new SqliteCapabilityStatus(family, Available: true, detail);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            return new SqliteCapabilityStatus(family, Available: false, ex.Message);
        }
    }

    private static string ProbeRegex(SqliteConnection connection)
    {
        RequireScalar(connection, "SELECT regexp('^h', 'hello')", 1L);
        RequireScalar(connection, "SELECT 'hello' REGEXP '^h'", 1L);
        RequireScalar(connection, "SELECT regexp_replace('a1b2', '[0-9]', '#')", "a#b#");
        return "regexp (REGEXP operator), regexp_replace, regexp_extract";
    }

    private static string ProbeDelimitedText(SqliteConnection connection)
    {
        RequireScalar(connection, "SELECT split_part('a|b|c', '|', 2)", "b");
        RequireScalar(connection, "SELECT COUNT(*) FROM json_each(delimited_to_json('a,b', ','))", 2L);
        RequireScalar(connection, "SELECT csv_agg(column1) FROM (VALUES ('a'), ('b,c'))", "a,\"b,c\"");
        return "split_part, split_count, delimited_to_json (+ json_each import), csv_quote, csv_agg";
    }

    private static string ProbeVectorSimilarity(SqliteConnection connection)
    {
        RequireScalar(connection, "SELECT vec_dot('[1,2]', '[3,4]')", 11.0);
        RequireScalar(connection, "SELECT vec_cosine('[1,0]', '[0,1]')", 0.0);
        RequireScalar(connection, "SELECT vec_dim('[1,2,3]')", 3L);
        return "vec_dim, vec_norm, vec_dot, vec_distance, vec_cosine (float32 BLOB + JSON encodings)";
    }

    private static string ProbeMathStatistics(SqliteConnection connection)
    {
        RequireScalar(connection, "SELECT median(column1) FROM (VALUES (1.0), (2.0), (3.0), (4.0))", 2.5);
        RequireScalar(connection, "SELECT sqrt(9)", 3.0);
        RequireScalar(connection, "SELECT percentile(column1, 0.5) FROM (VALUES (1.0), (2.0), (3.0))", 2.0);
        return "median, variance, stddev, percentile + native scalar math (sqrt, ln, exp, pow, …)";
    }

    private static void RequireScalar(SqliteConnection connection, string sql, object expected)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var actual = command.ExecuteScalar();
        if (!Equals(actual, expected))
        {
            throw new InvalidOperationException(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Capability probe query returned '{actual}', expected '{expected}'."));
        }
    }
}
