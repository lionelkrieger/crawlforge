using System.IO.Compression;
using System.Text;
using CrawlForge.Export.Writers;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Writer proofs: RFC-4180 CSV, self-contained HTML, multi-sheet XLSX read
/// back through the OpenXml reader, and a structurally valid PDF — all from
/// streaming datasets, fully offline (criterion #7).
/// </summary>
public sealed class WriterTests
{
    private static async IAsyncEnumerable<ExportDataset> SingleDatasetAsync(ExportDataset dataset)
    {
        yield return dataset;
        await Task.CompletedTask;
    }

    private static ExportDataset Dataset(string key, IReadOnlyList<ExportColumn> columns, params string?[][] rows)
        => new(key, columns, ToAsync(rows));

    private static async IAsyncEnumerable<ExportRow> ToAsync(string?[][] rows)
    {
        foreach (var row in rows)
        {
            yield return new ExportRow(row);
        }

        await Task.CompletedTask;
    }

    /// <summary>CSV quotes commas, quotes, and newlines per RFC 4180.</summary>
    [Fact]
    public void Csv_Escapes_Commas_Quotes_And_Newlines()
    {
        var writer = new StringWriter();
        CsvExportWriter.WriteRow(writer, ["plain", "with,comma", "with\"quote", "line\r\nbreak", null]);

        Assert.Equal("plain,\"with,comma\",\"with\"\"quote\",\"line\r\nbreak\",\r\n", writer.ToString());
    }

    /// <summary>CSV export produces one zip entry per dataset with headers + rows.</summary>
    [Fact]
    public async Task Csv_Writes_One_Entry_Per_Dataset()
    {
        var datasets = new[]
        {
            Dataset("urls", [new ExportColumn("url", "URL", "text")], ["https://example.com/a"], ["https://example.com/b"]),
            Dataset("findings", [new ExportColumn("rule", "Rule", "text")], ["content.readability"]),
        };

        await using var output = new MemoryStream();
        await CsvExportWriter.WriteAsync(output, ToDatasetsAsync(datasets));

        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(2, archive.Entries.Count);
        using var reader = new StreamReader(archive.GetEntry("urls.csv")!.Open());
        var content = await reader.ReadToEndAsync();
        Assert.Contains("URL", content);
        Assert.Contains("https://example.com/b", content);
    }

    /// <summary>HTML report is self-contained: no external references; values escaped.</summary>
    [Fact]
    public async Task Html_Is_SelfContained_And_Escaped()
    {
        var dataset = Dataset("urls", [new ExportColumn("url", "URL", "text")], ["<script>alert(1)</script>"]);

        await using var output = new MemoryStream();
        await HtmlReportWriter.WriteAsync(output, 7, SingleDatasetAsync(dataset));

        var html = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("<h1>CrawlForge Report — Audit 7</h1>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("http://", html.Replace("http://www.sitemaps.org", string.Empty));
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("src=", html);
        Assert.DoesNotContain("link rel=", html);
    }

    /// <summary>XLSX round-trip: one sheet per dataset, header + inline-string rows.</summary>
    [Fact]
    public async Task Excel_RoundTrips_Sheets_Headers_And_Rows()
    {
        var datasets = new[]
        {
            Dataset("summary", [new ExportColumn("key", "Key", "text"), new ExportColumn("value", "Value", "text")], ["status", "Completed"]),
            Dataset("custom-extraction", [new ExportColumn("url", "URL", "text"), new ExportColumn("custom.Price.0", "Price [0]", "number")], ["https://example.com/a", "9.99"]),
        };

        await using var output = new MemoryStream();
        await ExcelExportWriter.WriteAsync(output, ToDatasetsAsync(datasets));

        output.Position = 0;
        using var document = SpreadsheetDocument.Open(output, isEditable: false);
        var workbook = document.WorkbookPart!;
        var sheets = workbook.Workbook!.Sheets!.Elements<Sheet>().ToList();
        Assert.Equal(["summary", "custom-extraction"], sheets.Select(s => s.Name!.Value));

        var second = (WorksheetPart)workbook.GetPartById(sheets[1].Id!);
        var rows = second.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Price [0]", rows[0].Elements<Cell>().Skip(1).First().InnerText);
        Assert.Equal("9.99", rows[1].Elements<Cell>().Skip(1).First().InnerText);
    }

    /// <summary>PDF output starts with the PDF magic and carries the report title.</summary>
    [Fact]
    public async Task Pdf_Produces_Valid_Local_Document()
    {
        var dataset = Dataset("summary", [new ExportColumn("key", "Key", "text")], ["status"]);

        await using var output = new MemoryStream();
        await PdfSummaryWriter.WriteAsync(output, 42, SingleDatasetAsync(dataset));

        var bytes = output.ToArray();
        Assert.True(bytes.Length > 500);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.Contains("CrawlForge Report", Encoding.ASCII.GetString(bytes));
    }

    private static async IAsyncEnumerable<ExportDataset> ToDatasetsAsync(ExportDataset[] datasets)
    {
        foreach (var dataset in datasets)
        {
            yield return dataset;
        }

        await Task.CompletedTask;
    }
}
