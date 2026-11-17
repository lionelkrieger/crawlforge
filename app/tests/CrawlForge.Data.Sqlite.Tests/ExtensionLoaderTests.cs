using System.Runtime.InteropServices;
using CrawlForge.Data.Sqlite.Extensions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CrawlForge.Data.Sqlite.Tests;

/// <summary>
/// Unit tests for the four bundled capability families, the extension loader, and the
/// capability probe (FS-CRAWL-001 §2.4). Edge-case semantics documented here are part
/// of the logical conformance surface shared with the MySQL provider.
/// </summary>
public class ExtensionLoaderTests
{
    [Fact(DisplayName = "Loader registers all four managed families without native bundles")]
    public async Task LoaderRegistersAllManagedFamilies()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var loader = new SqliteExtensionLoader();

        var report = loader.Load(connection);

        Assert.Equal(4, report.ManagedFamilies.Count);
        Assert.Contains(SqliteExtensionFamily.Regex, report.ManagedFamilies);
        Assert.Contains(SqliteExtensionFamily.DelimitedText, report.ManagedFamilies);
        Assert.Contains(SqliteExtensionFamily.VectorSimilarity, report.ManagedFamilies);
        Assert.Contains(SqliteExtensionFamily.MathStatistics, report.ManagedFamilies);
        Assert.Empty(report.NativeExtensionsLoaded);
        Assert.Empty(report.NativeExtensionErrors);
    }

    [Fact(DisplayName = "Absent native bundle directory is not an error")]
    public async Task AbsentNativeDirectoryIsNotAnError()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var loader = new SqliteExtensionLoader(@"E:\nonexistent-native-bundle-directory");

        var report = loader.Load(connection);

        Assert.Empty(report.NativeExtensionsLoaded);
        Assert.Empty(report.NativeExtensionErrors);
    }

    [Fact(DisplayName = "Broken native bundle file is reported, managed families keep working")]
    public async Task BrokenNativeFileReportedWithoutBreakingManagedFamilies()
    {
        using var temp = new TempDatabase();
        var nativeDirectory = Path.Combine(temp.DirectoryPath, "native");
        Directory.CreateDirectory(nativeDirectory);
        await File.WriteAllTextAsync(Path.Combine(nativeDirectory, "broken.dll"), "not a native library");

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var loader = new SqliteExtensionLoader(nativeDirectory);

        var report = loader.Load(connection);

        Assert.Single(report.NativeExtensionErrors);
        Assert.Empty(report.NativeExtensionsLoaded);
        var regexResult = await TestSql.ScalarAsync<long>(connection, "SELECT regexp('^a', 'abc')");
        Assert.Equal(1L, regexResult);
    }

    [Fact(DisplayName = "Native bundle resolves the platform-specific subdirectory first")]
    public async Task NativeBundleResolvesPlatformSubdirectory()
    {
        using var temp = new TempDatabase();
        var nativeDirectory = Path.Combine(temp.DirectoryPath, "native");
        var platformDirectory = Path.Combine(nativeDirectory, RuntimeInformation.RuntimeIdentifier);
        Directory.CreateDirectory(platformDirectory);
        await File.WriteAllTextAsync(Path.Combine(platformDirectory, "platform.dll"), "not a native library");
        await File.WriteAllTextAsync(Path.Combine(nativeDirectory, "fallback.dll"), "not a native library");

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var loader = new SqliteExtensionLoader(nativeDirectory);

        var report = loader.Load(connection);

        // Only the platform subdirectory is loaded when it exists.
        Assert.Single(report.NativeExtensionErrors);
        Assert.Contains("platform.dll", report.NativeExtensionErrors[0], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Capability probe reports available families with details")]
    public void ProbeReportsAvailableFamiliesWithDetails()
    {
        var report = SqliteCapabilityProbe.Probe(new SqliteExtensionLoader());

        Assert.True(report.AllAvailable);
        Assert.Equal(4, report.Capabilities.Count);
        Assert.All(report.Capabilities, status =>
        {
            Assert.True(status.Available);
            Assert.NotNull(status.Detail);
        });
        Assert.Empty(report.NativeExtensionErrors);
    }

    [Fact(DisplayName = "Probe rejects an unknown family lookup")]
    public void ProbeRejectsUnknownFamilyLookup()
    {
        var report = SqliteCapabilityProbe.Probe(new SqliteExtensionLoader());

        Assert.Throws<ArgumentOutOfRangeException>(() => report.GetStatus((SqliteExtensionFamily)99));
    }

    [Fact(DisplayName = "Regex family: null inputs yield NULL, invalid patterns raise SQL errors")]
    public async Task RegexNullAndInvalidPatternSemantics()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        await using var connection = await provider.OpenConnectionAsync();

        Assert.Null(await TestSql.ScalarOrNullAsync<long>(connection, "SELECT regexp(NULL, 'abc')"));
        Assert.Null(await TestSql.ScalarOrNullAsync<long>(connection, "SELECT regexp('^a', NULL)"));
        Assert.Null(await TestSql.ScalarStringOrNullAsync(connection, "SELECT regexp_replace(NULL, 'a', 'b')"));
        Assert.Null(await TestSql.ScalarStringOrNullAsync(connection, "SELECT regexp_extract('abc', NULL, 0)"));
        Assert.Null(await TestSql.ScalarStringOrNullAsync(connection, "SELECT regexp_extract('abc', 'z', 0)"));

        await Assert.ThrowsAsync<SqliteException>(() => TestSql.ExecuteAsync(connection, "SELECT regexp('[invalid', 'abc')"));
        await Assert.ThrowsAsync<SqliteException>(() => TestSql.ExecuteAsync(connection, "SELECT regexp_extract('abc', 'a', -1)"));
    }

    [Fact(DisplayName = "Delimited text: split_part boundary semantics")]
    public async Task SplitPartBoundarySemantics()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        await using var connection = await provider.OpenConnectionAsync();

        Assert.Null(await TestSql.ScalarStringOrNullAsync(connection, "SELECT split_part('a,b', ',', 0)"));
        Assert.Null(await TestSql.ScalarStringOrNullAsync(connection, "SELECT split_part('a,b', ',', -1)"));
        Assert.Null(await TestSql.ScalarStringOrNullAsync(connection, "SELECT split_part('a,b', ',', 3)"));
        Assert.Null(await TestSql.ScalarStringOrNullAsync(connection, "SELECT split_part(NULL, ',', 1)"));
        Assert.Equal(string.Empty, await TestSql.ScalarAsync<string>(connection, "SELECT split_part('a,,b', ',', 2)"));

        await Assert.ThrowsAsync<SqliteException>(() => TestSql.ExecuteAsync(connection, "SELECT split_part('a,b', '', 1)"));
    }

    [Fact(DisplayName = "Delimited text: import surface null and empty inputs, escaping")]
    public async Task DelimitedImportSurfaceNullAndEmptyInputs()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        await using var connection = await provider.OpenConnectionAsync();

        Assert.Null(await TestSql.ScalarOrNullAsync<long>(connection, "SELECT split_count(NULL, ',')"));
        Assert.Null(await TestSql.ScalarStringOrNullAsync(connection, "SELECT delimited_to_json(NULL, ',')"));

        var emptyCount = await TestSql.ScalarAsync<long>(connection, "SELECT split_count('', ',')");
        Assert.Equal(1L, emptyCount);

        var emptyRows = await TestSql.ScalarAsync<long>(connection, "SELECT COUNT(*) FROM json_each(delimited_to_json('', ','))");
        Assert.Equal(1L, emptyRows);

        var emptyValue = await TestSql.ScalarAsync<string>(connection, "SELECT value FROM json_each(delimited_to_json('', ','))");
        Assert.Equal(string.Empty, emptyValue);

        // Fields containing quotes, commas, and newlines survive the JSON round-trip.
        var trickyValue = await TestSql.ScalarAsync<string>(connection, "SELECT value FROM json_each(delimited_to_json('a\"b,c\nd', '|')) WHERE key = 0");
        Assert.Equal("a\"b,c\nd", trickyValue);

        await Assert.ThrowsAsync<SqliteException>(() => TestSql.ExecuteAsync(connection, "SELECT split_count('a,b', '')"));
        await Assert.ThrowsAsync<SqliteException>(() => TestSql.ExecuteAsync(connection, "SELECT delimited_to_json('a,b', '')"));
    }

    [Fact(DisplayName = "Delimited text: csv_agg null fields, empty set, custom delimiter")]
    public async Task CsvAggNullFieldsAndEmptySet()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        await using var connection = await provider.OpenConnectionAsync();

        var withNull = await TestSql.ScalarAsync<string>(connection, "SELECT csv_agg(column1) FROM (VALUES ('a'), (NULL), ('b'))");
        Assert.Equal("a,,b", withNull);

        var emptySet = await TestSql.ScalarAsync<string>(connection, "SELECT csv_agg(column1) FROM (VALUES ('a')) WHERE 1 = 0");
        Assert.Equal(string.Empty, emptySet);

        var semicolons = await TestSql.ScalarAsync<string>(connection, "SELECT csv_agg(column1, ';') FROM (VALUES ('a'), ('b;c'))");
        Assert.Equal("a;\"b;c\"", semicolons);

        await Assert.ThrowsAsync<SqliteException>(() => TestSql.ExecuteAsync(connection, "SELECT csv_agg(column1, '') FROM (VALUES ('a'))"));
    }

    [Fact(DisplayName = "Vector family: dimension mismatch and zero vectors yield NULL")]
    public async Task VectorMismatchAndZeroVectorSemantics()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        await using var connection = await provider.OpenConnectionAsync();

        Assert.Null(await TestSql.ScalarOrNullAsync<double>(connection, "SELECT vec_dot('[1,2]', '[1,2,3]')"));
        Assert.Null(await TestSql.ScalarOrNullAsync<double>(connection, "SELECT vec_distance('[1,2]', '[1,2,3]')"));
        Assert.Null(await TestSql.ScalarOrNullAsync<double>(connection, "SELECT vec_cosine('[1,2]', '[1,2,3]')"));
        Assert.Null(await TestSql.ScalarOrNullAsync<double>(connection, "SELECT vec_cosine('[0,0]', '[1,2]')"));
        Assert.Null(await TestSql.ScalarOrNullAsync<double>(connection, "SELECT vec_cosine(NULL, '[1,2]')"));
        Assert.Null(await TestSql.ScalarOrNullAsync<long>(connection, "SELECT vec_dim(NULL)"));
    }

    [Fact(DisplayName = "Vector family: malformed payloads yield NULL")]
    public async Task VectorMalformedPayloadsYieldNull()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        await using var connection = await provider.OpenConnectionAsync();

        Assert.Null(await TestSql.ScalarOrNullAsync<long>(connection, "SELECT vec_dim(x'010203')")); // 3 bytes: not float32-aligned.
        Assert.Null(await TestSql.ScalarOrNullAsync<long>(connection, "SELECT vec_dim('not json')"));
        Assert.Null(await TestSql.ScalarOrNullAsync<long>(connection, "SELECT vec_dim('[]')"));
        Assert.Null(await TestSql.ScalarOrNullAsync<long>(connection, "SELECT vec_dim('[1,\"two\"]')"));
        Assert.Null(await TestSql.ScalarOrNullAsync<long>(connection, "SELECT vec_dim('{\"a\":1}')"));
        Assert.Null(await TestSql.ScalarOrNullAsync<long>(connection, "SELECT vec_dim(42)"));
    }

    [Fact(DisplayName = "Vector family: float32 BLOB and JSON encodings agree")]
    public async Task VectorBlobAndJsonEncodingsAgree()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        await using var connection = await provider.OpenConnectionAsync();

        float[] left = [0.25f, -0.5f, 1.5f];
        float[] right = [1f, 2f, -1f];

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT vec_dot($a, $b), vec_cosine($a, $b), vec_distance($a, $b)";
        var parameterA = command.CreateParameter();
        parameterA.ParameterName = "$a";
        parameterA.Value = TestVectors.ToBlob(left);
        command.Parameters.Add(parameterA);
        var parameterB = command.CreateParameter();
        parameterB.ParameterName = "$b";
        parameterB.Value = TestVectors.ToBlob(right);
        command.Parameters.Add(parameterB);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var blobDot = reader.GetDouble(0);
        var blobCosine = reader.GetDouble(1);
        var blobDistance = reader.GetDouble(2);

        var jsonDot = await TestSql.ScalarAsync<double>(connection, "SELECT vec_dot('[0.25,-0.5,1.5]', '[1,2,-1]')");
        var jsonCosine = await TestSql.ScalarAsync<double>(connection, "SELECT vec_cosine('[0.25,-0.5,1.5]', '[1,2,-1]')");
        var jsonDistance = await TestSql.ScalarAsync<double>(connection, "SELECT vec_distance('[0.25,-0.5,1.5]', '[1,2,-1]')");

        Assert.Equal(jsonDot, blobDot, 5);
        Assert.Equal(jsonCosine, blobCosine, 5);
        Assert.Equal(jsonDistance, blobDistance, 5);
    }

    [Fact(DisplayName = "Math/stats: median odd/even, empty sets, variance minimum samples")]
    public async Task MathMedianAndVarianceEdgeCases()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        await using var connection = await provider.OpenConnectionAsync();

        var oddMedian = await TestSql.ScalarAsync<double>(connection, "SELECT median(column1) FROM (VALUES (3.0), (1.0), (2.0))");
        Assert.Equal(2.0, oddMedian, 10);

        var evenMedian = await TestSql.ScalarAsync<double>(connection, "SELECT median(column1) FROM (VALUES (1.0), (2.0), (3.0), (4.0))");
        Assert.Equal(2.5, evenMedian, 10);

        Assert.Null(await TestSql.ScalarOrNullAsync<double>(connection, "SELECT median(column1) FROM (VALUES (1.0)) WHERE 1 = 0"));
        Assert.Null(await TestSql.ScalarOrNullAsync<double>(connection, "SELECT variance(column1) FROM (VALUES (1.0))"));
        Assert.Null(await TestSql.ScalarOrNullAsync<double>(connection, "SELECT stddev(column1) FROM (VALUES (1.0))"));

        var populationStdDev = await TestSql.ScalarAsync<double>(connection, "SELECT stddev_pop(column1) FROM (VALUES (1.0), (2.0), (3.0), (4.0), (5.0))");
        Assert.Equal(Math.Sqrt(2.0), populationStdDev, 10);

        // SQL NULL inputs are ignored.
        var withNulls = await TestSql.ScalarAsync<double>(connection, "SELECT median(column1) FROM (VALUES (1.0), (NULL), (3.0))");
        Assert.Equal(2.0, withNulls, 10);
    }

    [Fact(DisplayName = "Math/stats: percentile interpolation, endpoints, and validation")]
    public async Task PercentileInterpolationAndValidation()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        await using var connection = await provider.OpenConnectionAsync();

        var interpolated = await TestSql.ScalarAsync<double>(connection, "SELECT percentile(column1, 0.25) FROM (VALUES (1.0), (2.0), (3.0), (4.0))");
        Assert.Equal(1.75, interpolated, 10);

        var minimum = await TestSql.ScalarAsync<double>(connection, "SELECT percentile(column1, 0.0) FROM (VALUES (1.0), (2.0), (3.0))");
        Assert.Equal(1.0, minimum, 10);

        var maximum = await TestSql.ScalarAsync<double>(connection, "SELECT percentile(column1, 1.0) FROM (VALUES (1.0), (2.0), (3.0))");
        Assert.Equal(3.0, maximum, 10);

        Assert.Null(await TestSql.ScalarOrNullAsync<double>(connection, "SELECT percentile(column1, 0.5) FROM (VALUES (1.0)) WHERE 1 = 0"));

        await Assert.ThrowsAsync<SqliteException>(() => TestSql.ExecuteAsync(connection, "SELECT percentile(column1, 1.5) FROM (VALUES (1.0))"));
        await Assert.ThrowsAsync<SqliteException>(() => TestSql.ExecuteAsync(connection, "SELECT percentile(column1, -0.1) FROM (VALUES (1.0))"));
    }

    [Fact(DisplayName = "Math/stats: aggregate invocations inside one query never share state")]
    public async Task AggregateInvocationsAreIsolated()
    {
        using var temp = new TempDatabase();
        using var provider = temp.CreateProvider();
        await using var connection = await provider.OpenConnectionAsync();

        // Two aggregates over different columns in the same SELECT.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT median(column1), median(column2) FROM (VALUES (1.0, 10.0), (2.0, 20.0), (3.0, 30.0))";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2.0, reader.GetDouble(0), 10);
            Assert.Equal(20.0, reader.GetDouble(1), 10);
        }

        // Grouped aggregates keep per-group state.
        await TestSql.ExecuteAsync(connection, "CREATE TABLE samples (grp TEXT NOT NULL, value REAL NOT NULL)");
        await TestSql.ExecuteAsync(connection, "INSERT INTO samples (grp, value) VALUES ('a', 1.0), ('a', 3.0), ('b', 10.0), ('b', 20.0), ('b', 30.0)");

        var groupMedians = new Dictionary<string, double>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT grp, median(value) FROM samples GROUP BY grp ORDER BY grp";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                groupMedians[reader.GetString(0)] = reader.GetDouble(1);
            }
        }

        Assert.Equal(2, groupMedians.Count);
        Assert.Equal(2.0, groupMedians["a"], 10);
        Assert.Equal(20.0, groupMedians["b"], 10);
    }
}
