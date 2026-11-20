using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Crawler.Tests.Limits;
using CrawlForge.Crawler.Tests.Waf.Support;
using CrawlForge.Crawler.Waf;
using Xunit;

namespace CrawlForge.Crawler.Tests.Waf;

/// <summary>
/// D-021 five-class WAF block policy (FS-CRAWL-002 §2.3.1, §2.5; WP-10-05):
/// soft blocks (managed-challenge, rate-limit-soft-block) throttle through the
/// real AIMD controller (multiplicative decrease, locked waf-soft-block
/// trigger); hard blocks (captcha, interstitial, botblock) stop the host and
/// never touch the throttle; an unclassified corroborated challenge keeps the
/// conservative phase-09 throttle. Wire tokens come from the single authority
/// <see cref="EnumWireNames"/> — new members hyphenated, existing
/// interstitial/botblock wires locked lowercase.
/// </summary>
public class WafBlockPolicyTests
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

    private static WafEvaluationResult Verdict(WafChallengeClass challengeClass, bool isChallenge = true) =>
        new(
            isChallenge,
            isChallenge ? WafProvider.Cloudflare : WafProvider.Unknown,
            challengeClass,
            isChallenge ? ["test-signature"] : [],
            "{}");

    /// <summary>The five spec classes split soft/hard exactly as §2.3.1 dictates.</summary>
    [Theory]
    [InlineData(WafChallengeClass.ManagedChallenge, WafBlockResponse.AimdThrottle)]
    [InlineData(WafChallengeClass.RateLimitSoftBlock, WafBlockResponse.AimdThrottle)]
    [InlineData(WafChallengeClass.Captcha, WafBlockResponse.HostStop)]
    [InlineData(WafChallengeClass.Interstitial, WafBlockResponse.HostStop)]
    [InlineData(WafChallengeClass.BotBlock, WafBlockResponse.HostStop)]
    public void FiveClasses_ClassifySoftThrottleHardStop(WafChallengeClass challengeClass, WafBlockResponse expected)
    {
        Assert.Equal(expected, WafBlockPolicy.Classify(challengeClass));
        Assert.Equal(expected == WafBlockResponse.HostStop, WafBlockPolicy.IsHardBlock(challengeClass));
    }

    /// <summary>Soft-block membership is exactly the two rate-gated classes.</summary>
    [Theory]
    [InlineData(WafChallengeClass.ManagedChallenge, true)]
    [InlineData(WafChallengeClass.RateLimitSoftBlock, true)]
    [InlineData(WafChallengeClass.Captcha, false)]
    [InlineData(WafChallengeClass.Interstitial, false)]
    [InlineData(WafChallengeClass.BotBlock, false)]
    [InlineData(WafChallengeClass.Unknown, false)]
    public void IsSoftBlock_MembershipIsExact(WafChallengeClass challengeClass, bool expected) =>
        Assert.Equal(expected, WafBlockPolicy.IsSoftBlock(challengeClass));

    /// <summary>An unestablished class keeps the conservative phase-09 throttle
    /// (never an implicit host stop).</summary>
    [Fact]
    public void UnknownClass_ThrottlesConservatively()
    {
        Assert.Equal(WafBlockResponse.AimdThrottle, WafBlockPolicy.Classify(WafChallengeClass.Unknown));
        Assert.False(WafBlockPolicy.IsHardBlock(WafChallengeClass.Unknown));
    }

    /// <summary>A non-challenge verdict carries no WAF response.</summary>
    [Fact]
    public void NonChallengeVerdict_ResolvesNone()
    {
        Assert.Equal(WafBlockResponse.None, WafBlockPolicy.Resolve(Verdict(WafChallengeClass.Unknown, isChallenge: false)));
        Assert.Equal(WafBlockResponse.None, WafAimdTrigger.ResolveAction(Verdict(WafChallengeClass.Captcha, isChallenge: false)));
    }

    /// <summary>Wire tokens: new members hyphenated, existing wires locked.</summary>
    [Theory]
    [InlineData(WafChallengeClass.ManagedChallenge, "managed-challenge")]
    [InlineData(WafChallengeClass.RateLimitSoftBlock, "rate-limit-soft-block")]
    [InlineData(WafChallengeClass.Captcha, "captcha")]
    [InlineData(WafChallengeClass.Interstitial, "interstitial")]
    [InlineData(WafChallengeClass.BotBlock, "botblock")]
    [InlineData(WafChallengeClass.Unknown, "unknown")]
    public void WireTokens_AreTheLockedVocabulary(WafChallengeClass challengeClass, string expected)
    {
        Assert.Equal(expected, EnumWireNames.ToWireValue(challengeClass));
        Assert.True(EnumWireNames.TryParse<WafChallengeClass>(expected, out var parsed));
        Assert.Equal(challengeClass, parsed);
    }

    /// <summary>Soft blocks feed the REAL AIMD controller: rate halves and the
    /// locked waf-soft-block adjustment is emitted (both soft classes).</summary>
    [Theory]
    [InlineData(WafChallengeClass.ManagedChallenge)]
    [InlineData(WafChallengeClass.RateLimitSoftBlock)]
    public void SoftBlock_RecordsAimdMultiplicativeDecrease(WafChallengeClass challengeClass)
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(
            new AimdControllerOptions { InitialRate = 4.0, HealthyStreakTarget = 3, WarmupSamples = 3 },
            sink: sink);
        var before = controller.GetState(Host);

        var response = WafAimdTrigger.RecordVerdict(controller, Host, Fetch(), Verdict(challengeClass), AimdTestHelpers.Epoch);

        Assert.Equal(WafBlockResponse.AimdThrottle, response);
        var after = controller.GetState(Host);
        Assert.Equal(before.CurrentRate * 0.5, after.CurrentRate, precision: 9);
        var adjustment = Assert.Single(sink.Adjustments);
        Assert.Equal(HostThrottleTriggerCodes.WafSoftBlock, adjustment.Trigger);
        Assert.Equal("waf-soft-block", adjustment.Trigger);
        Assert.Equal(Host, adjustment.Host);
    }

    /// <summary>Hard blocks stop the host: no throttle adjustment, rate and
    /// concurrency untouched (all three hard classes).</summary>
    [Theory]
    [InlineData(WafChallengeClass.Captcha)]
    [InlineData(WafChallengeClass.Interstitial)]
    [InlineData(WafChallengeClass.BotBlock)]
    public void HardBlock_ReturnsHostStop_WithoutThrottling(WafChallengeClass challengeClass)
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(
            new AimdControllerOptions { InitialRate = 4.0, HealthyStreakTarget = 3, WarmupSamples = 3 },
            sink: sink);
        var before = controller.GetState(Host);

        var response = WafAimdTrigger.RecordVerdict(controller, Host, Fetch(), Verdict(challengeClass), AimdTestHelpers.Epoch);

        Assert.Equal(WafBlockResponse.HostStop, response);
        Assert.Empty(sink.Adjustments);
        var after = controller.GetState(Host);
        Assert.Equal(before.CurrentRate, after.CurrentRate);
        Assert.Equal(before.CurrentConcurrency, after.CurrentConcurrency);
    }

    /// <summary>A healthy verdict records the normal sample (no decrease).</summary>
    [Fact]
    public void HealthyVerdict_RecordsNormalSample()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(sink: sink);
        var before = controller.GetState(Host);

        var response = WafAimdTrigger.RecordVerdict(controller, Host, Fetch(), Verdict(WafChallengeClass.Unknown, isChallenge: false), AimdTestHelpers.Epoch);

        Assert.Equal(WafBlockResponse.None, response);
        Assert.Equal(before.CurrentRate, controller.GetState(Host).CurrentRate);
        Assert.Empty(sink.Adjustments);
    }

    /// <summary>An unclassified challenge still throttles (phase-09 baseline).</summary>
    [Fact]
    public void UnclassifiedChallenge_StillThrottles()
    {
        var sink = new RecordingAdjustmentSink();
        var controller = AimdTestHelpers.CreateController(
            new AimdControllerOptions { InitialRate = 4.0, HealthyStreakTarget = 3, WarmupSamples = 3 },
            sink: sink);

        var response = WafAimdTrigger.RecordVerdict(controller, Host, Fetch(), Verdict(WafChallengeClass.Unknown), AimdTestHelpers.Epoch);

        Assert.Equal(WafBlockResponse.AimdThrottle, response);
        Assert.Contains(sink.Adjustments, a => a.Trigger == HostThrottleTriggerCodes.WafSoftBlock);
    }

    /// <summary>A soft-block Retry-After pause rides the same observation
    /// (max(retry-after, cooldown), §3.4).</summary>
    [Fact]
    public async Task SoftBlockWithRetryAfter_PausesHost()
    {
        var sink = new RecordingAdjustmentSink();
        var time = new ManualTimeProvider();
        var controller = AimdTestHelpers.CreateController(
            new AimdControllerOptions { InitialRate = 4.0, HealthyStreakTarget = 3, WarmupSamples = 3, CooldownSeconds = 10 },
            sink: sink,
            time: time);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Retry-After"] = "30" };

        var response = WafAimdTrigger.RecordVerdict(
            controller, Host, Fetch(status: 429, headers: headers), Verdict(WafChallengeClass.RateLimitSoftBlock), AimdTestHelpers.Epoch);

        Assert.Equal(WafBlockResponse.AimdThrottle, response);
        Assert.Equal(2.0, controller.GetState(Host).CurrentRate, precision: 9);
        var wait = controller.WaitAsync(Host).AsTask();
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(wait.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>The evaluator classifies the two NEW soft classes from catalogue
    /// signatures (high-specificity header corroboration), and the policy
    /// resolves them to the AIMD throttle.</summary>
    [Theory]
    [InlineData(WafChallengeClass.ManagedChallenge, "cf-managed-challenge", "cf-mitigated~managed|0.9|high")]
    [InlineData(WafChallengeClass.RateLimitSoftBlock, "generic-rate-limit-soft", "x-ratelimit-remaining~0|0.9|high")]
    public async Task Evaluator_ClassifiesNewSoftClasses(WafChallengeClass challengeClass, string key, string headerMarker)
    {
        var catalogue = new InMemoryWafCatalogue(new WafSignature(
            key,
            WafProvider.Other,
            challengeClass,
            HeaderMarkers: [headerMarker],
            BodyMarkers: [],
            IsBundled: false));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var headers = challengeClass == WafChallengeClass.ManagedChallenge
            ? new Dictionary<string, string> { ["cf-mitigated"] = "managed" }
            : new Dictionary<string, string> { ["x-ratelimit-remaining"] = "0" };

        var result = evaluator.Evaluate(new WafEvaluationInput($"https://{Host}/gate", 200, headers, "<html><title>Checking</title></html>"));

        Assert.True(result.IsChallenge);
        Assert.Equal(challengeClass, result.ChallengeClass);
        Assert.Contains(key, result.MatchedSignatureKeys);
        Assert.Equal(WafBlockResponse.AimdThrottle, WafBlockPolicy.Resolve(result));
    }

    /// <summary>The evaluator's hard-class verdicts resolve to host stop.</summary>
    [Theory]
    [InlineData(WafChallengeClass.Captcha)]
    [InlineData(WafChallengeClass.Interstitial)]
    [InlineData(WafChallengeClass.BotBlock)]
    public async Task Evaluator_HardClassesResolveHostStop(WafChallengeClass challengeClass)
    {
        var catalogue = new InMemoryWafCatalogue(new WafSignature(
            "hard-class-signature",
            WafProvider.Other,
            challengeClass,
            HeaderMarkers: ["x-hard-waf|0.9|high"],
            BodyMarkers: [],
            IsBundled: false));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);

        var result = evaluator.Evaluate(new WafEvaluationInput(
            $"https://{Host}/hard", 200,
            new Dictionary<string, string> { ["x-hard-waf"] = "1" },
            "<html>checkpoint</html>"));

        Assert.True(result.IsChallenge);
        Assert.Equal(challengeClass, result.ChallengeClass);
        Assert.Equal(WafBlockResponse.HostStop, WafBlockPolicy.Resolve(result));
    }
}

/// <summary>
/// Bundled-asset challenge-class parsing goes through the single wire
/// authority: hyphenated NEW tokens (managed-challenge, rate-limit-soft-block)
/// load from the asset, and the locked lowercase wires keep loading. Proves
/// the asset upgrade path the coordinator applies to waf-catalogue.json.
/// </summary>
public class WafCatalogueWireTokenTests : IAsyncLifetime
{
    private readonly WafTestDatabase _db = new();
    private string _assetPath = null!;

    /// <summary>Creates the per-test fixture.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public Task InitializeAsync() => _db.InitializeAsync();

    /// <summary>Tears down the per-test fixture.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (File.Exists(_assetPath))
        {
            File.Delete(_assetPath);
        }
    }

    /// <summary>Hyphenated and locked class tokens both parse from the asset.</summary>
    [Fact]
    public async Task BundledAsset_ParsesHyphenatedAndLockedClassTokens()
    {
        _assetPath = Path.Combine(Path.GetTempPath(), $"cf-wp-10-05-waf-asset-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(_assetPath, """
            {
              "version": "2026.11-test",
              "revision": "r1",
              "signatures": [
                {
                  "key": "test-managed-challenge",
                  "provider": "Cloudflare",
                  "challengeClass": "managed-challenge",
                  "headerMarkers": ["cf-mitigated~managed|0.9|high"]
                },
                {
                  "key": "test-rate-limit-soft",
                  "provider": "Other",
                  "challengeClass": "rate-limit-soft-block",
                  "headerMarkers": ["x-ratelimit-remaining~0|0.9|high"]
                },
                {
                  "key": "test-locked-interstitial",
                  "provider": "Cloudflare",
                  "challengeClass": "Interstitial",
                  "headerMarkers": ["cf-mitigated~challenge|0.9|high"]
                },
                {
                  "key": "test-locked-botblock",
                  "provider": "Akamai",
                  "challengeClass": "BotBlock",
                  "headerMarkers": ["x-akamai-bot|0.9|high"]
                }
              ]
            }
            """);
        var catalogue = new WafSignatureCatalogue(_db.Provider, _assetPath, new FixedTimeProvider());

        var snapshot = await catalogue.GetEffectiveCatalogueAsync();

        Assert.Equal(WafChallengeClass.ManagedChallenge, snapshot.Signatures.Single(s => s.Key == "test-managed-challenge").ChallengeClass);
        Assert.Equal(WafChallengeClass.RateLimitSoftBlock, snapshot.Signatures.Single(s => s.Key == "test-rate-limit-soft").ChallengeClass);
        Assert.Equal(WafChallengeClass.Interstitial, snapshot.Signatures.Single(s => s.Key == "test-locked-interstitial").ChallengeClass);
        Assert.Equal(WafChallengeClass.BotBlock, snapshot.Signatures.Single(s => s.Key == "test-locked-botblock").ChallengeClass);
    }
}
