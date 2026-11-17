using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.Repositories;
using CrawlForge.Scale.Benchmarks.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Scale.Benchmarks;

/// <summary>
/// Seed manifest published for the browser benchmark: the exact identifiers
/// and counts the seeded database carries, plus the deterministic seed so a
/// later run can verify reuse instead of re-seeding.
/// </summary>
/// <param name="SchemaVersion">Manifest layout version.</param>
/// <param name="Seed">Deterministic generator seed.</param>
/// <param name="DataDirectory">Backend data directory holding crawlforge.db.</param>
/// <param name="ProjectId">Seeded project identifier.</param>
/// <param name="ConfigurationId">Seeded configuration identifier.</param>
/// <param name="AuditId">Completed audit identifier the explorers browse.</param>
/// <param name="UrlCount">URL records in the database.</param>
/// <param name="FindingCount">Finding rows in the database.</param>
/// <param name="LinkCount">Link edges in the database.</param>
/// <param name="ExtractionCount">Custom-extraction values in the database.</param>
/// <param name="NgramCount">N-gram rows in the database.</param>
/// <param name="SiteSummaryCount">N-gram site-summary rows in the database.</param>
/// <param name="SeededAtUtc">Seed completion timestamp.</param>
/// <param name="SeedSeconds">Seed wall-clock duration.</param>
public sealed record SeedManifest(
    int SchemaVersion,
    int Seed,
    string DataDirectory,
    long ProjectId,
    long ConfigurationId,
    long AuditId,
    long UrlCount,
    long FindingCount,
    long LinkCount,
    long ExtractionCount,
    long NgramCount,
    long SiteSummaryCount,
    string SeededAtUtc,
    double SeedSeconds);

/// <summary>
/// Criterion #40 data foundation: a deterministic-seed 500,000-URL dataset
/// loaded through the real persistence stack, with repository read-back proofs
/// (totals, whitelisted filters, sorting, deep offsets, streaming). The seed is
/// idempotent: a matching manifest + verified database is reused, so the
/// browser benchmark and repeated verification runs do not re-seed.
/// </summary>
[Trait("Category", "Scale")]
[Trait("Category", "ScaleSeed")]
public sealed class DatasetSeedTests
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public async Task Seeds_500k_Dataset_Through_Real_Persistence_With_Readback_Proofs()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var cancellationToken = timeout.Token;
        ScalePaths.EnsureArtifacts();

        // Reuse path: a valid manifest + verified counts means the deterministic
        // dataset is already on disk (browser benchmark depends on this).
        var existing = await TryReuseAsync(cancellationToken);
        if (existing is not null)
        {
            ScaleBackend.WriteResult("dataset-seed", new
            {
                reused = true,
                manifest = existing,
                readback = "verified-on-reuse"
            });
            return;
        }

        ResetSeededDatabase();
        await using var backend = await ScaleBackend.StartAsync(ScalePaths.SeededDataDirectory, cancellationToken);
        var dataset = new DeterministicDataset(backend.Services);
        var summary = await dataset.SeedAsync(cancellationToken);

        Assert.Equal(DeterministicDataset.UrlCount, summary.UrlCount);
        Assert.Equal(DeterministicDataset.FindingCount, summary.FindingCount);
        Assert.Equal(DeterministicDataset.LinkCount, summary.LinkCount);
        Assert.Equal(DeterministicDataset.ExtractionCount, summary.ExtractionCount);
        Assert.Equal(DeterministicDataset.NgramCount, summary.NgramCount);
        Assert.Equal(DeterministicDataset.SiteSummaryCount, summary.SiteSummaryCount);

        // ── Read-back proofs through the real read repositories ──────────────
        var urls = backend.GetRequired<IReadRepository<UrlRecordDto>>();

        var totals = await urls.ListAsync(new ListQuery(Offset: 0, Limit: 1), cancellationToken);
        Assert.Equal(DeterministicDataset.UrlCount, totals.TotalCount);

        // Whitelisted equality filter: 9% of rows are client errors.
        var clientErrors = await urls.ListAsync(new ListQuery(
            Offset: 0, Limit: 5,
            Filters: new Dictionary<string, string> { ["StatusClass"] = ((int)CrawlStatusClass.ClientError).ToString() }),
            cancellationToken);
        Assert.Equal(45_000, clientErrors.TotalCount);
        Assert.All(clientErrors.Items, row => Assert.Equal(CrawlStatusClass.ClientError, row.StatusClass));

        // Sorting: deepest rows first.
        var deepest = await urls.ListAsync(new ListQuery(Offset: 0, Limit: 20, SortBy: "Depth", SortDescending: true), cancellationToken);
        Assert.All(deepest.Items, row => Assert.Equal(6, row.Depth));

        // Deep offset window at the middle of the dataset.
        var middle = await urls.ListAsync(new ListQuery(Offset: 250_000, Limit: 200), cancellationToken);
        Assert.Equal(200, middle.Items.Count);
        Assert.Equal(DeterministicDataset.UrlCount, middle.TotalCount);

        // Streaming read: bounded-memory full count with a filter (no materialization).
        var streaming = (IStreamingReadRepository<UrlRecordDto>)urls;
        long streamed = 0;
        await foreach (var row in streaming.StreamAsync(new ListQuery(
            Filters: new Dictionary<string, string> { ["IsIndexable"] = "true" }), cancellationToken))
        {
            Assert.True(row.IsIndexable);
            streamed++;
        }
        Assert.True(streamed > 100_000, $"Expected a large indexable subset, got {streamed}.");

        // Sibling entity totals.
        Assert.Equal(DeterministicDataset.FindingCount,
            (await backend.GetRequired<IReadRepository<FindingDto>>().ListAsync(new ListQuery(Limit: 1), cancellationToken)).TotalCount);
        Assert.Equal(DeterministicDataset.LinkCount,
            (await backend.GetRequired<IReadRepository<LinkEdgeDto>>().ListAsync(new ListQuery(Limit: 1), cancellationToken)).TotalCount);
        Assert.Equal(DeterministicDataset.ExtractionCount,
            (await backend.GetRequired<IReadRepository<ExtractedContentDto>>().ListAsync(new ListQuery(Limit: 1), cancellationToken)).TotalCount);
        Assert.Equal(DeterministicDataset.NgramCount,
            (await backend.GetRequired<IReadRepository<NgramFrequencyDto>>().ListAsync(new ListQuery(Limit: 1), cancellationToken)).TotalCount);
        Assert.Equal(DeterministicDataset.SiteSummaryCount,
            (await backend.GetRequired<IReadRepository<NgramSiteSummaryDto>>().ListAsync(new ListQuery(Limit: 1), cancellationToken)).TotalCount);

        // Repository CRUD parity on the seeded database (create → read → delete).
        var writes = backend.GetRequired<IWriteRepository<UrlRecordDto>>();
        var probe = await writes.CreateAsync(new UrlRecordDto(
            0, summary.AuditId, "https://site-0.example.test/crud-probe", "site-0.example.test",
            "/crud-probe", 0, DiscoverySource.Seed, CrawlStatusClass.Success, 200, null,
            "text/html", 128, null, RobotsState.Allowed, IndexabilityFlags.None, true), cancellationToken);
        var readBack = await urls.GetByIdAsync(probe.Id, cancellationToken);
        Assert.NotNull(readBack);
        Assert.Equal("https://site-0.example.test/crud-probe", readBack!.CanonicalUrl);
        Assert.True(await writes.DeleteAsync(probe.Id, cancellationToken));

        var manifest = new SeedManifest(
            2, DeterministicDataset.GeneratorSeed, ScalePaths.SeededDataDirectory,
            summary.ProjectId, summary.ConfigurationId, summary.AuditId,
            summary.UrlCount, summary.FindingCount, summary.LinkCount,
            summary.ExtractionCount, summary.NgramCount, summary.SiteSummaryCount,
            DateTimeOffset.UtcNow.ToString("O"), summary.SeedSeconds);
        await File.WriteAllTextAsync(ScalePaths.SeedManifestPath,
            JsonSerializer.Serialize(manifest, ManifestJson), cancellationToken);

        ScaleBackend.WriteResult("dataset-seed", new
        {
            reused = false,
            manifest,
            readback = new
            {
                totalUrls = totals.TotalCount,
                clientErrorFilter = clientErrors.TotalCount,
                deepOffsetRows = middle.Items.Count,
                streamedIndexable = streamed
            }
        });
    }

    private static async Task<SeedManifest?> TryReuseAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ScalePaths.SeedManifestPath) ||
            !File.Exists(Path.Combine(ScalePaths.SeededDataDirectory, "crawlforge.db")))
        {
            return null;
        }

        SeedManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SeedManifest>(
                await File.ReadAllTextAsync(ScalePaths.SeedManifestPath, cancellationToken), ManifestJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (manifest is null || manifest.SchemaVersion != 2 || manifest.Seed != DeterministicDataset.GeneratorSeed)
        {
            return null;
        }

        await using var backend = await ScaleBackend.StartAsync(ScalePaths.SeededDataDirectory, cancellationToken);
        var totals = await backend.GetRequired<IReadRepository<UrlRecordDto>>()
            .ListAsync(new ListQuery(Limit: 1), cancellationToken);
        return totals.TotalCount == manifest.UrlCount ? manifest : null;
    }

    private static void ResetSeededDatabase()
    {
        // Only ever touches the owned artifacts directory.
        var directory = ScalePaths.SeededDataDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "crawlforge.db*"))
        {
            File.Delete(file);
        }
    }
}
