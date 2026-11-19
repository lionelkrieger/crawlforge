using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Tests.Waf.Support;
using CrawlForge.Crawler.Waf;
using Xunit;

namespace CrawlForge.Crawler.Tests.Waf;

/// <summary>
/// Evaluator behavior: Stage A (header/cookie) and Stage B (bounded pre-parse)
/// fingerprinting, the corroboration guard (two independent facets or one
/// high-specificity marker), the 0.8 default confidence threshold, the
/// 2xx+HTML eligibility gate (§2.4.1), provider attribution, and
/// detection-only verdicts.
/// </summary>
public class WafEvaluatorTests : IAsyncLifetime
{
    private readonly WafTestDatabase _db = new();

    /// <summary>Creates the per-test fixture.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public Task InitializeAsync() => _db.InitializeAsync();

    /// <summary>Tears down the per-test fixture.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public Task DisposeAsync() => _db.DisposeAsync();

    /// <summary>A clean page produces no challenge and no suspected keys.</summary>
    [Fact]
    public async Task CleanResponseIsNotChallenge()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var result = evaluator.Evaluate(WafResponses.Clean());
        Assert.False(result.IsChallenge);
        Assert.Equal(WafProvider.Unknown, result.Provider);
        Assert.Equal(WafChallengeClass.Unknown, result.ChallengeClass);
        Assert.Empty(result.MatchedSignatureKeys);
    }

    /// <summary>A Cloudflare interstitial served with 200 OK is classified with provider and class.</summary>
    [Fact]
    public async Task CloudflareInterstitialIsDetectedOn200Ok()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var result = evaluator.Evaluate(WafResponses.CloudflareInterstitial());
        Assert.True(result.IsChallenge);
        Assert.Equal(WafProvider.Cloudflare, result.Provider);
        Assert.Equal(WafChallengeClass.Interstitial, result.ChallengeClass);
        Assert.Contains("cloudflare-interstitial", result.MatchedSignatureKeys);
        AssertConfidenceAndFacets(result, minimumConfidence: 0.8, minimumFacets: 2);
    }

    /// <summary>A DataDome interstitial is attributed to DataDome.</summary>
    [Fact]
    public async Task DataDomeInterstitialIsDetected()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var result = evaluator.Evaluate(WafResponses.DataDomeInterstitial());
        Assert.True(result.IsChallenge);
        Assert.Equal(WafProvider.DataDome, result.Provider);
        Assert.Contains("datadome-interstitial", result.MatchedSignatureKeys);
    }

    /// <summary>An Akamai bot-block document is attributed to Akamai with the BotBlock class.</summary>
    [Fact]
    public async Task AkamaiBotBlockIsDetected()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var result = evaluator.Evaluate(WafResponses.AkamaiBotBlock());
        Assert.True(result.IsChallenge);
        Assert.Equal(WafProvider.Akamai, result.Provider);
        Assert.Equal(WafChallengeClass.BotBlock, result.ChallengeClass);
    }

    /// <summary>An Imperva interstitial is attributed to Imperva.</summary>
    [Fact]
    public async Task ImpervaInterstitialIsDetected()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var result = evaluator.Evaluate(WafResponses.ImpervaInterstitial());
        Assert.True(result.IsChallenge);
        Assert.Equal(WafProvider.Imperva, result.Provider);
        Assert.Equal(WafChallengeClass.Interstitial, result.ChallengeClass);
    }

    /// <summary>A PerimeterX CAPTCHA is attributed to PerimeterX with the Captcha class.</summary>
    [Fact]
    public async Task PerimeterXCaptchaIsDetected()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var result = evaluator.Evaluate(WafResponses.PerimeterXCaptcha());
        Assert.True(result.IsChallenge);
        Assert.Equal(WafProvider.PerimeterX, result.Provider);
        Assert.Equal(WafChallengeClass.Captcha, result.ChallengeClass);
    }

    /// <summary>A single header facet never classifies as blocked, regardless of marker strength.</summary>
    [Fact]
    public async Task SingleHeaderFacetNeverBlocks()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var result = evaluator.Evaluate(WafResponses.CloudflareHeadersOnly());
        Assert.False(result.IsChallenge);
        Assert.NotEmpty(result.MatchedSignatureKeys);
        AssertSuspected(result);
    }

    /// <summary>A single body facet never classifies as blocked.</summary>
    [Fact]
    public async Task SingleBodyFacetNeverBlocks()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var result = evaluator.Evaluate(WafResponses.ChallengeBodyOnly());
        Assert.False(result.IsChallenge);
        Assert.NotEmpty(result.MatchedSignatureKeys);
        AssertSuspected(result);
    }

    /// <summary>A single cookie facet never classifies as blocked.</summary>
    [Fact]
    public async Task SingleCookieFacetNeverBlocks()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var result = evaluator.Evaluate(WafResponses.ChallengeCookieOnly());
        Assert.False(result.IsChallenge);
        Assert.NotEmpty(result.MatchedSignatureKeys);
        AssertSuspected(result);
    }

    /// <summary>Two corroborating facets below the confidence threshold remain suspected, never blocked.</summary>
    [Fact]
    public async Task TwoFacetsBelowThresholdAreSuspected()
    {
        var catalogue = new InMemoryWafCatalogue(new WafSignature(
            "weak-two-facet",
            WafProvider.Other,
            WafChallengeClass.Interstitial,
            HeaderMarkers: ["x-weak-waf|0.3"],
            BodyMarkers: ["weak-challenge-marker|0.3"],
            IsBundled: false));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var result = evaluator.Evaluate(new WafEvaluationInput(
            "https://example.com/weak",
            200,
            new Dictionary<string, string> { ["x-weak-waf"] = "1" },
            "<html>weak-challenge-marker</html>"));
        Assert.False(result.IsChallenge);
        Assert.Contains("weak-two-facet", result.MatchedSignatureKeys);
        AssertSuspected(result);
    }

    /// <summary>A custom confidence threshold is honored by the guard.</summary>
    [Fact]
    public async Task CustomConfidenceThresholdIsHonored()
    {
        var catalogue = new InMemoryWafCatalogue(new WafSignature(
            "custom-two-facet",
            WafProvider.Other,
            WafChallengeClass.BotBlock,
            HeaderMarkers: ["x-custom-waf|0.5"],
            BodyMarkers: ["custom-challenge-marker|0.4"],
            IsBundled: false));
        var input = new WafEvaluationInput(
            "https://example.com/custom",
            200,
            new Dictionary<string, string> { ["x-custom-waf"] = "1" },
            "<html>custom-challenge-marker</html>");

        var strict = await WafChallengeEvaluator.CreateAsync(catalogue, new WafDetectionPolicy { ConfidenceThreshold = 0.99 });
        Assert.False(strict.Evaluate(input).IsChallenge);

        var lenient = await WafChallengeEvaluator.CreateAsync(catalogue, new WafDetectionPolicy { ConfidenceThreshold = 0.8 });
        Assert.True(lenient.Evaluate(input).IsChallenge);
    }

    /// <summary>A disabled policy returns a clean verdict even for a fully corroborated challenge.</summary>
    [Fact]
    public async Task DisabledPolicyReturnsCleanVerdict()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue, new WafDetectionPolicy { Enabled = false });
        var result = evaluator.Evaluate(WafResponses.CloudflareInterstitial());
        Assert.False(result.IsChallenge);
        Assert.Empty(result.MatchedSignatureKeys);
    }

    /// <summary>
    /// Non-2xx challenge signatures are evidence annotations only (§2.4.1):
    /// the status class never transitions to waf-challenge, but the matched
    /// signatures and eligibility facts are retained in the evidence.
    /// </summary>
    [Fact]
    public async Task Non200ChallengeIsAnnotationOnly()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var result = evaluator.Evaluate(WafResponses.CloudflareWith403());
        Assert.False(result.IsChallenge);
        Assert.NotEmpty(result.MatchedSignatureKeys);
        Assert.Contains(result.MatchedSignatureKeys, k => k.Contains("cloudflare", StringComparison.OrdinalIgnoreCase));

        using var evidence = JsonDocument.Parse(result.EvidenceJson);
        Assert.False(evidence.RootElement.GetProperty("eligible").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("statusEligible").GetBoolean());
        Assert.True(evidence.RootElement.GetProperty("suspected").GetBoolean());
    }

    /// <summary>The pre-parse stage never inspects body content beyond the configured bound.</summary>
    [Fact]
    public async Task BodySnippetIsBounded()
    {
        var catalogue = new InMemoryWafCatalogue(new WafSignature(
            "late-body-marker",
            WafProvider.Other,
            WafChallengeClass.Interstitial,
            HeaderMarkers: [],
            BodyMarkers: ["late-challenge-marker|0.9"],
            IsBundled: false));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue, new WafDetectionPolicy { BodySnippetLimit = 64 });
        var body = new string('a', 128) + "late-challenge-marker";
        var result = evaluator.Evaluate(new WafEvaluationInput(
            "https://example.com/late", 200, new Dictionary<string, string>(), body));
        Assert.False(result.IsChallenge);
        Assert.Empty(result.MatchedSignatureKeys);
    }

    /// <summary>Header and cookie matching is case-insensitive.</summary>
    [Fact]
    public async Task HeaderMatchingIsCaseInsensitive()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var input = WafResponses.CloudflareInterstitial() with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SERVER"] = "CloudFlare",
                ["CF-Mitigated"] = "Challenge",
                ["Set-Cookie"] = "__CF_BM=token; Path=/",
            },
        };
        var result = evaluator.Evaluate(input);
        Assert.True(result.IsChallenge);
        Assert.Equal(WafProvider.Cloudflare, result.Provider);
    }

    /// <summary>Evaluating before initialization is an explicit error.</summary>
    [Fact]
    public void EvaluateBeforeInitializeThrows()
    {
        var evaluator = new WafChallengeEvaluator(_db.Catalogue);
        Assert.Throws<InvalidOperationException>(() => evaluator.Evaluate(WafResponses.Clean()));
    }

    /// <summary>Identical inputs produce byte-identical evidence payloads.</summary>
    [Fact]
    public async Task EvidenceIsDeterministic()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var first = evaluator.Evaluate(WafResponses.CloudflareInterstitial());
        var second = evaluator.Evaluate(WafResponses.CloudflareInterstitial());
        Assert.Equal(first.EvidenceJson, second.EvidenceJson);
        Assert.Equal(first.MatchedSignatureKeys, second.MatchedSignatureKeys);
    }

    /// <summary>Cookie names match at cookie boundaries only, never as name prefixes.</summary>
    [Fact]
    public async Task CookieNameBoundaryIsExact()
    {
        var catalogue = new InMemoryWafCatalogue(new WafSignature(
            "prefix-cookie",
            WafProvider.Other,
            WafChallengeClass.Interstitial,
            HeaderMarkers: ["cookie:_px|0.9"],
            BodyMarkers: [],
            IsBundled: false));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var result = evaluator.Evaluate(new WafEvaluationInput(
            "https://example.com/prefix",
            200,
            new Dictionary<string, string> { ["set-cookie"] = "_pxhd=token; Path=/" },
            null));
        Assert.False(result.IsChallenge);
        Assert.Empty(result.MatchedSignatureKeys);
    }

    /// <summary>The generic body-only signature reports suspicion, never a blocked classification.</summary>
    [Fact]
    public async Task GenericBodyOnlyIsSuspectedNotBlocked()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var result = evaluator.Evaluate(new WafEvaluationInput(
            "https://example.com/generic",
            200,
            new Dictionary<string, string> { ["server"] = "nginx" },
            "<html><body>Please verify you are human before continuing.</body></html>"));
        Assert.False(result.IsChallenge);
        Assert.Contains("generic-challenge", result.MatchedSignatureKeys);
        AssertSuspected(result);
    }

    /// <summary>When several signatures corroborate, the highest-confidence candidate wins deterministically.</summary>
    [Fact]
    public async Task HighestConfidenceCandidateWins()
    {
        var catalogue = new InMemoryWafCatalogue(
            new WafSignature(
                "aaa-lower",
                WafProvider.Other,
                WafChallengeClass.Interstitial,
                HeaderMarkers: ["x-shared|0.5"],
                BodyMarkers: ["shared-marker|0.4"],
                IsBundled: false),
            new WafSignature(
                "zzz-higher",
                WafProvider.Cloudflare,
                WafChallengeClass.BotBlock,
                HeaderMarkers: ["x-shared|0.6"],
                BodyMarkers: ["shared-marker|0.6"],
                IsBundled: false));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var result = evaluator.Evaluate(new WafEvaluationInput(
            "https://example.com/shared",
            200,
            new Dictionary<string, string> { ["x-shared"] = "1" },
            "<html>shared-marker</html>"));
        Assert.True(result.IsChallenge);
        Assert.Equal(WafProvider.Cloudflare, result.Provider);
        Assert.Equal("zzz-higher", result.MatchedSignatureKeys[0]);
    }

    private static void AssertConfidenceAndFacets(WafEvaluationResult result, double minimumConfidence, int minimumFacets)
    {
        using var document = JsonDocument.Parse(result.EvidenceJson);
        Assert.True(document.RootElement.GetProperty("confidence").GetDouble() >= minimumConfidence);
        Assert.True(document.RootElement.GetProperty("matchedFacets").GetArrayLength() >= minimumFacets);
    }

    private static void AssertSuspected(WafEvaluationResult result)
    {
        using var document = JsonDocument.Parse(result.EvidenceJson);
        Assert.True(document.RootElement.GetProperty("suspected").GetBoolean());
        Assert.False(document.RootElement.GetProperty("isChallenge").GetBoolean());
    }
}
