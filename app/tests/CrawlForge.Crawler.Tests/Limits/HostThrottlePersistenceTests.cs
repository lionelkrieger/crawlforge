using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Politeness;
using Xunit;

namespace CrawlForge.Crawler.Tests.Limits;

/// <summary>
/// FS-CRAWL-002 §3.6/§3.7 persistence and emission: bounded host_throttle_events
/// history on the real SQLite stack, the completion metric summary under the
/// additive host-throttle key, and per-host coalescing of the typed emission.
/// </summary>
public sealed class HostThrottlePersistenceTests : IClassFixture<SqliteStackFixture>
{
    private const string Host = "example.com";
    private readonly SqliteStackFixture _stack;

    public HostThrottlePersistenceTests(SqliteStackFixture stack)
    {
        _stack = stack;
    }

    private RepositoryHostThrottleEventStore CreateStore() => new(_stack.ThrottleEvents, _stack.ThrottleEvents);

    [Fact]
    public async Task RecorderPersistsAdjustmentsWithTriggerCodes()
    {
        var auditId = await _stack.CreateAuditAsync();
        var store = CreateStore();
        var recorder = new HostThrottleEventRecorder(store);
        var time = new ManualTimeProvider();
        var controller = AimdTestHelpers.CreateController(sink: recorder, time: time, auditId: auditId);

        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        time.Advance(TimeSpan.FromSeconds(1));
        controller.RecordResponse(Host, AimdTestHelpers.Status(429));
        await recorder.FlushAsync();

        var history = await store.GetHistoryAsync(auditId, Host, limit: 100);
        Assert.Equal(2, history.Count);
        Assert.Equal(HostThrottleTriggerCodes.Http429, history[0].Reason);
        Assert.Equal(HostThrottleTriggerCodes.AdditiveIncrease, history[1].Reason);
        Assert.Equal(1.0, history[1].PreviousRate);
        Assert.Equal(1.5, history[1].NewRate);
        Assert.Equal(1.5, history[0].PreviousRate);
        Assert.Equal(0.75, history[0].NewRate);
        Assert.All(history, row => Assert.Equal(auditId, row.AuditId));
    }

    [Fact]
    public async Task BoundedHistoryKeepsOnlyTheNewestRows()
    {
        var auditId = await _stack.CreateAuditAsync();
        var store = CreateStore();
        var recorder = new HostThrottleEventRecorder(store, keepLastPerHost: 5);

        for (var i = 0; i < 12; i++)
        {
            recorder.Emit(new HostThrottleAdjustment(
                auditId, Host, HostThrottleTriggerCodes.AdditiveIncrease,
                PreviousRate: 1.0 + i, NewRate: 1.5 + i,
                PreviousConcurrency: 1, NewConcurrency: 1,
                TtfbSampleMilliseconds: 100, RollingBaselineMilliseconds: 100,
                Timestamp: AimdTestHelpers.Epoch + TimeSpan.FromSeconds(i)));
        }

        await recorder.FlushAsync();

        var history = await store.GetHistoryAsync(auditId, Host, limit: 100);
        Assert.Equal(5, history.Count);
        Assert.Equal(AimdTestHelpers.Epoch + TimeSpan.FromSeconds(11), history[0].Timestamp);
        Assert.Equal(AimdTestHelpers.Epoch + TimeSpan.FromSeconds(7), history[4].Timestamp);
    }

    [Fact]
    public async Task HistoryIsScopedPerAuditAndHost()
    {
        var firstAudit = await _stack.CreateAuditAsync();
        var secondAudit = await _stack.CreateAuditAsync();
        var store = CreateStore();
        var recorder = new HostThrottleEventRecorder(store);

        recorder.Emit(new HostThrottleAdjustment(firstAudit, "a.example", HostThrottleTriggerCodes.Http429, 1, 0.5, 1, 1, 100, 100, AimdTestHelpers.Epoch));
        recorder.Emit(new HostThrottleAdjustment(firstAudit, "b.example", HostThrottleTriggerCodes.Http503, 1, 0.5, 1, 1, 100, 100, AimdTestHelpers.Epoch));
        recorder.Emit(new HostThrottleAdjustment(secondAudit, "a.example", HostThrottleTriggerCodes.ManualOverride, 1, 2, 1, 1, null, 100, AimdTestHelpers.Epoch));
        await recorder.FlushAsync();

        var firstA = await store.GetHistoryAsync(firstAudit, "a.example", 100);
        var firstB = await store.GetHistoryAsync(firstAudit, "b.example", 100);
        var secondA = await store.GetHistoryAsync(secondAudit, "a.example", 100);

        Assert.Single(firstA);
        Assert.Single(firstB);
        Assert.Single(secondA);
        Assert.Equal(HostThrottleTriggerCodes.Http429, firstA[0].Reason);
        Assert.Equal(HostThrottleTriggerCodes.Http503, firstB[0].Reason);
        Assert.Equal(HostThrottleTriggerCodes.ManualOverride, secondA[0].Reason);
    }

    [Fact]
    public async Task CompletionSummaryIsWrittenUnderTheHostThrottleMetricKey()
    {
        // The summarizer targets the locked IWriteRepository<MetricSummaryDto>
        // contract. Real-stack metric_summaries integration is blocked by the
        // phase-01 column-name defect (metric_value vs mapped value), so this
        // test exercises the contract against a faithful in-memory repository.
        var controller = AimdTestHelpers.CreateController(auditId: 99);
        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        controller.RecordResponse(Host, AimdTestHelpers.Status(429));
        controller.SetManualOverride("other.example", 2.0);

        var repository = new InMemoryMetricRepository();
        var summarizer = new AimdMetricSummarizer();
        var summary = await summarizer.WriteCompletionSummaryAsync(controller, auditId: 99, repository);

        Assert.Equal(MetricKeys.HostThrottle, summary.MetricKey);
        Assert.Equal(3, summary.Value);
        Assert.Contains("\"hostCount\":2", summary.DimensionsJson, StringComparison.Ordinal);

        var read = Assert.Single(repository.Rows);
        Assert.Equal(summary.Id, read.Id);
        Assert.Equal(summary.Value, read.Value);
        Assert.Equal(MetricKeys.HostThrottle, read.MetricKey);
        Assert.Equal(99, read.AuditId);
    }

    private sealed class InMemoryMetricRepository : IWriteRepository<MetricSummaryDto>
    {
        private long _nextId = 1;

        public List<MetricSummaryDto> Rows { get; } = [];

        public Task<MetricSummaryDto> CreateAsync(MetricSummaryDto entity, CancellationToken cancellationToken = default)
        {
            var row = entity with { Id = _nextId++ };
            Rows.Add(row);
            return Task.FromResult(row);
        }

        public Task<MetricSummaryDto> UpdateAsync(MetricSummaryDto entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void CoalescingSinkForwardsFirstImmediatelyAndLatestAfterWindow()
    {
        var time = new ManualTimeProvider();
        var inner = new RecordingAdjustmentSink();
        var coalescing = new CoalescingHostThrottleSink(inner, time, TimeSpan.FromSeconds(1));

        var first = Adjustment("a.example", 1.0);
        var second = Adjustment("a.example", 2.0);
        var otherHost = Adjustment("b.example", 1.0);

        coalescing.Emit(first);
        time.Advance(TimeSpan.FromMilliseconds(500));
        coalescing.Emit(second);
        coalescing.FlushPending();
        Assert.Single(inner.Adjustments);

        time.Advance(TimeSpan.FromSeconds(1));
        coalescing.FlushPending();
        coalescing.Emit(otherHost);

        Assert.Equal(3, inner.Adjustments.Count);
        Assert.Equal(first, inner.Adjustments[0]);
        Assert.Equal(second, inner.Adjustments[1]);
        Assert.Equal(otherHost, inner.Adjustments[2]);
    }

    private static HostThrottleAdjustment Adjustment(string host, double newRate) => new(
        AuditId: 1, Host: host, Trigger: HostThrottleTriggerCodes.AdditiveIncrease,
        PreviousRate: 1.0, NewRate: newRate,
        PreviousConcurrency: 1, NewConcurrency: 1,
        TtfbSampleMilliseconds: 100, RollingBaselineMilliseconds: 100,
        Timestamp: AimdTestHelpers.Epoch);
}
