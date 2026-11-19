using System.Text;
using CrawlForge.Contracts.Enums;
using CrawlForge.Export.Datasets;
using CrawlForge.Export.Tests.TestInfrastructure;
using CrawlForge.Export.Writers;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// PDF report completion proofs (WP-09-10, CAP-H-177): the findings section
/// carries a severity rollup, and the fixture's noindex + duplicate-titles
/// findings are present in the extractable (uncompressed) report text.
/// </summary>
public sealed class PdfFindingsRollupTests
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

    /// <summary>Findings render with rule keys and a severity rollup line.</summary>
    [Fact]
    public async Task Pdf_Findings_Carry_Rule_Keys_And_Severity_Rollup()
    {
        var dataset = new ExportDataset(
            ExportDatasetKeys.Findings,
            [
                new ExportColumn("rule_key", "Rule", "text"),
                new ExportColumn("category", "Category", "text"),
                new ExportColumn("severity", "Severity", "text"),
                new ExportColumn("url", "URL", "text"),
            ],
            RowsAsync(
                ["indexability.noindex", "Indexability", "Warning", "https://example.com/noindex"],
                ["content.duplicate-title", "Content", "Warning", "https://example.com/dup"],
                ["content.readability", "Content", "Error", "https://example.com/a"]));

        await using var output = new MemoryStream();
        await PdfSummaryWriter.WriteAsync(output, 42, SingleAsync(dataset));

        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.StartsWith("%PDF-", text);
        Assert.Contains("indexability.noindex", text);
        Assert.Contains("content.duplicate-title", text);
        Assert.Contains("Findings rendered: 3", text);
        Assert.Contains("Warning=2", text);
        Assert.Contains("Error=1", text);
    }

    /// <summary>Real-stack engine PDF: the fixture findings reach the report bytes.</summary>
    [Fact]
    public async Task EnginePdf_Contains_Fixture_Findings()
    {
        await using var stack = new ExportTestStack();
        var outputDirectory = Path.Combine(Path.GetTempPath(), "cf-pdf-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var auditId = await stack.SeedAuditAsync();
            var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/noindex", indexable: false);
            await stack.SeedFindingAsync(auditId, "indexability.noindex", FindingSeverity.Warning, urlId);
            await stack.SeedFindingAsync(auditId, "content.duplicate-title", FindingSeverity.Warning, urlId);

            using var engine = new ExportEngine(
                stack.Provider, new ExportJobRegistry(), new ExportEventPublisher(stack.Hub),
                new ExportOptions { OutputDirectory = outputDirectory });
            var job = await engine.CreateExportAsync(new Contracts.Api.ExportRequest(auditId, ExportFormat.Pdf, [ExportDatasetKeys.Findings]));
            await engine.GetJobTask(job.Id);
            var finished = await engine.GetExportAsync(job.Id);
            Assert.NotNull(finished);
            Assert.Equal(ExportJobStatus.Completed, finished.Status);

            var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(finished!.FilePath!));
            Assert.Contains("indexability.noindex", text);
            Assert.Contains("content.duplicate-title", text);
            Assert.Contains("Findings rendered: 2", text);
        }
        finally
        {
            try
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>Empty findings still render a zero rollup (never a crash).</summary>
    [Fact]
    public async Task EmptyFindings_Render_Zero_Rollup()
    {
        var dataset = new ExportDataset(
            ExportDatasetKeys.Findings,
            [new ExportColumn("rule_key", "Rule", "text"), new ExportColumn("severity", "Severity", "text")],
            RowsAsync());

        await using var output = new MemoryStream();
        await PdfSummaryWriter.WriteAsync(output, 1, SingleAsync(dataset));

        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Findings rendered: 0.", text);
    }
}