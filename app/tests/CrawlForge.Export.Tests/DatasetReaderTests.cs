using CrawlForge.Contracts.Enums;
using CrawlForge.Export.Datasets;
using CrawlForge.Export.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Dataset reader proofs on the real SQLite stack: fixed datasets, dynamic
/// per-rule extraction expansion (busiest page), snippet columns, invalid
/// values as empty cells, and the open dataset vocabulary.
/// </summary>
public sealed class DatasetReaderTests
{
    private static async Task<List<ExportRow>> CollectAsync(ExportDataset dataset)
    {
        var rows = new List<ExportRow>();
        await foreach (var row in dataset.Rows)
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>URL dataset renders enum names and streams every record.</summary>
    [Fact]
    public async Task Urls_Streams_Records_With_Enum_Names()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        await stack.SeedUrlAsync(auditId, "https://example.com/a");
        await stack.SeedUrlAsync(auditId, "https://example.com/b", indexable: false, statusClass: CrawlStatusClass.Blocked);

        var reader = new ExportDatasetReader(stack.Provider);
        var dataset = await reader.ReadAsync(auditId, [ExportDatasetKeys.Urls]).SingleAsync();
        var rows = await CollectAsync(dataset);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Success", rows[0].Values[5]);
        Assert.Equal("Blocked", rows[1].Values[5]);
        Assert.Equal("false", rows[1].Values[12]);
    }

    /// <summary>Findings join the affected URL and render severity names.</summary>
    [Fact]
    public async Task Findings_Join_Urls_And_Render_Severity()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        await stack.SeedFindingAsync(auditId, "content.readability", FindingSeverity.Warning, urlId);

        var reader = new ExportDatasetReader(stack.Provider);
        var dataset = await reader.ReadAsync(auditId, [ExportDatasetKeys.Findings]).SingleAsync();
        var rows = await CollectAsync(dataset);

        var row = Assert.Single(rows);
        Assert.Equal("content.readability", row.Values[0]);
        Assert.Equal("Warning", row.Values[2]);
        Assert.Equal("https://example.com/a", row.Values[3]);
    }

    /// <summary>
    /// Dynamic extraction columns expand to the busiest page's ordinal count
    /// (locked DynamicColumnSchema contract); invalid values emit empty cells.
    /// </summary>
    [Fact]
    public async Task CustomExtraction_Expands_To_Busiest_Page_And_Empty_Invalid()
    {
        await using var stack = new ExportTestStack();
        var rulesJson = """[{"name":"Price","selector":".price","valueType":1,"allowMultiple":true},{"name":"Title","selector":"h1","valueType":0}]""";
        var auditId = await stack.SeedAuditAsync(rulesJson);
        var busyUrl = await stack.SeedUrlAsync(auditId, "https://example.com/busy");
        var quietUrl = await stack.SeedUrlAsync(auditId, "https://example.com/quiet");

        await stack.SeedExtractedAsync(auditId, busyUrl, "Price", 0, "9.99", ExtractedValueType.Number);
        await stack.SeedExtractedAsync(auditId, busyUrl, "Price", 1, "19.99", ExtractedValueType.Number);
        await stack.SeedExtractedAsync(auditId, busyUrl, "Price", 2, null, ExtractedValueType.Number, isInvalid: true);
        await stack.SeedExtractedAsync(auditId, busyUrl, "Title", 0, "\"Busy page\"");
        await stack.SeedExtractedAsync(auditId, quietUrl, "Price", 0, "5.00", ExtractedValueType.Number);

        var reader = new ExportDatasetReader(stack.Provider);
        var dataset = await reader.ReadAsync(auditId, [ExportDatasetKeys.CustomExtraction]).SingleAsync();

        Assert.Equal(
            ["url", "custom.Price.0", "custom.Price.1", "custom.Price.2", "custom.Title"],
            dataset.Columns.Select(c => c.Key));
        Assert.Equal("number", dataset.Columns[1].ValueType);

        var rows = await CollectAsync(dataset);
        Assert.Equal(2, rows.Count);
        var busy = rows.Single(r => r.Values[0] == "https://example.com/busy");
        Assert.Equal("9.99", busy.Values[1]);
        Assert.Equal("19.99", busy.Values[2]);
        Assert.Null(busy.Values[3]); // invalid → empty cell
        Assert.Equal("Busy page", busy.Values[4]);
        var quiet = rows.Single(r => r.Values[0] == "https://example.com/quiet");
        Assert.Equal("5", quiet.Values[1]); // invariant round-trip rendering of JSON number 5.00
        Assert.Null(quiet.Values[2]);
    }

    /// <summary>Snippet datasets union declared columns and stream bounded rows.</summary>
    [Fact]
    public async Task Snippets_Build_Dynamic_Columns_And_Stream_Rows()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        await stack.SeedSnippetAsync(auditId, urlId, "demo",
            """[{"key":"count","label":"Count","valueType":"number"}]""",
            """[{"count":5},{"count":7}]""");

        var reader = new ExportDatasetReader(stack.Provider);
        var dataset = await reader.ReadAsync(auditId, [ExportDatasetKeys.Snippets]).SingleAsync();

        Assert.Equal(["url", "snippet_key", "snippet.demo.count"], dataset.Columns.Select(c => c.Key));
        var rows = await CollectAsync(dataset);
        Assert.Equal(2, rows.Count);
        Assert.Equal("5", rows[0].Values[2]);
        Assert.Equal("7", rows[1].Values[2]);
    }

    /// <summary>Empty selection yields the default sheets; unknown keys are skipped.</summary>
    [Fact]
    public async Task Default_Selection_And_Unknown_Keys()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();

        var reader = new ExportDatasetReader(stack.Provider);
        var defaults = new List<string>();
        await foreach (var dataset in reader.ReadAsync(auditId, []))
        {
            defaults.Add(dataset.Key);
        }

        Assert.Equal(ExportDatasetKeys.Default, defaults);

        var unknown = new List<string>();
        await foreach (var dataset in reader.ReadAsync(auditId, ["not-a-dataset", ExportDatasetKeys.Urls]))
        {
            unknown.Add(dataset.Key);
        }

        Assert.Equal([ExportDatasetKeys.Urls], unknown);
    }

    /// <summary>Summary streams audit facts, metrics, and scores.</summary>
    [Fact]
    public async Task Summary_Includes_Metrics_And_Scores()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        await stack.SeedMetricAsync(auditId, "ngram-top", 12);
        await stack.ExecuteAsync(
            "INSERT INTO `audit_scores` (`audit_id`, `category`, `score`, `error_count`, `warning_count`, `notice_count`, `pass_count`) VALUES (@a, NULL, 87.5, 1, 2, 3, 4)",
            ("@a", auditId));

        var reader = new ExportDatasetReader(stack.Provider);
        var dataset = await reader.ReadAsync(auditId, [ExportDatasetKeys.Summary]).SingleAsync();
        var rows = await CollectAsync(dataset);

        Assert.Contains(rows, r => r.Values[0] == "metric" && r.Values[1] == "ngram-top" && r.Values[3] == "12");
        Assert.Contains(rows, r => r.Values[0] == "score" && r.Values[1] == "overall" && r.Values[3] == "87.5");
        Assert.Contains(rows, r => r.Values[0] == "audit" && r.Values[1] == "status");
    }
}

/// <summary>Async-enumerable test helpers.</summary>
internal static class AsyncEnumerableTestExtensions
{
    /// <summary>Returns the single element of an async sequence.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="source">Async sequence.</param>
    /// <returns>The single element.</returns>
    public static async Task<T> SingleAsync<T>(this IAsyncEnumerable<T> source)
    {
        T? result = default;
        var count = 0;
        await foreach (var item in source)
        {
            result = item;
            count++;
        }

        Assert.Equal(1, count);
        return result!;
    }
}
