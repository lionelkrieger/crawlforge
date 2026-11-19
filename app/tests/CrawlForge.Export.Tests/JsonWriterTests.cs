using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Enums;
using CrawlForge.Export.Datasets;
using CrawlForge.Export.Tests.TestInfrastructure;
using CrawlForge.Export.Writers;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// JSON writer proofs (WP-09-10, CAP-H-175; agent data contract §11.8):
/// every output is PARSEABLE local JSON, summary-first, with typed cells and
/// stable identifiers; the fixture carries the noindex + duplicate-titles
/// findings end-to-end.
/// </summary>
public sealed class JsonWriterTests
{
    private static async IAsyncEnumerable<ExportDataset> ToDatasetsAsync(params ExportDataset[] datasets)
    {
        foreach (var dataset in datasets)
        {
            yield return dataset;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ExportRow> ToRowsAsync(params string?[][] rows)
    {
        foreach (var row in rows)
        {
            yield return new ExportRow(row);
        }

        await Task.CompletedTask;
    }

    /// <summary>Summary-first document parses; schema leads, rows follow, rowCount closes.</summary>
    [Fact]
    public async Task Json_Document_Is_SummaryFirst_And_Parseable()
    {
        var dataset = new ExportDataset(
            "urls",
            [new ExportColumn("url_id", "URL Id", "number"), new ExportColumn("url", "URL", "text"), new ExportColumn("depth", "Depth", "number"), new ExportColumn("is_indexable", "Indexable", "boolean")],
            ToRowsAsync(["7", "https://example.com/a", "2", "true"], ["8", "https://example.com/b", "0", "false"]));

        await using var output = new MemoryStream();
        await JsonExportWriter.WriteAsync(output, 42, ToDatasetsAsync(dataset));

        using var document = JsonDocument.Parse(output.ToArray());
        var root = document.RootElement;
        Assert.Equal("42", root.GetProperty("auditId").GetString()); // string EntityId convention
        Assert.True(root.TryGetProperty("generatedAt", out _));

        var datasets = root.GetProperty("datasets");
        Assert.Equal(1, datasets.GetArrayLength());
        var first = datasets[0];
        Assert.Equal("urls", first.GetProperty("key").GetString());

        // Schema before rows (summary-first): columns enumerate the contract.
        var columns = first.GetProperty("columns");
        Assert.Equal("url_id", columns[0].GetProperty("key").GetString());
        Assert.Equal("number", columns[0].GetProperty("valueType").GetString());

        var rows = first.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(7, rows[0].GetProperty("url_id").GetInt32()); // typed number
        Assert.Equal("https://example.com/a", rows[0].GetProperty("url").GetString());
        Assert.True(rows[0].GetProperty("is_indexable").GetBoolean()); // typed boolean
        Assert.False(rows[1].GetProperty("is_indexable").GetBoolean());
        Assert.Equal(2, first.GetProperty("rowCount").GetInt64());

        // Property order: key, columns, rows, rowCount (summary-first layout).
        var names = first.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["key", "columns", "rows", "rowCount"], names);
    }

    /// <summary>Null cells stay null; unparseable numbers fall back to strings.</summary>
    [Fact]
    public async Task Json_Nulls_And_Fallback_Typing()
    {
        var dataset = new ExportDataset(
            "findings",
            [new ExportColumn("severity", "Severity", "text"), new ExportColumn("count", "Count", "number")],
            ToRowsAsync(["Warning", null], ["Error", "not-a-number"]));

        await using var output = new MemoryStream();
        await JsonExportWriter.WriteAsync(output, 1, ToDatasetsAsync(dataset));

        using var document = JsonDocument.Parse(output.ToArray());
        var rows = document.RootElement.GetProperty("datasets")[0].GetProperty("rows");
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("count").ValueKind);
        Assert.Equal("not-a-number", rows[1].GetProperty("count").GetString());
    }

    /// <summary>JSON Lines: one parseable object per row, tagged with the dataset key.</summary>
    [Fact]
    public async Task JsonLines_Emits_One_Parseable_Object_Per_Row()
    {
        var datasets = new[]
        {
            new ExportDataset("urls", [new ExportColumn("url_id", "URL Id", "number"), new ExportColumn("url", "URL", "text")], ToRowsAsync(["1", "https://example.com/a"], ["2", "https://example.com/b"])),
            new ExportDataset("findings", [new ExportColumn("rule_key", "Rule", "text")], ToRowsAsync(["indexability.noindex"])),
        };

        await using var output = new MemoryStream();
        await JsonExportWriter.WriteLinesAsync(output, ToDatasetsAsync(datasets));

        var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("urls", first.RootElement.GetProperty("dataset").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("url_id").GetInt32());
        using var last = JsonDocument.Parse(lines[2]);
        Assert.Equal("findings", last.RootElement.GetProperty("dataset").GetString());
        Assert.Equal("indexability.noindex", last.RootElement.GetProperty("rule_key").GetString());
    }

    /// <summary>
    /// Real SQLite stack: identifier mode carries the API's stable ids, and the
    /// noindex + duplicate-titles fixture findings survive the JSON round-trip.
    /// </summary>
    [Fact]
    public async Task Json_IdentifierMode_Carries_Stable_Ids_And_Fixture_Findings()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        var noindexUrl = await stack.SeedUrlAsync(auditId, "https://example.com/noindex", indexable: false);
        var dupUrl = await stack.SeedUrlAsync(auditId, "https://example.com/dup");
        await stack.SeedFindingAsync(auditId, "indexability.noindex", FindingSeverity.Warning, noindexUrl);
        await stack.SeedFindingAsync(auditId, "content.duplicate-title", FindingSeverity.Warning, dupUrl);
        await stack.SeedLinkAsync(auditId, noindexUrl, "https://example.com/dup", "dup");

        var reader = new ExportDatasetReader(stack.Provider);
        await using var output = new MemoryStream();
        await JsonExportWriter.WriteAsync(
            output,
            auditId,
            reader.ReadAsync(auditId, [ExportDatasetKeys.Urls, ExportDatasetKeys.Findings, ExportDatasetKeys.Links], includeIdentifiers: true));

        using var document = JsonDocument.Parse(output.ToArray());
        var datasets = document.RootElement.GetProperty("datasets");

        var urls = datasets[0].GetProperty("rows");
        Assert.Equal(2, urls.GetArrayLength());
        foreach (var row in urls.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Number, row.GetProperty("url_id").ValueKind);
        }

        var findings = datasets[1].GetProperty("rows");
        var rules = findings.EnumerateArray().Select(r => r.GetProperty("rule_key").GetString()).ToList();
        Assert.Contains("indexability.noindex", rules);
        Assert.Contains("content.duplicate-title", rules);
        foreach (var row in findings.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Number, row.GetProperty("finding_id").ValueKind);
            Assert.Equal(JsonValueKind.Number, row.GetProperty("url_id").ValueKind);
        }

        var links = datasets[2].GetProperty("rows");
        var link = Assert.Single(links.EnumerateArray());
        Assert.Equal(JsonValueKind.Number, link.GetProperty("link_id").ValueKind);
        Assert.Equal(noindexUrl, link.GetProperty("source_url_id").GetInt64());
    }

    /// <summary>Identifier mode keeps baseline column order intact (additive prefix only).</summary>
    [Fact]
    public async Task IdentifierMode_Prepends_Ids_Without_Reordering_Baseline()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        await stack.SeedUrlAsync(auditId, "https://example.com/a");

        var reader = new ExportDatasetReader(stack.Provider);
        var baseline = await reader.ReadAsync(auditId, [ExportDatasetKeys.Urls]).SingleAsync();
        var identified = await reader.ReadAsync(auditId, [ExportDatasetKeys.Urls], includeIdentifiers: true).SingleAsync();

        Assert.Equal("url_id", identified.Columns[0].Key);
        Assert.Equal(baseline.Columns.Select(c => c.Key), identified.Columns.Skip(1).Select(c => c.Key));
    }

    /// <summary>Dynamic extraction columns carry url_id in identifier mode.</summary>
    [Fact]
    public async Task IdentifierMode_Extraction_And_Snippets_Carry_UrlId()
    {
        await using var stack = new ExportTestStack();
        var rulesJson = """[{"name":"Price","selector":".price","valueType":1,"allowMultiple":true}]""";
        var auditId = await stack.SeedAuditAsync(rulesJson);
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        await stack.SeedExtractedAsync(auditId, urlId, "Price", 0, "9.99", ExtractedValueType.Number);
        await stack.SeedSnippetAsync(auditId, urlId, "demo", """[{"key":"count","label":"Count","valueType":"number"}]""", """[{"count":5}]""");

        var reader = new ExportDatasetReader(stack.Provider);
        await using var output = new MemoryStream();
        await JsonExportWriter.WriteAsync(output, auditId, reader.ReadAsync(auditId, [ExportDatasetKeys.CustomExtraction, ExportDatasetKeys.Snippets], includeIdentifiers: true));

        using var document = JsonDocument.Parse(output.ToArray());
        var datasets = document.RootElement.GetProperty("datasets");
        var extractionRow = Assert.Single(datasets[0].GetProperty("rows").EnumerateArray());
        Assert.Equal(urlId, extractionRow.GetProperty("url_id").GetInt64());
        // Single-value expansion collapses to the bare rule column (locked schema).
        Assert.Equal(9.99, extractionRow.GetProperty("custom.Price").GetDouble());
        var snippetRow = Assert.Single(datasets[1].GetProperty("rows").EnumerateArray());
        Assert.Equal(urlId, snippetRow.GetProperty("url_id").GetInt64());
        Assert.Equal(5, snippetRow.GetProperty("snippet.demo.count").GetInt32());
    }
}