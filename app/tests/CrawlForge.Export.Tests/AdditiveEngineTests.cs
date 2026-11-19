using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Export.Datasets;
using CrawlForge.Export.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Export.Tests;

/// <summary>
/// Additive engine surface proofs (WP-09-10): json/jsonl/text/markdown jobs
/// return immediately, generate PARSEABLE local files in the background,
/// publish export-progress with the fixed wire shape, and fail cleanly when
/// page-content wiring is absent. The locked IExportEngine surface is
/// untouched; additive jobs read through GetAdditiveExportAsync.
/// </summary>
public sealed class AdditiveEngineTests : IAsyncLifetime
{
    private ExportTestStack _stack = null!;
    private string _outputDirectory = null!;
    private string _artifactRoot = null!;
    private long _auditId;
    private long _urlId;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _stack = new ExportTestStack();
        _outputDirectory = Path.Combine(Path.GetTempPath(), "cf-additive-out-" + Guid.NewGuid().ToString("N"));
        _artifactRoot = Path.Combine(Path.GetTempPath(), "cf-additive-artifacts-" + Guid.NewGuid().ToString("N"));
        _auditId = await _stack.SeedAuditAsync();
        _urlId = await _stack.SeedUrlAsync(_auditId, "https://example.com/", path: "/");
        await _stack.SeedUrlAsync(_auditId, "https://example.com/noindex", indexable: false);
        await _stack.SeedFindingAsync(_auditId, "indexability.noindex", FindingSeverity.Warning, _urlId);
        await _stack.SeedFindingAsync(_auditId, "content.duplicate-title", FindingSeverity.Warning, _urlId);
        var store = new CompressedContentStore(_artifactRoot, _stack.Provider);
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes("<html><head><title>Home</title></head><body><h1>Welcome</h1><p>Fixture page.</p></body></html>"));
        await store.PutAsync(_auditId, _urlId, ContentArtifactKind.Body, body);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await _stack.DisposeAsync();
        foreach (var directory in new[] { _outputDirectory, _artifactRoot })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private ExportEngine CreateEngine(bool withContentStore = true)
        => new(
            _stack.Provider,
            new ExportJobRegistry(),
            new ExportEventPublisher(_stack.Hub),
            new ExportOptions { OutputDirectory = _outputDirectory },
            templateDirectory: null,
            contentStore: withContentStore ? new CompressedContentStore(_artifactRoot, _stack.Provider) : null,
            domParser: withContentStore ? new AngleSharpDomParser() : null);

    private static async Task<AdditiveExportJobDto> RunToCompletionAsync(ExportEngine engine, long auditId, string format, IReadOnlyList<string> datasets)
    {
        var job = await engine.CreateAdditiveExportAsync(auditId, format, datasets);
        Assert.Equal(ExportJobStatus.Queued, job.Status); // returns immediately
        Assert.Equal(format, job.Format);
        await engine.GetJobTask(job.Id);
        var finished = await engine.GetAdditiveExportAsync(job.Id);
        Assert.NotNull(finished);
        Assert.Equal(ExportJobStatus.Completed, finished.Status);
        Assert.NotNull(finished.FilePath);
        Assert.True(File.Exists(finished.FilePath));
        return finished;
    }

    /// <summary>JSON job: parseable summary-first document with fixture findings + ids.</summary>
    [Fact]
    public async Task JsonJob_Generates_Parseable_Agent_Document()
    {
        using var engine = CreateEngine();
        var job = await RunToCompletionAsync(engine, _auditId, AdditiveExportFormats.Json, [ExportDatasetKeys.Findings, ExportDatasetKeys.Urls]);

        Assert.EndsWith(".json", job.FilePath);
        var bytes = await File.ReadAllBytesAsync(job.FilePath!);
        using var document = JsonDocument.Parse(bytes);
        var datasets = document.RootElement.GetProperty("datasets");
        Assert.Equal("findings", datasets[0].GetProperty("key").GetString());
        var rules = datasets[0].GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("rule_key").GetString()).ToList();
        Assert.Contains("indexability.noindex", rules);
        Assert.Contains("content.duplicate-title", rules);
        Assert.All(datasets[0].GetProperty("rows").EnumerateArray(), r => Assert.Equal(JsonValueKind.Number, r.GetProperty("finding_id").ValueKind));

        // Locked surface stays honest: additive jobs are not ExportFormat jobs.
        Assert.Null(await engine.GetExportAsync(job.Id));
        await using var stream = await engine.OpenExportFileAsync(job.Id);
        Assert.True(stream.CanRead);
    }

    /// <summary>JSON Lines job: every line parseable, dataset-tagged.</summary>
    [Fact]
    public async Task JsonLinesJob_Generates_Parseable_Lines()
    {
        using var engine = CreateEngine();
        var job = await RunToCompletionAsync(engine, _auditId, AdditiveExportFormats.JsonLines, [ExportDatasetKeys.Urls]);

        Assert.EndsWith(".jsonl", job.FilePath);
        var lines = (await File.ReadAllTextAsync(job.FilePath!)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var parsed = JsonDocument.Parse(line);
            Assert.Equal("urls", parsed.RootElement.GetProperty("dataset").GetString());
            Assert.Equal(JsonValueKind.Number, parsed.RootElement.GetProperty("url_id").ValueKind);
        }
    }

    /// <summary>Text + Markdown jobs: parseable per-page bundles from the content store.</summary>
    [Theory]
    [InlineData(AdditiveExportFormats.Text, ".txt")]
    [InlineData(AdditiveExportFormats.Markdown, ".md")]
    public async Task PageContentJobs_Generate_Parseable_Bundles(string format, string pageExtension)
    {
        using var engine = CreateEngine();
        var job = await RunToCompletionAsync(engine, _auditId, format, []);

        Assert.EndsWith(".zip", job.FilePath);
        await using var file = File.OpenRead(job.FilePath!);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, e => e.Name == "manifest.json");
        var pageEntry = Assert.Single(archive.Entries, e => e.Name.EndsWith(pageExtension, StringComparison.Ordinal));
        Assert.StartsWith(_urlId + "-", pageEntry.Name);
        using var reader = new StreamReader(pageEntry.Open());
        var content = await reader.ReadToEndAsync();
        Assert.Contains("Welcome", content);
        if (format == AdditiveExportFormats.Markdown)
        {
            Assert.Contains("# Home", content);
            Assert.Contains("> Source: https://example.com/", content);
        }
    }

    /// <summary>Progress events carry the additive wire format + fixed shape.</summary>
    [Fact]
    public async Task AdditiveJob_Progress_Events_Carry_Wire_Format_And_String_Ids()
    {
        using var engine = CreateEngine();
        var job = await RunToCompletionAsync(engine, _auditId, AdditiveExportFormats.Json, [ExportDatasetKeys.Urls]);

        var progress = _stack.Hub.Events.Where(e => e.Name == ExportHubEventNames.ExportProgress).Select(e => e.PayloadJson).ToList();
        Assert.Contains(progress, p => p.Contains("\"state\":\"queued\"") && p.Contains("\"format\":\"json\""));
        Assert.Contains(progress, p => p.Contains("\"state\":\"completed\""));
        Assert.Contains(progress, p => p.Contains($"\"exportId\":\"{job.Id}\""));
        Assert.Contains(progress, p => p.Contains($"\"auditId\":\"{_auditId}\""));
        Assert.DoesNotContain(progress, p => p.Contains("exportJobId"));
        Assert.DoesNotContain(progress, p => p.Contains("\"status\""));
    }

    /// <summary>Page-content formats fail cleanly without Host wiring (never a caller exception).</summary>
    [Fact]
    public async Task PageContent_Without_Wiring_Fails_Job_Cleanly()
    {
        using var engine = CreateEngine(withContentStore: false);
        var job = await engine.CreateAdditiveExportAsync(_auditId, AdditiveExportFormats.Text, []);
        await engine.GetJobTask(job.Id);

        var finished = await engine.GetAdditiveExportAsync(job.Id);
        Assert.NotNull(finished);
        Assert.Equal(ExportJobStatus.Failed, finished.Status);
        Assert.Contains(_stack.Hub.Events, e => e.Name == ExportHubEventNames.ExportProgress && e.PayloadJson.Contains("\"state\":\"failed\""));
    }

    /// <summary>Unknown additive keys are rejected at creation.</summary>
    [Fact]
    public async Task Unknown_Additive_Format_Is_Rejected()
    {
        using var engine = CreateEngine();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateAdditiveExportAsync(_auditId, "docx", []));
    }

    /// <summary>Baseline jobs still read through both surfaces (unified wire projection).</summary>
    [Fact]
    public async Task Baseline_Jobs_Read_Through_Additive_Projection()
    {
        using var engine = CreateEngine();
        var job = await engine.CreateExportAsync(new Contracts.Api.ExportRequest(_auditId, ExportFormat.Csv, []));
        await engine.GetJobTask(job.Id);

        var locked = await engine.GetExportAsync(job.Id);
        Assert.NotNull(locked);
        var additive = await engine.GetAdditiveExportAsync(job.Id);
        Assert.NotNull(additive);
        Assert.Equal("csv", additive.Format);
        Assert.Equal(locked.Id, additive.Id);
    }
}