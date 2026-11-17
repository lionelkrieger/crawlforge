using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Scheduling;
using CrawlForge.E2E.Tests.Fixtures;
using CrawlForge.Mcp;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// Full audit lifecycle over real loopback origins (WP-07-01): acceptance
/// criteria #4 (unlimited defaults), #6 (pause/resume/cancel/crash-recovery
/// checkpointing), #11 (scheduled runs + restart-stable next-due), #12
/// (catch-up exactly once).
/// </summary>
public sealed class AuditLifecycleE2ETests
{
    private static LoopbackOrigin ManyPages(int pageCount, int delayMs = 0)
    {
        var originTask = LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/robots.txt", OriginResponse.Text("User-agent: *\nAllow: /\n"));
            var links = FixturePages.Links(Enumerable.Range(0, pageCount).Select(i => $"/p/{i}").ToArray());
            routes.Map("/", OriginResponse.Html(FixturePages.Page("Index", links)));
            for (var i = 0; i < pageCount; i++)
            {
                var index = i;
                routes.Map($"/p/{index}", OriginResponse.Html(
                    FixturePages.Page($"Page {index}", $"<p>Content block {index} with enough words to analyze.</p>"))
                    .WithDelay(delayMs));
            }
        });
        return originTask.GetAwaiter().GetResult();
    }

    [Fact]
    public async Task FreshAudit_NoOverrides_RunsWithUnlimitedPageCeilings()
    {
        // Criterion #4: no overrides → no small hidden page ceiling. Index +
        // 15 pages on one host must all be fetched (any 5/10-style cap fails).
        await using var origin = ManyPages(15);
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);

        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        var audit = await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(90));

        var urls = await backend.ListByAuditAsync<UrlRecordDto>(auditId);
        Assert.Equal(16, urls.Count);
        Assert.All(urls, u => Assert.Equal(CrawlStatusClass.Success, u.StatusClass));

        using var counters = JsonDocument.Parse(audit.CountersJson);
        Assert.Equal(16, counters.RootElement.GetProperty("fetched").GetInt64());
        Assert.Equal(16, counters.RootElement.GetProperty("analyzed").GetInt64());

        // Overall score row exists → full pipeline ran to report readiness.
        var scores = await backend.ListByAuditAsync<AuditScoreDto>(auditId);
        Assert.NotEmpty(scores);
    }

    [Fact]
    public async Task PauseResume_PreservesCheckpoint_AndCompletes()
    {
        // Criterion #6: pause → checkpoint persisted → resume → complete.
        await using var origin = ManyPages(12, delayMs: 120);
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(
            origin.BaseUrl, limits: new CrawlLimitOverrides(RequestsPerSecond: 8));
        var lifecycle = backend.Service<IAuditLifecyclePort>();

        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForAuditAsync(auditId,
            a => a.Status == AuditStatus.Running && Counters(a).fetched >= 2,
            TimeSpan.FromSeconds(30));

        var paused = await lifecycle.PauseAsync(auditId);
        Assert.Equal(AuditStatus.Paused, paused.Status);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Paused, TimeSpan.FromSeconds(15));

        var checkpoints = backend.Service<ICrawlCheckpointService>();
        var checkpoint = await WaitForCheckpointAsync(checkpoints, auditId);
        Assert.NotNull(checkpoint);

        var resumed = await lifecycle.ResumeAsync(auditId);
        Assert.Equal(AuditStatus.Running, resumed.Status);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(90));

        var urls = await backend.ListByAuditAsync<UrlRecordDto>(auditId);
        Assert.Equal(13, urls.Count);
    }

    [Fact]
    public async Task Cancel_MidRun_RetainsPartialData()
    {
        // Criterion #6: cancel preserves the partial dataset.
        await using var origin = ManyPages(12, delayMs: 120);
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(
            origin.BaseUrl, limits: new CrawlLimitOverrides(RequestsPerSecond: 8));
        var lifecycle = backend.Service<IAuditLifecyclePort>();

        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForAuditAsync(auditId,
            a => a.Status == AuditStatus.Running && Counters(a).fetched >= 2,
            TimeSpan.FromSeconds(30));

        var cancelled = await lifecycle.CancelAsync(auditId);
        Assert.Equal(AuditStatus.Cancelled, cancelled.Status);

        var audit = await backend.WaitForAuditAsync(auditId,
            a => a.Status == AuditStatus.Cancelled && a.FinishedAt is not null,
            TimeSpan.FromSeconds(30));
        Assert.NotNull(audit.FinishedAt);

        var urls = await backend.ListByAuditAsync<UrlRecordDto>(auditId);
        Assert.True(urls.Count >= 1, "Partial URL data must survive cancellation.");
        Assert.True(urls.Count < 13, "Cancel must stop the crawl before completion.");
    }

    [Fact]
    public async Task CrashRecovery_RestartResumesFromCheckpoint()
    {
        // Criterion #6: interrupted audits resume from checkpoint across a
        // backend restart; no URL is fetched twice.
        await using var origin = ManyPages(12, delayMs: 120);
        var dataDirectory = Path.Combine(Path.GetTempPath(), "crawlforge-e2e", Guid.NewGuid().ToString("N"));

        long auditId;
        await using (var first = await E2EBackend.StartAsync(dataDirectory: dataDirectory))
        {
            first.PreserveDataDirectory = true;
            var (projectId, configurationId) = await first.SeedProjectAsync(
                origin.BaseUrl, limits: new CrawlLimitOverrides(RequestsPerSecond: 8));
            auditId = await first.StartAuditAsync(projectId, configurationId);
            await first.WaitForAuditAsync(auditId,
                a => a.Status == AuditStatus.Running && Counters(a).fetched >= 2,
                TimeSpan.FromSeconds(30));

            // Pause forces a checkpoint; the drain then keeps the audit paused.
            await first.Service<IAuditLifecyclePort>().PauseAsync(auditId);
            await first.WaitForStatusAsync(auditId, AuditStatus.Paused, TimeSpan.FromSeconds(15));
            await WaitForCheckpointAsync(first.Service<ICrawlCheckpointService>(), auditId);
        }

        // Second launch on the same data directory: orphan recovery + resume.
        await using var second = await E2EBackend.StartAsync(dataDirectory: dataDirectory);
        var audit = await second.Service<IReadRepository<AuditDto>>().GetByIdAsync(auditId);
        Assert.NotNull(audit);
        Assert.Equal(AuditStatus.Paused, audit!.Status);

        var hitsBeforeResume = origin.RequestCount;
        await second.Service<IAuditLifecyclePort>().ResumeAsync(auditId);
        await second.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(90));

        var urls = await second.ListByAuditAsync<UrlRecordDto>(auditId);
        Assert.Equal(13, urls.Count);
        Assert.Equal(13, urls.Select(u => u.CanonicalUrl).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // The resumed crawl only fetched the remaining pages (robots.txt may be
        // re-read once per launch; page fetches must not repeat).
        var pageHitsAfter = origin.Requests.Count(r => r.Path.StartsWith("/p/", StringComparison.Ordinal));
        Assert.True(pageHitsAfter <= 12 + 1, $"Pages re-fetched after resume: {pageHitsAfter}.");
        Assert.True(origin.RequestCount > hitsBeforeResume, "Resume must make progress.");

        try
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task ScheduledRun_FiresUnattended_WithRunHistory_AndStableNextDue()
    {
        // Criterion #11: a due schedule fires without interaction, produces a
        // normal audit + run-history entry, and next-due survives a restart.
        await using var origin = ManyPages(3);
        var dataDirectory = Path.Combine(Path.GetTempPath(), "crawlforge-e2e", Guid.NewGuid().ToString("N"));

        long scheduleId;
        long auditId;
        await using (var first = await E2EBackend.StartAsync(dataDirectory: dataDirectory))
        {
            first.PreserveDataDirectory = true;
            var (projectId, configurationId) = await first.SeedProjectAsync(
                origin.BaseUrl, limits: new CrawlLimitOverrides(RequestsPerSecond: 50));

            var schedules = first.Service<IWriteRepository<ScheduleDefinitionDto>>();
            var schedule = await schedules.CreateAsync(new ScheduleDefinitionDto(
                0, projectId, configurationId, "interval:00:30:00", true, null, null,
                DateTimeOffset.UtcNow.AddSeconds(-1)));
            scheduleId = schedule.Id;

            var scheduler = (ScheduledAuditService)first.Service<IScheduledAuditService>();
            await scheduler.TickAsync();

            var runs = await WaitForRunsAsync(first, scheduleId, 1);
            var run = Assert.Single(runs);
            Assert.Equal(ScheduleTriggerType.Scheduled, run.TriggerType);
            Assert.NotNull(run.AuditId);
            auditId = run.AuditId!.Value;

            await first.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));
            var finished = await first.WaitForAuditAsync(auditId, a => a.FinishedAt is not null);
            Assert.Equal(AuditStatus.Completed, finished.Status);

            // Next-due advanced into the future after firing.
            var updated = await first.Service<IReadRepository<ScheduleDefinitionDto>>().GetByIdAsync(scheduleId);
            Assert.NotNull(updated!.NextDueAt);
            Assert.True(updated.NextDueAt > DateTimeOffset.UtcNow);
        }

        // Restart: next-due state must survive (criterion #11).
        await using var second = await E2EBackend.StartAsync(dataDirectory: dataDirectory);
        var nextDue = await second.Service<IScheduledAuditService>().GetNextDueAsync();
        Assert.NotNull(nextDue);
        Assert.True(nextDue > DateTimeOffset.UtcNow);

        try
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task MissedSchedule_RunsExactlyOnce_AsCatchUp()
    {
        // Criterion #12: a schedule missed while the machine was off fires
        // exactly once at the next opportunity, recorded as catch-up.
        await using var origin = ManyPages(3);
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(
            origin.BaseUrl, limits: new CrawlLimitOverrides(RequestsPerSecond: 50));

        var schedules = backend.Service<IWriteRepository<ScheduleDefinitionDto>>();
        var schedule = await schedules.CreateAsync(new ScheduleDefinitionDto(
            0, projectId, configurationId, "interval:01:00:00", true, null, null,
            DateTimeOffset.UtcNow.AddHours(-2)));

        var scheduler = (ScheduledAuditService)backend.Service<IScheduledAuditService>();
        await scheduler.TickAsync();

        var runs = await WaitForRunsAsync(backend, schedule.Id, 1);
        var run = Assert.Single(runs);
        Assert.Equal(ScheduleTriggerType.CatchUp, run.TriggerType);
        Assert.NotNull(run.AuditId);
        await backend.WaitForStatusAsync(run.AuditId!.Value, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // A second tick must NOT fire again: next-due moved into the future.
        await scheduler.TickAsync();
        await Task.Delay(500);
        var allRuns = await backend.Service<IReadRepository<ScheduleRunDto>>().ListAsync(new ListQuery(
            0, 50, new Dictionary<string, string> { ["scheduleDefinitionId"] = schedule.Id.ToString() }));
        Assert.Equal(1, allRuns.TotalCount);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static (long fetched, long analyzed) Counters(AuditDto audit)
    {
        try
        {
            using var document = JsonDocument.Parse(audit.CountersJson);
            var root = document.RootElement;
            return (
                root.TryGetProperty("fetched", out var f) ? f.GetInt64() : 0,
                root.TryGetProperty("analyzed", out var a) ? a.GetInt64() : 0);
        }
        catch (JsonException)
        {
            return (0, 0);
        }
    }

    private static async Task<CrawlCheckpointDto> WaitForCheckpointAsync(
        ICrawlCheckpointService checkpoints, long auditId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var checkpoint = await checkpoints.LoadLatestAsync(auditId);
            if (checkpoint is not null)
            {
                return checkpoint;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No checkpoint persisted for audit {auditId}.");
    }

    private static async Task<IReadOnlyList<ScheduleRunDto>> WaitForRunsAsync(
        E2EBackend backend, long scheduleId, int expected)
    {
        var reads = backend.Service<IReadRepository<ScheduleRunDto>>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var page = await reads.ListAsync(new ListQuery(
                0, 50, new Dictionary<string, string> { ["scheduleDefinitionId"] = scheduleId.ToString() }));
            if (page.TotalCount >= expected)
            {
                return page.Items;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Schedule {scheduleId} did not produce {expected} run(s).");
    }
}
