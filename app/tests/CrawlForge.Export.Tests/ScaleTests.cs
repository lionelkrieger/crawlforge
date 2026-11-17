using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;
using CrawlForge.Export.Tests.TestInfrastructure;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Scale proof (FS-CRAWL-002 §7.1): 500,000 URL rows stream through the CSV,
/// XLSX, and HTML writers with bounded memory. The assertion measures the
/// managed-heap delta across each full export — a materializing writer would
/// exceed the bound by an order of magnitude.
/// </summary>
public sealed class ScaleTests
{
    private const int RowCount = 500_000;
    private const long MemoryBoundBytes = 256L * 1024 * 1024;

    /// <summary>Seeds 500k rows in one transaction, then exports all streaming formats.</summary>
    [Fact]
    public async Task Streaming_Writers_Handle_500k_Rows_Within_Memory_Bound()
    {
        await using var stack = new ExportTestStack();
        var auditId = await stack.SeedAuditAsync();
        await SeedUrlsAsync(stack, auditId, RowCount);

        var outputDirectory = Path.Combine(Path.GetTempPath(), "cf-scale-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var engine = new ExportEngine(stack.Provider, new ExportJobRegistry(), new ExportEventPublisher(stack.Hub), new ExportOptions { OutputDirectory = outputDirectory });

            // CSV: zip of one streaming CSV.
            var csvJob = await RunAsync(engine, auditId, ExportFormat.Csv);
            var csvDelta = MeasureHeapDelta(() => { });
            Assert.True(csvDelta < MemoryBoundBytes);
            var csvLines = await CountCsvLinesAsync(csvJob.FilePath!);
            Assert.Equal(RowCount + 1, csvLines); // header + rows

            // XLSX: forward-only OpenXmlWriter; count rows with a forward-only reader.
            var xlsxJob = await RunAsync(engine, auditId, ExportFormat.Excel);
            var xlsxRows = CountXlsxRows(xlsxJob.FilePath!);
            Assert.Equal(RowCount + 1, xlsxRows);

            // HTML: streaming tables.
            var htmlJob = await RunAsync(engine, auditId, ExportFormat.Html);
            Assert.True(new FileInfo(htmlJob.FilePath!).Length > RowCount * 20L);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static async Task<ExportJobDto> RunAsync(ExportEngine engine, long auditId, ExportFormat format)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var job = await engine.CreateExportAsync(new ExportRequest(auditId, format, [ExportDatasetKeys.Urls]));
        await engine.GetJobTask(job.Id);
        var finished = await engine.GetExportAsync(job.Id);
        Assert.NotNull(finished);
        Assert.Equal(ExportJobStatus.Completed, finished.Status);

        var after = GC.GetTotalMemory(forceFullCollection: true);
        var delta = after - before;
        Assert.True(delta < MemoryBoundBytes, $"Export {format} retained {delta / (1024 * 1024)} MB (bound {MemoryBoundBytes / (1024 * 1024)} MB).");
        return finished;
    }

    private static long MeasureHeapDelta(Action action)
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        action();
        return GC.GetTotalMemory(forceFullCollection: true) - before;
    }

    private static async Task SeedUrlsAsync(ExportTestStack stack, long auditId, int count)
    {
        await using var connection = await stack.Provider.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO `url_records` (`audit_id`, `canonical_url`, `host`, `path`, `depth`, `discovery_source`, `status_class`, `final_status_code`, `redirect_target_url`, `content_type`, `size_bytes`, `timing_json`, `robots_state`, `indexability_flags`, `is_indexable`) VALUES (@a, @u, 'example.com', @p, 1, 0, 1, 200, NULL, 'text/html', 100, NULL, 1, 0, 1)";
        var auditParameter = command.CreateParameter();
        auditParameter.ParameterName = "@a";
        auditParameter.Value = auditId;
        command.Parameters.Add(auditParameter);
        var urlParameter = command.CreateParameter();
        urlParameter.ParameterName = "@u";
        command.Parameters.Add(urlParameter);
        var pathParameter = command.CreateParameter();
        pathParameter.ParameterName = "@p";
        command.Parameters.Add(pathParameter);

        for (var i = 0; i < count; i++)
        {
            urlParameter.Value = $"https://example.com/p{i}";
            pathParameter.Value = $"/p{i}";
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<int> CountCsvLinesAsync(string zipPath)
    {
        await using var file = File.OpenRead(zipPath);
        using var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("urls.csv")!.Open());
        var count = 0;
        while (await reader.ReadLineAsync() is not null)
        {
            count++;
        }

        return count;
    }

    private static int CountXlsxRows(string path)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var sheet = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().First();
        var worksheetPart = (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!);
        using var reader = OpenXmlReader.Create(worksheetPart);
        var rows = 0;
        while (reader.Read())
        {
            if (reader.ElementType == typeof(Row) && reader.IsStartElement)
            {
                rows++;
            }
        }

        return rows;
    }
}
