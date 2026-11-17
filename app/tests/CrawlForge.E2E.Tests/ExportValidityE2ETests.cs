using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrawlForge.Contracts.Enums;
using CrawlForge.E2E.Tests.Fixtures;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PdfSharp.Pdf.IO;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// WP-07-01 criterion #7 — export file validity over the composed backend:
/// a real crawl feeds the locked export engine through REST, and every
/// downloaded artifact is opened with an independent reader (OpenXml for
/// xlsx, PdfSharp for pdf, ZipArchive for the csv bundle, HTML inspection for
/// the self-contained report). Downloads stream raw bytes without a JSON
/// envelope (FS-CRAWL-001 §10.2). The two label tests flip green when defect
/// D10 (csv/html content-type + extension mismatch) is fixed.
/// </summary>
public sealed class ExportValidityE2ETests : IClassFixture<ExportValidityE2ETests.ExportSiteFixture>
{
    private readonly ExportSiteFixture _fixture;

    public ExportValidityE2ETests(ExportSiteFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task XlsxExport_IsAValidWorkbook_WithDatasetSheetsAndRows()
    {
        var exportId = await CreateCompletedExportAsync("xlsx");
        var (bytes, contentType, disposition) = await DownloadAsync(exportId);

        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", contentType);
        Assert.EndsWith(".xlsx", disposition);

        using var stream = new MemoryStream(bytes);
        using var workbook = SpreadsheetDocument.Open(stream, isEditable: false);
        Assert.NotNull(workbook.WorkbookPart);
        var sheets = workbook.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().ToList();
        var names = sheets.Select(s => s.Name!.Value!).ToList();
        foreach (var dataset in new[] { "summary", "urls", "findings", "links" })
        {
            Assert.Contains(dataset, names);
        }

        // Every dataset sheet carries a header and data rows.
        foreach (var sheet in sheets)
        {
            var part = (WorksheetPart)workbook.WorkbookPart.GetPartById(sheet.Id!.Value!);
            var rows = part.Worksheet!.Descendants<Row>().ToList();
            Assert.True(rows.Count >= 2, $"Sheet '{sheet.Name}' has no data rows.");
        }
    }

    [Fact]
    public async Task PdfExport_IsAValidPdf_WithAtLeastOnePage()
    {
        var exportId = await CreateCompletedExportAsync("pdf");
        var (bytes, contentType, disposition) = await DownloadAsync(exportId);

        Assert.Equal("application/pdf", contentType);
        Assert.EndsWith(".pdf", disposition);
        Assert.True(bytes.Length > 4);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));

        using var stream = new MemoryStream(bytes);
        using var document = PdfReader.Open(stream);
        Assert.True(document.PageCount >= 1, "PDF summary has no pages.");
    }

    [Fact]
    public async Task CsvExport_IsAZipOfRfc4180Csvs_OnePerDataset()
    {
        var exportId = await CreateCompletedExportAsync("csv");
        var (bytes, _, _) = await DownloadAsync(exportId);

        // The locked CsvExportWriter (§8.4 "CSV per dataset") emits one zip
        // entry per dataset; verify the artifact bytes independently.
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = archive.Entries.Select(e => e.FullName).ToList();
        foreach (var dataset in new[] { "summary", "urls", "findings", "links" })
        {
            Assert.Contains(dataset + ".csv", entries);
        }

        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var content = await reader.ReadToEndAsync();
            var records = SplitRfc4180Records(content);
            Assert.True(records.Count >= 2, $"{entry.FullName} has no data rows.");
            var headerColumns = CountRfc4180Fields(records[0]);
            Assert.All(records, record => Assert.Equal(headerColumns, CountRfc4180Fields(record)));
        }

        // The urls dataset carries the crawled fixture URLs.
        var urlsEntry = archive.GetEntry("urls.csv")!;
        using var urlsReader = new StreamReader(urlsEntry.Open(), Encoding.UTF8);
        var urlsCsv = await urlsReader.ReadToEndAsync();
        Assert.Contains("/p/1", urlsCsv);
    }

    [Fact]
    public async Task CsvDownload_LabelsMatchTheZipBytes()
    {
        // Defect D10 flip: the artifact is a zip archive, so the download must
        // be labeled application/zip with a .zip name (not text/csv/.csv).
        var exportId = await CreateCompletedExportAsync("csv");
        var (_, contentType, disposition) = await DownloadAsync(exportId);
        Assert.Equal("application/zip", contentType);
        Assert.EndsWith(".zip", disposition);
    }

    [Fact]
    public async Task HtmlExport_IsASelfContainedHtmlReport()
    {
        var exportId = await CreateCompletedExportAsync("html");
        var (bytes, _, _) = await DownloadAsync(exportId);

        var html = Encoding.UTF8.GetString(bytes);
        Assert.Contains($"CrawlForge Report — Audit {_fixture.AuditId}", html);
        foreach (var dataset in new[] { "summary", "urls", "findings", "links" })
        {
            Assert.Contains($"<h2>{dataset}</h2>", html);
        }
        Assert.Contains("<table>", html);
        // Self-contained: no external resource references (§10.2).
        Assert.False(Regex.IsMatch(html, "(src|href)\\s*=\\s*[\"']https?://", RegexOptions.IgnoreCase),
            "HTML report references external resources.");
    }

    [Fact]
    public async Task HtmlDownload_LabelsMatchTheHtmlBytes()
    {
        // Defect D10 flip: the artifact is a single HTML document, so the
        // download must be labeled text/html with a .html name (not zip).
        var exportId = await CreateCompletedExportAsync("html");
        var (_, contentType, disposition) = await DownloadAsync(exportId);
        Assert.Equal("text/html", contentType);
        Assert.EndsWith(".html", disposition);
    }

    [Fact]
    public async Task ExportValidation_AndMissingArtifacts_UseTheEnvelope()
    {
        var backend = _fixture.Backend;

        var unknownAudit = await backend.Http.PostAsJsonAsync("/api/exports", new
        {
            auditId = "999999",
            format = "csv",
            datasets = new[] { "summary" }
        });
        Assert.Equal(HttpStatusCode.NotFound, unknownAudit.StatusCode);
        var unknownEnvelope = await WireJson.EnvelopeAsync(unknownAudit);
        Assert.Equal("not-found", unknownEnvelope.GetProperty("error").GetProperty("code").GetString());

        var badFormat = await backend.Http.PostAsJsonAsync("/api/exports", new
        {
            auditId = _fixture.AuditId.ToString(),
            format = "docx",
            datasets = new[] { "summary" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, badFormat.StatusCode);
        var formatEnvelope = await WireJson.EnvelopeAsync(badFormat);
        Assert.Equal("validation", formatEnvelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Object, formatEnvelope.GetProperty("error").GetProperty("fieldErrors").ValueKind);

        var emptyDatasets = await backend.Http.PostAsJsonAsync("/api/exports", new
        {
            auditId = _fixture.AuditId.ToString(),
            format = "csv",
            datasets = Array.Empty<string>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, emptyDatasets.StatusCode);

        var missingFile = await backend.Http.GetAsync("/api/exports/999999/file");
        Assert.Equal(HttpStatusCode.NotFound, missingFile.StatusCode);
        var missingEnvelope = await WireJson.EnvelopeAsync(missingFile);
        Assert.Equal("not-found", missingEnvelope.GetProperty("error").GetProperty("code").GetString());
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>Splits CSV content into records, honoring quoted embedded newlines.</summary>
    private static List<string> SplitRfc4180Records(string content)
    {
        var records = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '"')
            {
                current.Append(c);
                if (inQuotes && i + 1 < content.Length && content[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == '\r' && !inQuotes && i + 1 < content.Length && content[i + 1] == '\n')
            {
                records.Add(current.ToString());
                current.Clear();
                i++;
            }
            else if (c == '\n' && !inQuotes)
            {
                records.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
        {
            records.Add(current.ToString());
        }
        return records.Where(r => r.Length > 0).ToList();
    }

    /// <summary>Counts fields in one RFC-4180 record (commas inside quotes do not split).</summary>
    private static int CountRfc4180Fields(string record)
    {
        var count = 1;
        var inQuotes = false;
        for (var i = 0; i < record.Length; i++)
        {
            var c = record[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < record.Length && record[i + 1] == '"')
                {
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                count++;
            }
        }
        return count;
    }

    private async Task<string> CreateCompletedExportAsync(string format)
    {
        var backend = _fixture.Backend;
        var create = await backend.Http.PostAsJsonAsync("/api/exports", new
        {
            auditId = _fixture.AuditId.ToString(),
            format,
            datasets = new[] { "summary", "urls", "findings", "links" }
        });
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"Export create failed: {(int)create.StatusCode} {createBody}");
        using var createDocument = JsonDocument.Parse(createBody);
        var exportId = createDocument.RootElement.GetProperty("data").GetProperty("exportId").GetString()!;

        // Long operations return immediately; poll the additive job route.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var job = await WireJson.EnvelopeAsync(await backend.Http.GetAsync($"/api/exports/{exportId}"));
            var state = job.GetProperty("data").GetProperty("state").GetString();
            if (state == "completed")
            {
                return exportId;
            }
            Assert.NotEqual("failed", state);
            await Task.Delay(100);
        }
        throw new TimeoutException($"Export {exportId} ({format}) did not complete within 60s.");
    }

    private async Task<(byte[] Bytes, string ContentType, string Disposition)> DownloadAsync(string exportId)
    {
        var response = await _fixture.Backend.Http.GetAsync($"/api/exports/{exportId}/file");
        Assert.True(response.IsSuccessStatusCode, $"Download failed: {(int)response.StatusCode}");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var disposition = response.Content.Headers.ContentDisposition?.FileName ?? "";
        return (bytes, contentType, disposition.Trim('"'));
    }

    /// <summary>One real crawl shared by every export-format proof.</summary>
    public sealed class ExportSiteFixture : IAsyncLifetime
    {
        public LoopbackOrigin Origin = null!;
        public E2EBackend Backend = null!;
        public long AuditId;

        public async Task InitializeAsync()
        {
            Origin = await LoopbackOrigin.StartAsync(routes =>
            {
                routes.Map("/", OriginResponse.Html(FixturePages.Page(
                    "Export index", "<p>Export fixture index.</p>" + FixturePages.Links(["/p/1", "/p/2"]))));
                routes.Map("/p/1", OriginResponse.Html(FixturePages.Page(
                    "Export page one", "<p>First export page content.</p>")));
                routes.Map("/p/2", OriginResponse.Html(FixturePages.Page(
                    "Export page two", "<p>Second export page content.</p>")));
            });
            Backend = await E2EBackend.StartAsync();
            var (projectId, configurationId) = await Backend.SeedProjectAsync(Origin.BaseUrl);
            AuditId = await Backend.StartAuditAsync(projectId, configurationId);
            await Backend.WaitForStatusAsync(AuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));
        }

        public async Task DisposeAsync()
        {
            await Backend.DisposeAsync();
            await Origin.DisposeAsync();
        }
    }
}
