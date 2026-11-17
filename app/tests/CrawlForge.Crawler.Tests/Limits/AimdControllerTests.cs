using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Politeness;
using Xunit;

namespace CrawlForge.Crawler.Tests.Limits;

/// <summary>
/// FS-CRAWL-002 §3.4 control law: additive increase under the resolved
/// ceilings and crawl-delay floor, multiplicative decrease on every trigger
/// class, cooldown and baseline freezing, retry-after pauses, manual pins,
/// concurrency coupling, spacing, and determinism.
/// </summary>
public sealed class AimdControllerTests
{
    private const string Host = "example.com";

    [Fact]
    public void AdditiveIncreaseAfterKHealthyFastSamples()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink);

        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        Assert.Equal(1.5, controller.GetState(Host).CurrentRate);
        var adjustment = Assert.Single(sink.Adjustments);
        Assert.Equal(HostThrottleTriggerCodes.AdditiveIncrease, adjustment.Trigger);
        Assert.Equal(1.0, adjustment.PreviousRate);
        Assert.Equal(1.5, adjustment.NewRate);
    }

    [Fact]
    public void IncreaseIsClampedAtResolvedThroughputCeiling()
    {
        var sink = new RecordingAdjustmentSink();
        var options = AimdTestHelpers.FastOptions() with { ResolvedThroughputCeiling = 1.2 };
        var controller = AimdTestHelpers.CreateController(options, sink);

        for (var i = 0; i < 6; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        Assert.Equal(1.2, controller.GetState(Host).CurrentRate);
        Assert.Single(sink.Adjustments);
    }

    [Fact]
    public void IncreaseIsClampedAtMaxAutoRateWhenCeilingUnlimited()
    {
        var sink = new RecordingAdjustmentSink();
        var options = AimdTestHelpers.FastOptions() with { MaxAutoRate = 2.0, HealthyStreakTarget = 1 };
        var controller = AimdTestHelpers.CreateController(options, sink);

        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        Assert.Equal(2.0, controller.GetState(Host).CurrentRate);
        Assert.Equal(2, sink.Adjustments.Count);
    }

    [Fact]
    public void CrawlDelayCapsInitialAndAdaptiveRate()
    {
        var sink = new RecordingAdjustmentSink();
        var delay = new FakeCrawlDelaySource();
        delay.Delays[Host] = 2.0;
        var options = AimdTestHelpers.FastOptions() with { HealthyStreakTarget = 1 };
        var controller = AimdTestHelpers.CreateController(options, sink, delay);

        Assert.Equal(0.5, controller.GetState(Host).CurrentRate);
        for (var i = 0; i < 4; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        Assert.Equal(0.5, controller.GetState(Host).CurrentRate);
        Assert.Empty(sink.Adjustments);
    }

    [Fact]
    public void DecreaseOnHttp429HalvesRate()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink);
        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        controller.RecordResponse(Host, AimdTestHelpers.Status(429));

        Assert.Equal(0.75, controller.GetState(Host).CurrentRate);
        Assert.Equal(HostThrottleTriggerCodes.Http429, sink.Adjustments[^1].Trigger);
        Assert.Equal(1.5, sink.Adjustments[^1].PreviousRate);
        Assert.Equal(0.75, sink.Adjustments[^1].NewRate);
    }

    [Fact]
    public void DecreaseOnHttp503HalvesRate()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink);

        controller.RecordResponse(Host, AimdTestHelpers.Status(503));

        Assert.Equal(0.5, controller.GetState(Host).CurrentRate);
        Assert.Equal(HostThrottleTriggerCodes.Http503, Assert.Single(sink.Adjustments).Trigger);
    }

    [Fact]
    public void DecreaseOnWafSoftBlock()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink);

        controller.RecordResponse(Host, AimdTestHelpers.Challenge());

        Assert.Equal(0.5, controller.GetState(Host).CurrentRate);
        Assert.Equal(HostThrottleTriggerCodes.WafSoftBlock, Assert.Single(sink.Adjustments).Trigger);
    }

    [Fact]
    public void DecreaseOnLatencySpikeOverKappaBaseline()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink);
        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy(100));
        }

        controller.RecordResponse(Host, AimdTestHelpers.Healthy(260));

        Assert.Equal(0.75, controller.GetState(Host).CurrentRate);
        var spike = sink.Adjustments[^1];
        Assert.Equal(HostThrottleTriggerCodes.LatencySpike, spike.Trigger);
        Assert.Equal(260, spike.TtfbSampleMilliseconds);
        Assert.Equal(100, spike.RollingBaselineMilliseconds);
    }

    [Fact]
    public void NoSpikeDetectionBeforeWarmupCompletes()
    {
        var sink = new RecordingAdjustmentSink();
        var options = AimdTestHelpers.FastOptions() with { WarmupSamples = 10, HealthyStreakTarget = 100 };
        var controller = AimdTestHelpers.CreateController(options, sink);

        controller.RecordResponse(Host, AimdTestHelpers.Healthy(100));
        controller.RecordResponse(Host, AimdTestHelpers.Healthy(100));
        controller.RecordResponse(Host, AimdTestHelpers.Healthy(1000));

        Assert.Empty(sink.Adjustments);
        Assert.Equal(1.0, controller.GetState(Host).CurrentRate);
    }

    [Fact]
    public void DecreaseRespectsRateFloor()
    {
        var options = AimdTestHelpers.FastOptions() with { InitialRate = 0.5, MinRate = 0.4, HealthyStreakTarget = 100 };
        var controller = AimdTestHelpers.CreateController(options);

        controller.RecordResponse(Host, AimdTestHelpers.Status(429));

        Assert.Equal(0.4, controller.GetState(Host).CurrentRate);
    }

    [Fact]
    public void CooldownBlocksIncreasesUntilExpiry()
    {
        var sink = new RecordingAdjustmentSink();
        var time = new ManualTimeProvider();
        var controller = AimdTestHelpers.CreateController(sink: sink, time: time);
        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        controller.RecordResponse(Host, AimdTestHelpers.Status(429));
        time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        Assert.Equal(0.75, controller.GetState(Host).CurrentRate);

        time.Advance(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        Assert.Equal(1.25, controller.GetState(Host).CurrentRate);
        var triggers = sink.Adjustments.Select(a => a.Trigger).ToList();
        var expected = new[]
        {
            HostThrottleTriggerCodes.AdditiveIncrease,
            HostThrottleTriggerCodes.Http429,
            HostThrottleTriggerCodes.CooldownExpiry,
            HostThrottleTriggerCodes.AdditiveIncrease
        };
        Assert.Equal(expected, triggers);
    }

    [Fact]
    public void BaselineIsFrozenDuringCooldownAndNeverUpdatedByTrigger()
    {
        var time = new ManualTimeProvider();
        var controller = AimdTestHelpers.CreateController(time: time);
        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy(100));
        }

        controller.RecordResponse(Host, AimdTestHelpers.Status(429, ttfbMs: 5000));
        Assert.Equal(100, controller.GetState(Host).EwmaTtfbMilliseconds);

        time.Advance(TimeSpan.FromSeconds(1));
        controller.RecordResponse(Host, AimdTestHelpers.Healthy(900));
        Assert.Equal(100, controller.GetState(Host).EwmaTtfbMilliseconds);
    }

    [Fact]
    public void Other5xxDoesNotDecreaseByDefault()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink);

        controller.RecordResponse(Host, AimdTestHelpers.Status(500));

        Assert.Empty(sink.Adjustments);
        Assert.Equal(1.0, controller.GetState(Host).CurrentRate);
    }

    [Fact]
    public void Other5xxDecreasesWhenToggleEnabled()
    {
        var sink = new RecordingAdjustmentSink();
        var options = AimdTestHelpers.FastOptions() with { DecreaseOnOther5xx = true };
        var controller = AimdTestHelpers.CreateController(options, sink);

        controller.RecordResponse(Host, AimdTestHelpers.Status(502));

        Assert.Equal(0.5, controller.GetState(Host).CurrentRate);
        Assert.Equal(HostThrottleTriggerCodes.Http5xx, Assert.Single(sink.Adjustments).Trigger);
    }

    [Fact]
    public void OtherErrorsResetStreakWithoutBaselineUpdate()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink);

        controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        controller.RecordResponse(Host, AimdTestHelpers.Status(404));
        controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        Assert.Empty(sink.Adjustments);

        controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        Assert.Single(sink.Adjustments);
    }

    [Fact]
    public void RetryAfterPausesHostForMaxOfHeaderAndCooldown()
    {
        var time = new ManualTimeProvider();
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink, time: time);
        var observation = new AimdResponseObservation(AimdTestHelpers.Status(429), RetryAfterSeconds: 30);

        controller.RecordResponse(Host, observation);

        var checkpoint = controller.CreateFullCheckpoint();
        var host = Assert.Single(checkpoint.Hosts);
        Assert.Equal(AimdTestHelpers.Epoch + TimeSpan.FromSeconds(30), host.PausedUntil);
        Assert.Contains(sink.Adjustments, a => a.Trigger == HostThrottleTriggerCodes.RetryAfter);
    }

    [Fact]
    public void RetryAfterShorterThanCooldownUsesCooldown()
    {
        var time = new ManualTimeProvider();
        var controller = AimdTestHelpers.CreateController(time: time);
        var observation = new AimdResponseObservation(AimdTestHelpers.Status(503), RetryAfterSeconds: 3);

        controller.RecordResponse(Host, observation);

        var host = Assert.Single(controller.CreateFullCheckpoint().Hosts);
        Assert.Equal(AimdTestHelpers.Epoch + TimeSpan.FromSeconds(10), host.PausedUntil);
    }

    [Fact]
    public void ManualPinSuspendsAdaptationUntilCleared()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink);

        controller.SetManualOverride(Host, 2.0);
        Assert.Equal(2.0, controller.GetState(Host).CurrentRate);
        Assert.Equal(2.0, controller.GetState(Host).ManualOverrideRate);

        controller.RecordResponse(Host, AimdTestHelpers.Status(429));
        Assert.Equal(2.0, controller.GetState(Host).CurrentRate);
        Assert.Single(sink.Adjustments);
        Assert.Equal(HostThrottleTriggerCodes.ManualOverride, sink.Adjustments[0].Trigger);

        controller.SetManualOverride(Host, null);
        Assert.Null(controller.GetState(Host).ManualOverrideRate);
        Assert.Equal(1.0, controller.GetState(Host).CurrentRate);

        controller.RecordResponse(Host, AimdTestHelpers.Status(429));
        Assert.Equal(0.5, controller.GetState(Host).CurrentRate);
    }

    [Fact]
    public async Task ManualPinStillHonorsCrawlDelay()
    {
        var time = new ManualTimeProvider();
        var delay = new FakeCrawlDelaySource();
        delay.Delays[Host] = 2.0;
        var controller = AimdTestHelpers.CreateController(crawlDelay: delay, time: time);

        controller.SetManualOverride(Host, 10.0);
        await controller.WaitAsync(Host);

        var host = Assert.Single(controller.CreateFullCheckpoint().Hosts);
        Assert.Equal(AimdTestHelpers.Epoch + TimeSpan.FromSeconds(2), host.NextPermittedAt);
    }

    [Fact]
    public async Task WaitAsyncSpacesRequestsAtCurrentRate()
    {
        var time = new ManualTimeProvider();
        var controller = AimdTestHelpers.CreateController(time: time);

        await controller.WaitAsync(Host);
        var first = Assert.Single(controller.CreateFullCheckpoint().Hosts);
        Assert.Equal(AimdTestHelpers.Epoch + TimeSpan.FromSeconds(1), first.NextPermittedAt);

        var second = controller.WaitAsync(Host);
        Assert.False(second.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await second;

        var after = Assert.Single(controller.CreateFullCheckpoint().Hosts);
        Assert.Equal(AimdTestHelpers.Epoch + TimeSpan.FromSeconds(2), after.NextPermittedAt);
    }

    [Fact]
    public void IdenticalSequenceProducesIdenticalAdjustments()
    {
        var firstSink = new RecordingAdjustmentSink();
        var secondSink = new RecordingAdjustmentSink();
        var first = AimdTestHelpers.CreateController(sink: firstSink, time: new ManualTimeProvider());
        var second = AimdTestHelpers.CreateController(sink: secondSink, time: new ManualTimeProvider());

        foreach (var controller in new[] { first, second })
        {
            for (var i = 0; i < 3; i++)
            {
                controller.RecordResponse(Host, AimdTestHelpers.Healthy());
            }

            controller.RecordResponse(Host, AimdTestHelpers.Status(429));
            controller.SetManualOverride(Host, 3.0);
            controller.SetManualOverride(Host, null);
            controller.RecordResponse(Host, AimdTestHelpers.Status(503));
        }

        Assert.Equal(firstSink.Adjustments, secondSink.Adjustments);
        Assert.Equal(first.CreateFullCheckpoint(), second.CreateFullCheckpoint());
    }

    [Fact]
    public void ConcurrencyCouplesToRateAndLatency()
    {
        var options = AimdTestHelpers.FastOptions() with { HealthyStreakTarget = 1, AdditiveIncrease = 1.0 };
        var controller = AimdTestHelpers.CreateController(options);
        var observation = new AimdResponseObservation(AimdTestHelpers.Healthy(), FullResponseLatencyMs: 2000);

        controller.RecordResponse(Host, observation);

        Assert.Equal(2.0, controller.GetState(Host).CurrentRate);
        Assert.Equal(3, controller.GetState(Host).CurrentConcurrency);
    }

    [Fact]
    public void ConcurrencySumNeverExceedsWorkerCeiling()
    {
        var options = AimdTestHelpers.FastOptions() with
        {
            HealthyStreakTarget = 1,
            AdditiveIncrease = 2.0,
            WorkerConcurrencyCeiling = 4
        };
        var controller = AimdTestHelpers.CreateController(options);
        var observation = new AimdResponseObservation(AimdTestHelpers.Healthy(), FullResponseLatencyMs: 5000);

        for (var i = 0; i < 4; i++)
        {
            controller.RecordResponse("a.example", observation);
            controller.RecordResponse("b.example", observation);
            var total = controller.GetState("a.example").CurrentConcurrency + controller.GetState("b.example").CurrentConcurrency;
            Assert.True(total <= 4, $"Concurrency sum {total} exceeded the worker ceiling.");
        }
    }

    [Fact]
    public void ConcurrencyDecreaseHalvesWithFloorOne()
    {
        var options = AimdTestHelpers.FastOptions() with { HealthyStreakTarget = 1, AdditiveIncrease = 1.0 };
        var controller = AimdTestHelpers.CreateController(options);
        controller.RecordResponse(Host, new AimdResponseObservation(AimdTestHelpers.Healthy(), FullResponseLatencyMs: 2000));
        Assert.Equal(3, controller.GetState(Host).CurrentConcurrency);

        controller.RecordResponse(Host, AimdTestHelpers.Status(429));

        Assert.Equal(1, controller.GetState(Host).CurrentConcurrency);
    }

    [Fact]
    public void EventsCarryFullTelemetryPayload()
    {
        var sink = new RecordingAdjustmentSink();
        var time = new ManualTimeProvider();
        var controller = AimdTestHelpers.CreateController(sink: sink, time: time, auditId: 7);
        for (var i = 0; i < 3; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        var adjustment = Assert.Single(sink.Adjustments);
        Assert.Equal(7, adjustment.AuditId);
        Assert.Equal(Host, adjustment.Host);
        Assert.Equal(HostThrottleTriggerCodes.AdditiveIncrease, adjustment.Trigger);
        Assert.Equal(1.0, adjustment.PreviousRate);
        Assert.Equal(1.5, adjustment.NewRate);
        Assert.Equal(1, adjustment.PreviousConcurrency);
        Assert.True(adjustment.NewConcurrency >= 1);
        Assert.Equal(100, adjustment.TtfbSampleMilliseconds);
        Assert.Equal(100, adjustment.RollingBaselineMilliseconds);
        Assert.Equal(AimdTestHelpers.Epoch, adjustment.Timestamp);
    }

    [Fact]
    public void GetStateOfUnknownHostReturnsInitialSnapshotWithoutMutation()
    {
        var controller = AimdTestHelpers.CreateController();

        var state = controller.GetState("unknown.example");

        Assert.Equal(1.0, state.CurrentRate);
        Assert.Equal(1, state.CurrentConcurrency);
        Assert.Equal(0, state.EwmaTtfbMilliseconds);
        Assert.Equal(0, state.AdjustmentCount);
        Assert.Empty(controller.CreateCheckpointState().Hosts);
    }

    [Fact]
    public void FromSettingsMapsLockedDtoAndResolvedCeilings()
    {
        var settings = new AimdSettings(
            InitialRate: 2.0, AdditiveIncrease: 1.0, MultiplicativeDecrease: 0.6,
            EwmaSmoothing: 0.3, MinRate: 0.2, MaxConcurrency: 4);
        var limits = new ResolvedCrawlLimits(null, null, null, MaxConcurrency: 16, RequestsPerSecond: 3.5, FetchTimeoutSeconds: 30);

        var options = AimdControllerOptions.FromSettings(settings, limits);

        Assert.Equal(2.0, options.InitialRate);
        Assert.Equal(1.0, options.AdditiveIncrease);
        Assert.Equal(0.6, options.MultiplicativeDecrease);
        Assert.Equal(0.3, options.EwmaLambda);
        Assert.Equal(0.2, options.MinRate);
        Assert.Equal(4, options.PerHostConcurrencyCap);
        Assert.Equal(3.5, options.ResolvedThroughputCeiling);
        Assert.Equal(16, options.WorkerConcurrencyCeiling);
        options.Validate();
    }

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (AimdTestHelpers.FastOptions() with { MultiplicativeDecrease = 1.0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (AimdTestHelpers.FastOptions() with { EwmaLambda = 0.0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (AimdTestHelpers.FastOptions() with { HealthyStreakTarget = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (AimdTestHelpers.FastOptions() with { InitialRate = -1.0 }).Validate());
    }
}
