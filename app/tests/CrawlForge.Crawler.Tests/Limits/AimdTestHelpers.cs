using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Politeness;

namespace CrawlForge.Crawler.Tests.Limits;

/// <summary>Test sink capturing every typed adjustment in order.</summary>
internal sealed class RecordingAdjustmentSink : IHostThrottleAdjustmentSink
{
    public List<HostThrottleAdjustment> Adjustments { get; } = [];

    public void Emit(HostThrottleAdjustment adjustment) => Adjustments.Add(adjustment);
}

/// <summary>Test crawl-delay source with per-host canned values.</summary>
internal sealed class FakeCrawlDelaySource : ICrawlDelaySource
{
    public Dictionary<string, double> Delays { get; } = new(StringComparer.OrdinalIgnoreCase);

    public double? GetCrawlDelaySeconds(string host) =>
        Delays.TryGetValue(host, out var delay) ? delay : null;
}

/// <summary>Shared sample factories and controller builders for AIMD tests.</summary>
internal static class AimdTestHelpers
{
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static HostResponseSample Healthy(double ttfbMs = 100, int statusCode = 200) =>
        new(ttfbMs, statusCode, IsChallenge: false, ObservedAt: Epoch);

    public static HostResponseSample Challenge(double ttfbMs = 100) =>
        new(ttfbMs, StatusCode: 200, IsChallenge: true, ObservedAt: Epoch);

    public static HostResponseSample Status(int statusCode, double ttfbMs = 100) =>
        new(ttfbMs, statusCode, IsChallenge: false, ObservedAt: Epoch);

    /// <summary>Small fast parameters: K=3, M=3 warm-up, 10s cooldown, §3.3 defaults otherwise.</summary>
    public static AimdControllerOptions FastOptions() => new()
    {
        HealthyStreakTarget = 3,
        WarmupSamples = 3
    };

    public static AimdPolitenessController CreateController(
        AimdControllerOptions? options = null,
        IHostThrottleAdjustmentSink? sink = null,
        ICrawlDelaySource? crawlDelay = null,
        TimeProvider? time = null,
        long auditId = 7) =>
        new(options ?? FastOptions(), auditId, sink, crawlDelay, time ?? new ManualTimeProvider());
}
