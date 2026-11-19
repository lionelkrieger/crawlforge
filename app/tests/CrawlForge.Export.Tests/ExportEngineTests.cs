using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;
using CrawlForge.Export.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Export engine proofs: immediate job creation, background generation of all
/// five formats into valid local files (criterion #7), hub progress events,
/// file download, and the failed-job path (never an audit failure).
/// </summary>
public sealed class ExportEngineTests : IAsyncLifetime
{
    private ExportTestStack _stack = null!;
    private string _outputDirectory = null!;
    private long _auditId;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _stack = new ExportTestStack();
        _outputDirectory = Path.Combine(Path.GetTempPath(), "cf-export-out-" + Guid.NewGuid().ToString("N"));
        _auditId = await _stack.SeedAuditAsync();
        var urlId = await _stack.SeedUrlAsync(_auditId, "https://example.com/", path: "/");
        await _stack.SeedUrlAsync(_auditId, "https://example.com/page");
        await _stack.SeedFindingAsync(_auditId, "content.readability", FindingSeverity.Warning, urlId);
        await _stack.SeedLinkAsync(_auditId, urlId, "https://example.com/page", "page");
        await _stack.SeedMetricAsync(_auditId, "ngram-top", 3);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await _stack.DisposeAsync();
        try
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private ExportEngine CreateEngine()
        => new(_stack.Provider, new ExportJobRegistry(), new ExportEventPublisher(_stack.Hub), new ExportOptions { OutputDirectory = _outputDirectory });

    private static async Task<ExportJobDto> RunToCompletionAsync(ExportEngine engine, ExportRequest request)
    {
        var job = await engine.CreateExportAsync(request);
        Assert.Equal(ExportJobStatus.Queued, job.Status); // returns immediately
        await engine.GetJobTask(job.Id);
        var finished = await engine.GetExportAsync(job.Id);
        Assert.NotNull(finished);
        Assert.Equal(ExportJobStatus.Completed, finished.Status);
        Assert.NotNull(finished.FilePath);
        Assert.True(File.Exists(finished.FilePath));
        return finished;
    }

    /// <summary>All five formats produce non-empty valid local files.</summary>
    [Theory]
    [InlineData(ExportFormat.Excel, ".xlsx")]
    [InlineData(ExportFormat.Csv, ".zip")]
    [InlineData(ExportFormat.Html, ".html")]
    [InlineData(ExportFormat.Pdf, ".pdf")]
    [InlineData(ExportFormat.XmlSitemap, ".xml")]
    public async Task AllFormats_Generate_Valid_Local_Files(ExportFormat format, string extension)
    {
        using var engine = CreateEngine();
        var job = await RunToCompletionAsync(engine, new ExportRequest(_auditId, format, []));

        Assert.EndsWith(extension, job.FilePath);
        Assert.True(new FileInfo(job.FilePath!).Length > 0);

        await using var stream = await engine.OpenExportFileAsync(job.Id);
        Assert.True(stream.CanRead);
    }

    /// <summary>Progress flows over the hub: queued → running → completed.</summary>
    [Fact]
    public async Task Progress_Events_Flow_Over_Hub()
    {
        using var engine = CreateEngine();
        await RunToCompletionAsync(engine, new ExportRequest(_auditId, ExportFormat.Csv, []));

        var progress = _stack.Hub.Events.Where(e => e.Name == ExportHubEventNames.ExportProgress).ToList();
        Assert.Contains(progress, e => e.PayloadJson.Contains("\"state\":\"queued\""));
        Assert.Contains(progress, e => e.PayloadJson.Contains("\"state\":\"running\""));
        Assert.Contains(progress, e => e.PayloadJson.Contains("\"state\":\"completed\""));
    }

    /// <summary>Generation failure yields a Failed job + event, never an exception to the caller.</summary>
    [Fact]
    public async Task Unknown_Format_Fails_Job_Without_Throwing()
    {
        using var engine = CreateEngine();
        var job = await engine.CreateExportAsync(new ExportRequest(_auditId, (ExportFormat)99, []));
        await engine.GetJobTask(job.Id);

        var finished = await engine.GetExportAsync(job.Id);
        Assert.NotNull(finished);
        Assert.Equal(ExportJobStatus.Failed, finished.Status);
        Assert.Contains(_stack.Hub.Events, e => e.Name == ExportHubEventNames.ExportProgress && e.PayloadJson.Contains("\"state\":\"failed\""));
    }

    /// <summary>Opening an incomplete or unknown job fails cleanly.</summary>
    [Fact]
    public async Task OpenExportFile_Rejects_Unknown_And_Incomplete_Jobs()
    {
        using var engine = CreateEngine();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OpenExportFileAsync(999));

        var job = await engine.CreateExportAsync(new ExportRequest(_auditId, (ExportFormat)99, []));
        await engine.GetJobTask(job.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.OpenExportFileAsync(job.Id));
    }

    /// <summary>Dataset selection reaches the workbook (custom extraction sheet present).</summary>
    [Fact]
    public async Task Dataset_Selection_Drives_Sheets()
    {
        using var engine = CreateEngine();
        var job = await RunToCompletionAsync(engine, new ExportRequest(_auditId, ExportFormat.Excel, [ExportDatasetKeys.Urls, ExportDatasetKeys.Findings]));

        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(job.FilePath!, isEditable: false);
        var names = document.WorkbookPart!.Workbook!.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Select(s => s.Name!.Value).ToList();
        Assert.Equal(["urls", "findings"], names);
    }
}
