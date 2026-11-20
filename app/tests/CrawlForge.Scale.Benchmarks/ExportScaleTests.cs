using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Scale.Benchmarks.Support;
using Xunit;

namespace CrawlForge.Scale.Benchmarks;

/// <summary>
/// Export writers at 500k scale (criterion #19, FS-CRAWL-001 §8.4): the seeded
/// 500,000-URL audit exported through the real export engine — CSV-per-dataset
/// zip and forward-only multi-sheet Excel — with exact streamed row-count
/// proofs and runtime memory sampling during generation.
/// </summary>
[Trait("Category", "Scale")]
public sealed class ExportScaleTests
{
    private const long MemoryBoundBytes = 2_500_000_000;

    [Fact]
    public async Task Export_Writers_Stream_500k_Dataset_With_Bounded_Memory()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var cancellationToken = timeout.Token;
        ScalePaths.EnsureArtifacts();

        var manifest = await EnsureSeededAsync(cancellationToken);
        await using var backend = await ScaleBackend.StartAsync(ScalePaths.SeededDataDirectory, cancellationToken);
        var engine = backend.GetRequired<IExportEngine>();

        // ── CSV zip: urls + findings + links + ngrams + custom-extraction ────
        var csvWatch = Stopwatch.StartNew();
        var csvJob = await engine.CreateExportAsync(new ExportRequest(manifest.AuditId, ExportFormat.Csv,
            ["urls", "findings", "links", "ngrams", "custom-extraction"]), cancellationToken);

        var peakWorkingSet = await AwaitJobWithMemorySamplingAsync(backend, engine, csvJob.Id, cancellationToken);
        csvWatch.Stop();

        var csvCompleted = await engine.GetExportAsync(csvJob.Id, cancellationToken);
        Assert.NotNull(csvCompleted);
        Assert.Equal(ExportJobStatus.Completed, csvCompleted!.Status);
        Assert.NotNull(csvCompleted.FilePath);
        Assert.True(File.Exists(csvCompleted.FilePath));

        var csvRows = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var stream = await engine.OpenExportFileAsync(csvJob.Id, cancellationToken))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                csvRows[entry.Name] = await CountCsvRowsAsync(entry, cancellationToken);
            }
        }

        // Header + one line per row, streamed forward-only.
        Assert.Equal(DeterministicDataset.UrlCount + 1, csvRows["urls.csv"]);
        Assert.Equal(DeterministicDataset.FindingCount + 1, csvRows["findings.csv"]);
        Assert.Equal(DeterministicDataset.LinkCount + 1, csvRows["links.csv"]);
        Assert.Equal(DeterministicDataset.NgramCount + 1, csvRows["ngrams.csv"]);
        Assert.Equal(DeterministicDataset.ExtractionCount + 1, csvRows["custom-extraction.csv"]);

        // ── Excel: forward-only OpenXml writer over the 500k URL sheet ───────
        var excelWatch = Stopwatch.StartNew();
        var excelJob = await engine.CreateExportAsync(new ExportRequest(manifest.AuditId, ExportFormat.Excel,
            ["summary", "urls", "findings"]), cancellationToken);
        var excelPeak = await AwaitJobWithMemorySamplingAsync(backend, engine, excelJob.Id, cancellationToken);
        excelWatch.Stop();

        var excelCompleted = await engine.GetExportAsync(excelJob.Id, cancellationToken);
        Assert.NotNull(excelCompleted);
        Assert.Equal(ExportJobStatus.Completed, excelCompleted!.Status);
        Assert.NotNull(excelCompleted.FilePath);
        var excelLength = new FileInfo(excelCompleted.FilePath!).Length;
        Assert.True(excelLength > 1_000_000, $"Excel export suspiciously small: {excelLength} bytes.");

        // OpenXml workbook integrity: the URL worksheet part exists and carries
        // the full row population (streamed substring count, no DOM load).
        long urlSheetRows;
        await using (var stream = await engine.OpenExportFileAsync(excelJob.Id, cancellationToken))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
            // Resolve the URL worksheet BY NAME through the workbook relationship
            // map. The old first-entry-over-10MB heuristic selected the SUMMARY
            // sheet once D-021's three new scoring categories grew it past 10 MB
            // (phase-10 wave-10A0 gate); name resolution is stable regardless of
            // sheet sizes. The row-population assertion is unchanged.
            var urlSheet = ResolveWorksheet(archive, "urls");
            Assert.NotNull(urlSheet);
            Assert.True(urlSheet!.Length > 10_000_000, "URL worksheet suspiciously small.");
            urlSheetRows = await CountSheetRowsAsync(urlSheet, cancellationToken);
        }
        Assert.Equal(DeterministicDataset.UrlCount + 1, urlSheetRows);

        var peak = Math.Max(peakWorkingSet, excelPeak);
        Assert.True(peak < MemoryBoundBytes, $"Export peak working set {peak:N0} exceeded the bound.");

        ScaleBackend.WriteResult("export-scale", new
        {
            auditId = manifest.AuditId,
            csv = new
            {
                file = csvCompleted.FilePath,
                elapsedSeconds = csvWatch.Elapsed.TotalSeconds,
                rowsPerDataset = csvRows,
                peakWorkingSetBytes = peakWorkingSet
            },
            excel = new
            {
                file = excelCompleted.FilePath,
                fileBytes = excelLength,
                elapsedSeconds = excelWatch.Elapsed.TotalSeconds,
                urlSheetRows,
                peakWorkingSetBytes = excelPeak
            },
            combinedPeakWorkingSetBytes = peak,
            memoryBoundBytes = MemoryBoundBytes
        });
    }

    private static async Task<long> AwaitJobWithMemorySamplingAsync(
        ScaleBackend backend, IExportEngine engine, long jobId, CancellationToken cancellationToken)
    {
        long peak = 0;
        while (true)
        {
            var job = await engine.GetExportAsync(jobId, cancellationToken);
            Assert.NotNull(job);
            if (job!.Status is ExportJobStatus.Completed or ExportJobStatus.Failed)
            {
                Assert.Equal(ExportJobStatus.Completed, job.Status);
                return peak;
            }
            var health = await backend.GetDataAsync<SystemHealthWire>("/api/system/health", cancellationToken);
            peak = Math.Max(peak, health.BackendWorkingSetBytes);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static async Task<long> CountCsvRowsAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        long rows = 0;
        while (await reader.ReadLineAsync(cancellationToken) is not null)
        {
            rows++;
        }
        return rows;
    }

    private static async Task<long> CountSheetRowsAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var buffer = new char[1 << 20];
        long rows = 0;
        var carry = "";
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var chunk = carry + new string(buffer, 0, read);
            var index = 0;
            // The product OpenXml writer emits a namespace prefix: rows are
            // "<x:row r=…>" (xmlns:x="…spreadsheetml…"), NOT "<row>". The leading
            // '<' can never appear unescaped in cell text, so this token cannot
            // false-positive on inline string content.
            while ((index = chunk.IndexOf("<x:row", index, StringComparison.Ordinal)) >= 0)
            {
                var after = index + 6;
                if (after >= chunk.Length)
                {
                    // Partial "<x:row" token split across the chunk boundary —
                    // leave it in the carry and complete it next read.
                    break;
                }
                // Count a row start tag for ANY delimiter: "<x:row r=…>",
                // "<x:row>" (no attributes) or "<x:row/>".
                var c = chunk[after];
                if (c is ' ' or '>' or '/' or '\t' or '\r' or '\n')
                {
                    rows++;
                }
                index = after;
            }
            // Keep only the last 6 chars (max partial "<x:row" without its
            // delimiter): a counted match is ≥7 chars, so it can never sit
            // wholly inside the 6-char carry — no double counting.
            carry = chunk.Length >= 6 ? chunk[^6..] : chunk;
        }
        return rows;
    }

    private static ZipArchiveEntry? ResolveWorksheet(ZipArchive archive, string sheetName)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbookEntry is null || relsEntry is null)
        {
            return null;
        }

        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace officeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        using var workbookStream = workbookEntry.Open();
        var workbook = XDocument.Load(workbookStream);
        var relationshipId = workbook.Descendants(main + "sheet")
            .FirstOrDefault(sheet => (string?)sheet.Attribute("name") == sheetName)
            ?.Attribute(officeRel + "id")?.Value;
        if (relationshipId is null)
        {
            return null;
        }

        using var relsStream = relsEntry.Open();
        var relationships = XDocument.Load(relsStream);
        var target = relationships.Descendants(packageRel + "Relationship")
            .FirstOrDefault(relationship => (string?)relationship.Attribute("Id") == relationshipId)
            ?.Attribute("Target")?.Value;
        if (target is null)
        {
            return null;
        }

        // Targets may be package-absolute ("/xl/worksheets/sheet2.xml") or
        // relative to the xl/ part folder ("worksheets/sheet2.xml").
        var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        return archive.GetEntry(path);
    }

    private static async Task<SeedManifest> EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(ScalePaths.SeedManifestPath))
        {
            var manifest = JsonSerializer.Deserialize<SeedManifest>(
                await File.ReadAllTextAsync(ScalePaths.SeedManifestPath, cancellationToken),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (manifest is not null && manifest.SchemaVersion == 2
                && manifest.Seed == DeterministicDataset.GeneratorSeed
                && File.Exists(Path.Combine(ScalePaths.SeededDataDirectory, "crawlforge.db")))
            {
                return manifest;
            }
        }

        // Self-contained fallback: seed through the real persistence stack.
        await using var backend = await ScaleBackend.StartAsync(ScalePaths.SeededDataDirectory, cancellationToken);
        var summary = await new DeterministicDataset(backend.Services).SeedAsync(cancellationToken);
        var fresh = new SeedManifest(2, DeterministicDataset.GeneratorSeed, ScalePaths.SeededDataDirectory,
            summary.ProjectId, summary.ConfigurationId, summary.AuditId,
            summary.UrlCount, summary.FindingCount, summary.LinkCount,
            summary.ExtractionCount, summary.NgramCount, summary.SiteSummaryCount,
            DateTimeOffset.UtcNow.ToString("O"), summary.SeedSeconds);
        await File.WriteAllTextAsync(ScalePaths.SeedManifestPath,
            JsonSerializer.Serialize(fresh, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);
        return fresh;
    }
}
