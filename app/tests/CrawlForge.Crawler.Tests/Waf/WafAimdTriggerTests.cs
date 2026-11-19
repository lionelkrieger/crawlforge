using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Crawler.Tests.Limits;
using CrawlForge.Crawler.Waf;
using Xunit;

namespace CrawlForge.Crawler.Tests.Waf;

/// <summary>
/// G07 WAF-to-AIMD trigger path (FS-CRAWL-002 §3.4): a challenge verdict
/// feeds IsChallenge=true into the controller, producing the multiplicative
/// decrease and the locked waf-soft-block adjustment; Retry-After transport
/// facts ride the same observation. Proves the engine wiring reorder
/// (evaluate WAF BEFORE recording the AIMD sample) at component level.
/// </summary>
public class WafAimdTriggerTests
{
    private const string Host = "example.com";

    private static HttpFetchResult Fetch(int status = 200, IReadOnlyDictionary<string, string>? headers = null) =>
        new(
            $"https://{Host}/page",
            status,
            headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ReadOnlyMemory<byte>.Empty,
            Ttfb: TimeSpan.FromMilliseconds(100),
            TotalDuration: TimeSpan.FromMilliseconds(250));

    private static WafEvaluationResult Verdict(bool isChallenge) =>
        new(
            isChallenge,
            isChallenge ? WafProvider.Cloudflare : WafProvider.Unknown,
            isChallenge ? WafChallengeClass.Interstitial : WafChallengeClass.Unknown,
            isChallenge ? ["cloudflare-interstitial"] : [],
            "{}");

    /// <summary>A challenge verdict halves the rate and emits the locked waf-soft-block adjustment.</summary>
    [Fact]
    public void ChallengeVerdictHalvesRateAndEmitsWafSoftBlock()
    {
        var sink = new RecordingAdjustmentSink();
        var time = new ManualTimeProvider();
        var controller = AimdTestHelpers.CreateController(
            new AimdControllerOptions { InitialRate = 4.0, HealthyStreakTarget = 3, WarmupSamples = 3 },
            sink: sink,
            time: time);
        var before = controller.GetState(Host);

        WafAimdTrigger.Record(controller, Host, Fetch(), Verdict(isChallenge: true), AimdTestHelpers.Epoch);

        var after = controller.GetState(Host);
        Assert.Equal(before.CurrentRate * 0.5, after.CurrentRate, precision: 9);
        Assert.Equal(Math.Max(1, (int)Math.Floor(0.5 * before.CurrentConcurrency)), after.CurrentConcurrency);

        var adjustment = Assert.Single(sink.Adjustments);
        Assert.Equal(HostThrottleTriggerCodes.WafSoftBlock, adjustment.Trigger);
        Assert.Equal("waf-soft-block", adjustment.Trigger);
        Assert.Equal(Host, adjustment.Host);
        Assert.Equal(7, adjustment.AuditId);
        Assert.Equal(before.CurrentRate, adjustment.PreviousRate);
        Assert.Equal(after.CurrentRate, adjustment.NewRate);
        Assert.Equal(before.CurrentConcurrency, adjustment.PreviousConcurrency);
        Assert.Equal(after.CurrentConcurrency, adjustment.NewConcurrency);
        Assert.Equal(100, adjustment.TtfbSampleMilliseconds);
        Assert.Equal(AimdTestHelpers.Epoch, adjustment.Timestamp);
    }

    /// <summary>A healthy verdict never decreases; the rate is untouched.</summary>
    [Fact]
    public void HealthyVerdictDoesNotDecrease()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink);
        var before = controller.GetState(Host);

        WafAimdTrigger.Record(controller, Host, Fetch(), Verdict(isChallenge: false), AimdTestHelpers.Epoch);

        Assert.Equal(before.CurrentRate, controller.GetState(Host).CurrentRate);
        Assert.Empty(sink.Adjustments);
    }

    /// <summary>A non-challenge 403 is not a WAF trigger (status backoff belongs to the parent path).</summary>
    [Fact]
    public void NonChallenge403IsNotWafTrigger()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink);
        var before = controller.GetState(Host);

        WafAimdTrigger.Record(controller, Host, Fetch(status: 403), Verdict(isChallenge: false), AimdTestHelpers.Epoch);

        Assert.Equal(before.CurrentRate, controller.GetState(Host).CurrentRate);
        Assert.Empty(sink.Adjustments);
    }

    /// <summary>Concurrency halving is observable once the controller has ramped above one worker.</summary>
    [Fact]
    public void ConcurrencyHalvesAfterRampUp()
    {
        var sink = new RecordingAdjustmentSink();
        var time = new ManualTimeProvider();
        var controller = AimdTestHelpers.CreateController(
            new AimdControllerOptions { InitialRate = 1.0, HealthyStreakTarget = 3, WarmupSamples = 3 },
            sink: sink,
            time: time);

        // Slow full responses (2s) grow the latency EWMA so the concurrency
        // coupling ramps above one worker; three healthy samples complete
        // warm-up and the first additive increase.
        var slowFetch = new HttpFetchResult(
            $"https://{Host}/page", 200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ReadOnlyMemory<byte>.Empty,
            Ttfb: TimeSpan.FromMilliseconds(100),
            TotalDuration: TimeSpan.FromSeconds(2));
        for (var i = 0; i < 3; i++)
        {
            WafAimdTrigger.Record(controller, Host, slowFetch, Verdict(isChallenge: false), AimdTestHelpers.Epoch);
        }

        var ramped = controller.GetState(Host);
        Assert.True(ramped.CurrentRate > 1.0, "the healthy streak must have increased the rate");
        Assert.True(ramped.CurrentConcurrency >= 2, $"concurrency must ramp above one worker (was {ramped.CurrentConcurrency})");

        WafAimdTrigger.Record(controller, Host, Fetch(), Verdict(isChallenge: true), AimdTestHelpers.Epoch);
        var after = controller.GetState(Host);
        Assert.Equal((int)Math.Floor(0.5 * ramped.CurrentConcurrency), after.CurrentConcurrency);
        Assert.Contains(sink.Adjustments, a => a.Trigger == HostThrottleTriggerCodes.WafSoftBlock);
    }

    /// <summary>BuildObservation carries the fetch facts and the verdict's IsChallenge.</summary>
    [Fact]
    public void BuildObservationCarriesFacts()
    {
        var fetch = Fetch();
        var observation = WafAimdTrigger.BuildObservation(fetch, Verdict(isChallenge: true), AimdTestHelpers.Epoch);
        Assert.Equal(100, observation.Sample.TtfbMilliseconds);
        Assert.Equal(200, observation.Sample.StatusCode);
        Assert.True(observation.Sample.IsChallenge);
        Assert.Equal(AimdTestHelpers.Epoch, observation.Sample.ObservedAt);
        Assert.Equal(250, observation.FullResponseLatencyMs);
        Assert.Null(observation.RetryAfterSeconds);
    }

    /// <summary>Retry-After delta-seconds parse case-insensitively.</summary>
    [Fact]
    public void RetryAfterDeltaSecondsParse()
    {
        var headers = new Dictionary<string, string> { ["retry-after"] = "120" };
        Assert.Equal(120, RetryAfterParser.Parse(headers, AimdTestHelpers.Epoch));
        Assert.Equal(0, RetryAfterParser.Parse(new Dictionary<string, string> { ["Retry-After"] = "-5" }, AimdTestHelpers.Epoch));
    }

    /// <summary>Retry-After HTTP-dates parse relative to the observation clock.</summary>
    [Fact]
    public void RetryAfterHttpDateParses()
    {
        var future = AimdTestHelpers.Epoch.AddSeconds(90).ToString("R");
        var parsed = RetryAfterParser.Parse(new Dictionary<string, string> { ["Retry-After"] = future }, AimdTestHelpers.Epoch);
        Assert.NotNull(parsed);
        Assert.InRange(parsed!.Value, 89, 91);

        var past = AimdTestHelpers.Epoch.AddSeconds(-90).ToString("R");
        Assert.Equal(0, RetryAfterParser.Parse(new Dictionary<string, string> { ["Retry-After"] = past }, AimdTestHelpers.Epoch));
    }

    /// <summary>Missing or unparseable Retry-After yields null.</summary>
    [Fact]
    public void InvalidRetryAfterYieldsNull()
    {
        Assert.Null(RetryAfterParser.Parse(null, AimdTestHelpers.Epoch));
        Assert.Null(RetryAfterParser.Parse(new Dictionary<string, string>(), AimdTestHelpers.Epoch));
        Assert.Null(RetryAfterParser.Parse(new Dictionary<string, string> { ["Retry-After"] = "soon" }, AimdTestHelpers.Epoch));
    }

    /// <summary>A 429 challenge with Retry-After pauses max(retry-after, cooldown) (§3.4).</summary>
    [Fact]
    public async Task Challenge429WithRetryAfterPauses()
    {
        var sink = new RecordingAdjustmentSink();
        var time = new ManualTimeProvider();
        var controller = AimdTestHelpers.CreateController(
            new AimdControllerOptions { InitialRate = 4.0, HealthyStreakTarget = 3, WarmupSamples = 3, CooldownSeconds = 10 },
            sink: sink,
            time: time);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Retry-After"] = "30" };
        WafAimdTrigger.Record(controller, Host, Fetch(status: 429, headers: headers), Verdict(isChallenge: true), AimdTestHelpers.Epoch);

        Assert.Equal(2.0, controller.GetState(Host).CurrentRate, precision: 9);
        // Locked trigger precedence: the 429 status trigger classifies before
        // IsChallenge; the Retry-After pause rides the same observation.
        Assert.Equal(
            [HostThrottleTriggerCodes.Http429, HostThrottleTriggerCodes.RetryAfter],
            sink.Adjustments.Select(a => a.Trigger));

        // The pause holds requests for max(30, 10) = 30 seconds.
        var wait = controller.WaitAsync(Host).AsTask();
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(wait.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
