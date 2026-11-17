using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace CrawlForge.Export.Writers;

/// <summary>
/// Multi-sheet Excel export (FS-CRAWL-001 §8.4: summary, URLs, findings, links,
/// and custom extraction when enabled). Uses the forward-only
/// <see cref="OpenXmlWriter"/> with inline strings — no shared-string table and
/// no in-memory workbook DOM — so memory stays bounded at 500k+ rows
/// (FS-CRAWL-002 §7.1). Dynamic columns appear exactly as the dataset schema
/// declares them.
/// </summary>
public static class ExcelExportWriter
{
    /// <summary>
    /// Writes one worksheet per dataset into a workbook on the output stream.
    /// The stream is left open.
    /// </summary>
    /// <param name="output">Destination stream (local .xlsx file).</param>
    /// <param name="datasets">Streaming datasets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the workbook is written.</returns>
    public static async Task WriteAsync(Stream output, IAsyncEnumerable<ExportDataset> datasets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(datasets);

        using var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        var sheets = new List<(string Name, string RelationshipId)>();
        uint sheetId = 1;

        await foreach (var dataset in datasets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            using (var writer = OpenXmlWriter.Create(worksheetPart))
            {
                writer.WriteStartElement(new Worksheet());
                writer.WriteStartElement(new SheetData());

                uint rowIndex = 1;
                WriteRow(writer, rowIndex++, dataset.Columns.Select(c => c.Label));
                await foreach (var row in dataset.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    WriteRow(writer, rowIndex++, row.Values);
                }

                writer.WriteEndElement(); // SheetData
                writer.WriteEndElement(); // Worksheet
            }

            sheets.Add((SanitizeSheetName(dataset.Key), workbookPart.GetIdOfPart(worksheetPart)));
            sheetId++;
        }

        // The workbook part is written last: sheet references are known only
        // after every dataset streamed through.
        using (var writer = OpenXmlWriter.Create(workbookPart))
        {
            writer.WriteStartElement(new Workbook());
            writer.WriteStartElement(new Sheets());
            uint id = 1;
            foreach (var (name, relationshipId) in sheets)
            {
                writer.WriteElement(new Sheet { Name = name, SheetId = id++, Id = relationshipId });
            }

            writer.WriteEndElement(); // Sheets
            writer.WriteEndElement(); // Workbook
        }
    }

    private static void WriteRow(OpenXmlWriter writer, uint rowIndex, IEnumerable<string?> values)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });
        foreach (var value in values)
        {
            writer.WriteStartElement(new Cell { DataType = CellValues.InlineString });
            writer.WriteElement(new InlineString(new Text(value ?? string.Empty)));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static string SanitizeSheetName(string key)
    {
        var name = key;
        foreach (var invalid in new[] { '[', ']', ':', '*', '?', '/', '\\' })
        {
            name = name.Replace(invalid, '-');
        }

        return name.Length <= 31 ? name : name[..31];
    }
}
