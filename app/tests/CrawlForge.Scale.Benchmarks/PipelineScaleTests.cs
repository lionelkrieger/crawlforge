using System.Diagnostics;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Scale.Benchmarks.Support;
using Xunit;

namespace CrawlForge.Scale.Benchmarks;

/// <summary>
/// Full audit pipeline at scale (criterion #19): a real 10,000-URL crawl
/// against a deterministic loopback origin through the product execution
/// engine — frontier discovery, fetch, parse, analysis, persistence — with
/// working-set sampling throughout, followed by a transactional retention
/// prune of the completed audit.
///
/// Scale note (WP-07-03): the former AuditExecutionEngine.HardUrlSafetyCap
/// (100,000 clamp) was REMOVED via WP-06-01 (unlimited sentinel default,
/// overrides honored exactly, re-audit queries page in bounded 1,000-row
/// batches — memory batch, not ceiling). The 500k+ scale target is carried by
/// the seeded dataset and the export-writer suite; this suite is the bounded
/// LIVE-pipeline proof (fetch + analyze + persist + prune end-to-end).
///
/// Throughput finding (WP-07-04 observation): a 100,000-URL live run was
/// measured at ~22 URLs/s fetch (~73 min) but only ~5.3 URLs/s analysis
/// (the full analyzer suite ≈ 200 ms/URL), so completing analysis of 100k
/// URLs takes ≈ 5 h — impractical for a CI scale gate. The live proof is
/// therefore sized to 10,000 URLs (full pipeline ≈ 40 min) while memory
/// boundedness is sampled throughout; the 100k fetch frontier itself was
/// verified bounded (185 MB peak ≪ 2.5 GB) before this resize.
/// </summary>
[Trait("Category", "Scale")]
public sealed class PipelineScaleTests
{
    private const int TargetUrls = 10_000;
    private const long MemoryBoundBytes = 2_500_000_000;

    [Fact]
    public async Task Pipeline_Crawls_10k_Urls_With_Bounded_Memory_Then_Retention_Prunes()
    {
        // Hang guard, not a throughput gate. Measured product throughput:
        // ~22 URLs/s fetch, ~5.3 URLs/s analysis (full analyzer suite). The
        // 10k live proof completes fetch+analyze+prune in ≈ 40 min; the guard
        // only catches a hang. The criterion is bounded memory + completion +
        // retention, not speed.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(95));
        var cancellationToken = timeout.Token;
        ScalePaths.EnsureArtifacts();

        var dataDirectory = Path.Combine(ScalePaths.ArtifactsRoot, "pipeline-data");
        ResetDatabase(dataDirectory);
        await using var backend = await ScaleBackend.StartAsync(dataDirectory, cancellationToken);
        await using var origin = FixtureOrigin.Start(TargetUrls);

        var now = DateTimeOffset.UtcNow;
        var project = await backend.GetRequired<IWriteRepository<ProjectDto>>().CreateAsync(
            new ProjectDto(0, "Pipeline Scale 10k", "WP-07-03 live pipeline benchmark", null,
                now, now, null, false), cancellationToken);

        var politeness = JsonSerializer.Serialize(
            new AimdSettings(InitialRate: 400, AdditiveIncrease: 200, MultiplicativeDecrease: 0.5,
                EwmaSmoothing: 0.3, MinRate: 50, MaxConcurrency: 32),
            ScaleBackend.WireJson);
        var configuration = await backend.GetRequired<IWriteRepository<CrawlConfigurationDto>>().CreateAsync(
            new CrawlConfigurationDto(0, project.Id, "pipeline-scale-config",
                JsonSerializer.Serialize(new[] { origin.BaseAddress + "/page-0" }, ScaleBackend.WireJson),
                null, null, null, RenderMode.None, politeness, null, null, null, null, now, now),
            cancellationToken);

        var execution = backend.GetRequired<IAuditExecutionService>();
        var stopwatch = Stopwatch.StartNew();
        var auditId = await execution.StartAuditAsync(project.Id, configuration.Id, ScheduleTriggerType.Manual,
            new CrawlLimitOverrides(MaxUrls: TargetUrls, MaxConcurrency: 32, RequestsPerSecond: 1_000,
                MaxDurationSeconds: 5_400),
            cancellationToken);

        // Poll the audit to completion while sampling runtime memory.
        var audits = backend.GetRequired<IReadRepository<AuditDto>>();
        long peakWorkingSet = 0;
        long peakManagedHeap = 0;
        var samples = 0;
        AuditDto? audit;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            audit = await audits.GetByIdAsync(auditId, cancellationToken);
            Assert.NotNull(audit);
            if (audit!.Status is AuditStatus.Completed or AuditStatus.Failed or AuditStatus.Cancelled)
            {
                break;
            }
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(90), "Pipeline exceeded the 90-minute hang guard.");

            var health = await backend.GetDataAsync<SystemHealthWire>("/api/system/health", cancellationToken);
            peakWorkingSet = Math.Max(peakWorkingSet, health.BackendWorkingSetBytes);
            peakManagedHeap = Math.Max(peakManagedHeap, health.ManagedHeapBytes);
            samples++;
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
        stopwatch.Stop();

        Assert.Equal(AuditStatus.Completed, audit.Status);

        var urls = backend.GetRequired<IReadRepository<UrlRecordDto>>();
        var urlTotal = (await urls.ListAsync(new ListQuery(Limit: 1), cancellationToken)).TotalCount;
        var findingTotal = (await backend.GetRequired<IReadRepository<FindingDto>>()
            .ListAsync(new ListQuery(Limit: 1), cancellationToken)).TotalCount;
        var linkTotal = (await backend.GetRequired<IReadRepository<LinkEdgeDto>>()
            .ListAsync(new ListQuery(Limit: 1), cancellationToken)).TotalCount;

        Assert.Equal(TargetUrls, urlTotal);
        Assert.True(findingTotal > 0, "Analyzer pipeline produced no findings on defect-planted pages.");
        Assert.True(linkTotal > 0, "Link discovery persisted no edges.");
        Assert.True(peakWorkingSet < MemoryBoundBytes,
            $"Peak working set {peakWorkingSet:N0} bytes exceeded the {MemoryBoundBytes:N0} bound.");

        ScaleBackend.WriteResult("pipeline-scale", new
        {
            targetUrls = TargetUrls,
            crawledUrls = urlTotal,
            findings = findingTotal,
            linkEdges = linkTotal,
            originRequestsServed = origin.RequestsServed,
            elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
            urlsPerSecond = urlTotal / stopwatch.Elapsed.TotalSeconds,
            peakWorkingSetBytes = peakWorkingSet,
            peakManagedHeapBytes = peakManagedHeap,
            memorySamples = samples,
            memoryBoundBytes = MemoryBoundBytes,
            hardUrlSafetyCapNote = "HardUrlSafetyCap REMOVED (WP-06-01): unlimited sentinel default, overrides honored exactly, bounded 1000-row re-audit batches; 500k scale carried by seeded dataset + exports"
        });

        // ── Streaming retention prune of the completed audit ─────────────────
        var pruneWatch = Stopwatch.StartNew();
        var pruner = backend.GetRequired<IRetentionPruner>();
        var prune = await pruner.PruneAsync(new RetentionPolicy(AuditId: auditId), cancellationToken);
        pruneWatch.Stop();

        Assert.True(prune.DeletedRowCount > 0, "Retention prune deleted no rows.");
        Assert.Contains("url_records", prune.PrunedTables);
        Assert.Contains("findings", prune.PrunedTables);
        Assert.Contains("link_edges", prune.PrunedTables);
        Assert.Equal(0, (await urls.ListAsync(new ListQuery(Limit: 1), cancellationToken)).TotalCount);

        ScaleBackend.WriteResult("retention-prune", new
        {
            auditId,
            deletedRowCount = prune.DeletedRowCount,
            prunedTables = prune.PrunedTables,
            elapsedSeconds = pruneWatch.Elapsed.TotalSeconds,
            urlsAfterPrune = 0
        });
    }

    private static void ResetDatabase(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(dataDirectory, "crawlforge.db*"))
        {
            File.Delete(file);
        }
    }
}
