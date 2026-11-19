using CrawlForge.Export.Writers;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// XLSX typed-cell proofs (WP-09-10, CAP-H-174 completion): number/boolean
/// columns produce native typed cells (sortable/summable in spreadsheets),
/// unparseable values fall back to inline strings, headers stay strings —
/// all read back through the OpenXml reader.
/// </summary>
public sealed class ExcelTypedCellTests
{
    private static async IAsyncEnumerable<ExportDataset> SingleAsync(ExportDataset dataset)
    {
        yield return dataset;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ExportRow> RowsAsync(params string?[][] rows)
    {
        foreach (var row in rows)
        {
            yield return new ExportRow(row);
        }

        await Task.CompletedTask;
    }

    /// <summary>Number cells are typed; booleans are typed; fallbacks stay strings.</summary>
    [Fact]
    public async Task TypedCells_RoundTrip_Through_OpenXml()
    {
        var dataset = new ExportDataset(
            "urls",
            [
                new ExportColumn("url", "URL", "text"),
                new ExportColumn("depth", "Depth", "number"),
                new ExportColumn("size_bytes", "Size (bytes)", "number"),
                new ExportColumn("is_indexable", "Indexable", "boolean"),
            ],
            RowsAsync(
                ["https://example.com/a", "2", "1024", "true"],
                ["https://example.com/b", "not-a-number", "2048", "false"]));

        await using var output = new MemoryStream();
        await ExcelExportWriter.WriteAsync(output, SingleAsync(dataset));

        output.Position = 0;
        using var document = SpreadsheetDocument.Open(output, isEditable: false);
        var sheet = (WorksheetPart)document.WorkbookPart!.GetPartById(document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().Single().Id!);
        var rows = sheet.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().ToList();
        Assert.Equal(3, rows.Count);

        // Header row stays inline strings.
        Assert.All(rows[0].Elements<Cell>(), c => Assert.Equal(CellValues.InlineString, c.DataType!.Value));

        var first = rows[1].Elements<Cell>().ToList();
        Assert.Equal(CellValues.InlineString, first[0].DataType!.Value);
        Assert.Equal(CellValues.Number, first[1].DataType!.Value);
        Assert.Equal("2", first[1].CellValue!.Text);
        Assert.Equal(CellValues.Number, first[2].DataType!.Value);
        Assert.Equal("1024", first[2].CellValue!.Text);
        Assert.Equal(CellValues.Boolean, first[3].DataType!.Value);
        Assert.Equal("1", first[3].CellValue!.Text);

        var second = rows[2].Elements<Cell>().ToList();
        // Unparseable number → inline string fallback (no data dropped).
        Assert.Equal(CellValues.InlineString, second[1].DataType!.Value);
        Assert.Equal("not-a-number", second[1].InnerText);
        Assert.Equal(CellValues.Boolean, second[3].DataType!.Value);
        Assert.Equal("0", second[3].CellValue!.Text);
    }

    /// <summary>Null cells stay empty inline strings in typed columns.</summary>
    [Fact]
    public async Task NullCells_Stay_Empty()
    {
        var dataset = new ExportDataset(
            "findings",
            [new ExportColumn("count", "Count", "number")],
            RowsAsync(new string?[] { null }));

        await using var output = new MemoryStream();
        await ExcelExportWriter.WriteAsync(output, SingleAsync(dataset));

        output.Position = 0;
        using var document = SpreadsheetDocument.Open(output, isEditable: false);
        var sheet = (WorksheetPart)document.WorkbookPart!.GetPartById(document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().Single().Id!);
        var dataRow = sheet.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().Skip(1).Single();
        var cell = dataRow.Elements<Cell>().Single();
        Assert.Equal(CellValues.InlineString, cell.DataType!.Value);
        Assert.Equal(string.Empty, cell.InnerText);
    }
}