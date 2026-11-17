using CrawlForge.Crawler.Politeness;
using Xunit;

namespace CrawlForge.Crawler.Tests.Limits;

/// <summary>
/// Criterion #36 evidence: with the local disable toggle off, the controller
/// reproduces the parent's static politeness exactly — same spacing schedule
/// (resolved throughput ceiling + robots crawl-delay floor) and same static
/// concurrency share — with no adaptation and no adjustment events.
/// </summary>
public sealed class AimdDisableToggleTests
{
    private const string Host = "example.com";

    /// <summary>
    /// Reference model of the parent's static politeness (FS-CRAWL-001 §4.4):
    /// token-bucket spacing at the resolved ceiling, crawl-delay floor, fixed
    /// concurrency share. No adaptation.
    /// </summary>
    private sealed class StaticPolitenessReference
    {
        private readonly double _rate;
        private readonly double? _crawlDelay;
        private DateTimeOffset _next = DateTimeOffset.MinValue;

        public StaticPolitenessReference(double rate, double? crawlDelay)
        {
            _rate = rate;
            _crawlDelay = crawlDelay;
        }

        public DateTimeOffset NextPermittedAt => _next;

        public void Wait(DateTimeOffset now)
        {
            var interval = double.IsPositiveInfinity(_rate) ? TimeSpan.Zero : TimeSpan.FromSeconds(1.0 / _rate);
            if (_crawlDelay is > 0)
            {
                var crawlInterval = TimeSpan.FromSeconds(_crawlDelay.Value);
                if (crawlInterval > interval)
                {
                    interval = crawlInterval;
                }
            }

            var earliest = now > _next ? now : _next;
            _next = earliest + interval;
        }
    }

    private static async Task AssertSpacingMatchesReference(double ceiling, double? crawlDelaySeconds)
    {
        var time = new ManualTimeProvider();
        var delay = new FakeCrawlDelaySource();
        if (crawlDelaySeconds is { } value)
        {
            delay.Delays[Host] = value;
        }

        var options = AimdTestHelpers.FastOptions() with
        {
            Enabled = false,
            ResolvedThroughputCeiling = ceiling
        };
        var controller = AimdTestHelpers.CreateController(options, crawlDelay: delay, time: time);
        var reference = new StaticPolitenessReference(ceiling, crawlDelaySeconds);

        for (var i = 0; i < 8; i++)
        {
            var wait = controller.WaitAsync(Host);
            var host = Assert.Single(controller.CreateFullCheckpoint().Hosts);
            reference.Wait(time.GetUtcNow());
            Assert.Equal(reference.NextPermittedAt, host.NextPermittedAt);
            if (!wait.IsCompleted)
            {
                time.Advance(host.NextPermittedAt - time.GetUtcNow());
            }

            await wait;
            time.Advance(TimeSpan.FromMilliseconds(250));
        }
    }

    [Fact]
    public async Task DisabledControllerMatchesStaticSpacingWithCeilingAndDelay()
    {
        await AssertSpacingMatchesReference(ceiling: 2.0, crawlDelaySeconds: 0.5);
    }

    [Fact]
    public async Task DisabledControllerMatchesStaticSpacingWhenDelayDominates()
    {
        await AssertSpacingMatchesReference(ceiling: 10.0, crawlDelaySeconds: 3.0);
    }

    [Fact]
    public async Task DisabledControllerWithUnlimitedCeilingDoesNotSpace()
    {
        await AssertSpacingMatchesReference(ceiling: double.PositiveInfinity, crawlDelaySeconds: null);
    }

    [Fact]
    public void DisabledControllerNeverAdaptsAndNeverEmits()
    {
        var sink = new RecordingAdjustmentSink();
        var options = AimdTestHelpers.FastOptions() with { Enabled = false, ResolvedThroughputCeiling = 2.0 };
        var controller = AimdTestHelpers.CreateController(options, sink);

        for (var i = 0; i < 12; i++)
        {
            controller.RecordResponse(Host, AimdTestHelpers.Healthy());
        }

        controller.RecordResponse(Host, AimdTestHelpers.Status(429));
        controller.RecordResponse(Host, AimdTestHelpers.Challenge());
        controller.RecordResponse(Host, AimdTestHelpers.Healthy(5000));

        Assert.Empty(sink.Adjustments);
        var state = controller.GetState(Host);
        Assert.Equal(2.0, state.CurrentRate);
        Assert.Equal(8, state.CurrentConcurrency);
        Assert.Equal(0, state.AdjustmentCount);
        Assert.Equal(0, state.EwmaTtfbMilliseconds);
    }

    [Fact]
    public async Task DisabledControllerConcurrencyMatchesStaticShare()
    {
        var options = AimdTestHelpers.FastOptions() with
        {
            Enabled = false,
            WorkerConcurrencyCeiling = 8,
            PerHostConcurrencyCap = 4
        };
        var controller = AimdTestHelpers.CreateController(options);

        await controller.WaitAsync(Host);

        Assert.Equal(4, controller.GetState(Host).CurrentConcurrency);
    }

    [Fact]
    public void EnabledControllerDivergesFromStaticOnCongestion()
    {
        var enabledSink = new RecordingAdjustmentSink();
        var disabledSink = new RecordingAdjustmentSink();
        var enabled = AimdTestHelpers.CreateController(AimdTestHelpers.FastOptions() with { ResolvedThroughputCeiling = 2.0 }, enabledSink);
        var disabled = AimdTestHelpers.CreateController(AimdTestHelpers.FastOptions() with { Enabled = false, ResolvedThroughputCeiling = 2.0 }, disabledSink);

        enabled.RecordResponse(Host, AimdTestHelpers.Status(429));
        disabled.RecordResponse(Host, AimdTestHelpers.Status(429));

        Assert.Equal(0.5, enabled.GetState(Host).CurrentRate);
        Assert.Equal(2.0, disabled.GetState(Host).CurrentRate);
        Assert.Single(enabledSink.Adjustments);
        Assert.Empty(disabledSink.Adjustments);
    }
}
