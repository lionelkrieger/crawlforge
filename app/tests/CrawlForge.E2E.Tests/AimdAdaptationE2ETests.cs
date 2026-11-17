using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Politeness;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// AIMD rate adaptation against a simulated origin (WP-07-01, criterion #36):
/// deterministic control-law proofs over the accepted controller with a manual
/// clock and scripted response observations — additive increase to the
/// resolved ceiling, multiplicative decrease on 429/503/TTFB spikes,
/// Retry-After pauses, manual pinning, disabled-static behavior, and
/// checkpoint state preservation (pause/resume).
/// </summary>
public sealed class AimdAdaptationE2ETests
{
    private const string Host = "origin.test";

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class CollectingSink : IHostThrottleAdjustmentSink
    {
        public List<HostThrottleAdjustment> Adjustments { get; } = [];

        public void Emit(HostThrottleAdjustment adjustment) => Adjustments.Add(adjustment);
    }

    private static HostResponseSample Healthy(ManualClock clock, double ttfbMs = 50) =>
        new(ttfbMs, 200, false, clock.GetUtcNow());

    private static AimdControllerOptions Options(double ceiling = 3.0) => new()
    {
        InitialRate = 1.0,
        AdditiveIncrease = 0.5,
        MultiplicativeDecrease = 0.5,
        HealthyStreakTarget = 5,
        ResolvedThroughputCeiling = ceiling,
        WorkerConcurrencyCeiling = 8
    };

    [Fact]
    public void HealthyStreaks_IncreaseAdditively_UpToResolvedCeiling()
    {
        var clock = new ManualClock();
        var sink = new CollectingSink();
        var controller = new AimdPolitenessController(Options(ceiling: 3.0), auditId: 1, sink, timeProvider: clock);

        Assert.Equal(1.0, controller.GetState(Host).CurrentRate);

        // Four rounds of K=5 healthy fast responses: 1 → 1.5 → 2 → 2.5 → 3.
        for (var round = 0; round < 4; round++)
        {
            for (var i = 0; i < 5; i++)
            {
                controller.RecordResponse(Host, Healthy(clock));
                clock.Advance(TimeSpan.FromMilliseconds(200));
            }
        }

        Assert.Equal(3.0, controller.GetState(Host).CurrentRate, 3);

        // Further healthy rounds never exceed the resolved ceiling.
        for (var i = 0; i < 10; i++)
        {
            controller.RecordResponse(Host, Healthy(clock));
            clock.Advance(TimeSpan.FromMilliseconds(200));
        }

        Assert.Equal(3.0, controller.GetState(Host).CurrentRate, 3);
        Assert.All(sink.Adjustments.Where(a => a.Trigger == HostThrottleTriggerCodes.AdditiveIncrease),
            a => Assert.True(a.NewRate <= 3.0));
        Assert.Equal(4, sink.Adjustments.Count(a => a.Trigger == HostThrottleTriggerCodes.AdditiveIncrease));
    }

    [Fact]
    public void Http429_HalvesRate_AndFreezesAdaptationDuringCooldown()
    {
        var clock = new ManualClock();
        var sink = new CollectingSink();
        var controller = new AimdPolitenessController(Options(), auditId: 1, sink, timeProvider: clock);

        for (var i = 0; i < 5; i++)
        {
            controller.RecordResponse(Host, Healthy(clock));
        }

        Assert.Equal(1.5, controller.GetState(Host).CurrentRate, 3);

        controller.RecordResponse(Host, new HostResponseSample(20, 429, false, clock.GetUtcNow()));
        Assert.Equal(0.75, controller.GetState(Host).CurrentRate, 3);
        Assert.Contains(sink.Adjustments, a => a.Trigger == HostThrottleTriggerCodes.Http429
            && Math.Abs(a.PreviousRate - 1.5) < 1e-9 && Math.Abs(a.NewRate - 0.75) < 1e-9);

        // Cooldown (10s default): healthy responses do not increase the rate.
        for (var i = 0; i < 10; i++)
        {
            controller.RecordResponse(Host, Healthy(clock));
        }

        Assert.Equal(0.75, controller.GetState(Host).CurrentRate, 3);

        // After cooldown expiry, adaptation resumes.
        clock.Advance(TimeSpan.FromSeconds(11));
        for (var i = 0; i < 6; i++)
        {
            controller.RecordResponse(Host, Healthy(clock));
        }

        Assert.True(controller.GetState(Host).CurrentRate > 0.75);
        Assert.Contains(sink.Adjustments, a => a.Trigger == HostThrottleTriggerCodes.CooldownExpiry);
    }

    [Fact]
    public void Http503_HalvesRate()
    {
        var clock = new ManualClock();
        var sink = new CollectingSink();
        var controller = new AimdPolitenessController(Options(), auditId: 1, sink, timeProvider: clock);

        controller.RecordResponse(Host, new HostResponseSample(20, 503, false, clock.GetUtcNow()));
        Assert.Equal(0.5, controller.GetState(Host).CurrentRate, 3);
        Assert.Contains(sink.Adjustments, a => a.Trigger == HostThrottleTriggerCodes.Http503);
    }

    [Fact]
    public void TtfbSpike_OverBaseline_HalvesRate()
    {
        var clock = new ManualClock();
        var sink = new CollectingSink();
        var controller = new AimdPolitenessController(Options(), auditId: 1, sink, timeProvider: clock);

        // Warm-up: 12 healthy 50ms samples establish the EWMA baseline
        // (and produce two additive increases along the way).
        for (var i = 0; i < 12; i++)
        {
            controller.RecordResponse(Host, Healthy(clock, ttfbMs: 50));
            clock.Advance(TimeSpan.FromMilliseconds(200));
        }

        var rateBeforeSpike = controller.GetState(Host).CurrentRate;

        // 500ms > kappa(2.5) × ~50ms baseline → latency-spike decrease.
        controller.RecordResponse(Host, Healthy(clock, ttfbMs: 500));

        var state = controller.GetState(Host);
        Assert.True(state.CurrentRate < rateBeforeSpike);
        Assert.Equal(rateBeforeSpike * 0.5, state.CurrentRate, 3);
        Assert.Contains(sink.Adjustments, a => a.Trigger == HostThrottleTriggerCodes.LatencySpike);
    }

    [Fact]
    public void RetryAfter_PausesHost_AndIsRecorded()
    {
        var clock = new ManualClock();
        var sink = new CollectingSink();
        var controller = new AimdPolitenessController(Options(), auditId: 1, sink, timeProvider: clock);

        controller.RecordResponse(Host, new AimdResponseObservation(
            new HostResponseSample(20, 429, false, clock.GetUtcNow()),
            RetryAfterSeconds: 30));

        Assert.Contains(sink.Adjustments, a => a.Trigger == HostThrottleTriggerCodes.RetryAfter);
        // The rate decrease still applies on top of the pause.
        Assert.Equal(0.5, controller.GetState(Host).CurrentRate, 3);
    }

    [Fact]
    public void ManualPin_SuspendsAdaptation_UntilReleased()
    {
        var clock = new ManualClock();
        var sink = new CollectingSink();
        var controller = new AimdPolitenessController(Options(), auditId: 1, sink, timeProvider: clock);

        controller.SetManualOverride(Host, 2.0);
        Assert.Equal(2.0, controller.GetState(Host).ManualOverrideRate);

        // Neither healthy streaks nor 429s move a pinned host.
        for (var i = 0; i < 10; i++)
        {
            controller.RecordResponse(Host, Healthy(clock));
        }

        controller.RecordResponse(Host, new HostResponseSample(20, 429, false, clock.GetUtcNow()));
        Assert.Equal(2.0, controller.GetState(Host).CurrentRate, 3);

        // Release resumes automatic control.
        controller.SetManualOverride(Host, null);
        Assert.Null(controller.GetState(Host).ManualOverrideRate);
        Assert.Contains(sink.Adjustments, a => a.Trigger == HostThrottleTriggerCodes.ManualOverride);
    }

    [Fact]
    public void DisabledController_ReproducesStaticPoliteness()
    {
        var clock = new ManualClock();
        var sink = new CollectingSink();
        var options = Options(ceiling: 4.0) with { Enabled = false };
        var controller = new AimdPolitenessController(options, auditId: 1, sink, timeProvider: clock);

        // Static rate = resolved ceiling; observations never adapt it.
        Assert.Equal(4.0, controller.GetState(Host).CurrentRate, 3);
        for (var i = 0; i < 20; i++)
        {
            controller.RecordResponse(Host, Healthy(clock));
        }

        controller.RecordResponse(Host, new HostResponseSample(20, 429, false, clock.GetUtcNow()));
        Assert.Equal(4.0, controller.GetState(Host).CurrentRate, 3);
        Assert.Empty(sink.Adjustments);
    }

    [Fact]
    public void CheckpointState_SurvivesPauseResume()
    {
        var clock = new ManualClock();
        var controller = new AimdPolitenessController(Options(), auditId: 1, timeProvider: clock);

        for (var i = 0; i < 5; i++)
        {
            controller.RecordResponse(Host, Healthy(clock));
        }

        controller.RecordResponse(Host, new HostResponseSample(20, 429, false, clock.GetUtcNow()));
        var before = controller.GetState(Host);

        // Pause → checkpoint → new controller instance (restart) → restore.
        var checkpoint = controller.CreateCheckpointState();
        var resumed = new AimdPolitenessController(Options(), auditId: 1, timeProvider: clock);
        resumed.RestoreCheckpointState(checkpoint);

        var after = resumed.GetState(Host);
        Assert.Equal(before.CurrentRate, after.CurrentRate, 6);
        Assert.Equal(before.EwmaTtfbMilliseconds, after.EwmaTtfbMilliseconds, 6);
        Assert.Equal(before.AdjustmentCount, after.AdjustmentCount);
    }

    [Fact]
    public void PerHostState_IsIndependent()
    {
        var clock = new ManualClock();
        var controller = new AimdPolitenessController(Options(), auditId: 1, timeProvider: clock);

        for (var i = 0; i < 5; i++)
        {
            controller.RecordResponse("a.test", Healthy(clock));
        }

        controller.RecordResponse("b.test", new HostResponseSample(20, 429, false, clock.GetUtcNow()));

        Assert.Equal(1.5, controller.GetState("a.test").CurrentRate, 3);
        Assert.Equal(0.5, controller.GetState("b.test").CurrentRate, 3);
    }
}
