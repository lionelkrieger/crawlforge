using System.Text;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Tests.Waf.Support;
using CrawlForge.Crawler.Waf;
using Xunit;

namespace CrawlForge.Crawler.Tests.Waf;

/// <summary>
/// Acceptance criterion #35: a re-audit re-detects WAF challenges from stored
/// bodies through the compressed content store, fully offline. Observed
/// headers recorded in the detection evidence restore the two-facet
/// corroboration; body-only re-evaluation stays suspected; the current
/// effective catalogue (including user edits) drives the fresh verdict.
/// </summary>
public class WafStoredBodyRedetectionTests : IAsyncLifetime
{
    private readonly WafTestDatabase _db = new();

    /// <summary>Creates the per-test fixture with the parent graph and two URLs.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await _db.InsertGraphAsync();
        await _db.InsertUrlAsync(1, "https://example.com/page-1");
        await _db.InsertUrlAsync(2, "https://example.com/page-2");
    }

    /// <summary>Tears down the per-test fixture.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public Task DisposeAsync() => _db.DisposeAsync();

    /// <summary>A stored challenge body plus recorded headers re-detects the same provider and class offline.</summary>
    [Fact]
    public async Task ReDetectsChallengeFromStoredBodyOffline()
    {
        var input = WafResponses.CloudflareInterstitial();
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var recorder = new WafDetectionRecorder(_db.Provider, new FixedTimeProvider());
        await recorder.RecordChallengeAsync(1, 1, evaluator.Evaluate(input));
        await StoreBodyAsync(1, input.BodySnippet!);

        var redetector = new WafStoredBodyRedetector(evaluator, _db.ContentStore, _db.Provider);
        var result = await redetector.RedetectAsync(1, 1);

        Assert.NotNull(result);
        Assert.True(result.IsChallenge);
        Assert.Equal(WafProvider.Cloudflare, result.Provider);
        Assert.Equal(WafChallengeClass.Interstitial, result.ChallengeClass);
    }

    /// <summary>URLs without a stored body artifact return null.</summary>
    [Fact]
    public async Task ReDetectionWithoutStoredBodyReturnsNull()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var redetector = new WafStoredBodyRedetector(evaluator, _db.ContentStore, _db.Provider);
        Assert.Null(await redetector.RedetectAsync(1, 2));
    }

    /// <summary>Without recorded headers the body-only re-evaluation stays suspected, never blocked.</summary>
    [Fact]
    public async Task BodyOnlyReDetectionIsSuspectedNotBlocked()
    {
        var input = WafResponses.CloudflareInterstitial();
        await StoreBodyAsync(1, input.BodySnippet!);

        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var redetector = new WafStoredBodyRedetector(evaluator, _db.ContentStore, _db.Provider);
        var result = await redetector.RedetectAsync(1, 1);

        Assert.NotNull(result);
        Assert.False(result.IsChallenge);
        Assert.NotEmpty(result.MatchedSignatureKeys);
    }

    /// <summary>Re-detection uses the current effective catalogue, including user-local edits.</summary>
    [Fact]
    public async Task ReDetectionUsesUpdatedCatalogue()
    {
        var input = WafResponses.CloudflareInterstitial();
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var recorder = new WafDetectionRecorder(_db.Provider, new FixedTimeProvider());
        await recorder.RecordChallengeAsync(1, 1, evaluator.Evaluate(input));
        await StoreBodyAsync(1, input.BodySnippet!);

        // The user shadows the bundled Cloudflare interstitial with a local attribution.
        await _db.Catalogue.UpsertUserSignatureAsync(new Contracts.Entities.WafUserSignatureDto(
            Id: 0,
            Key: "cloudflare-interstitial",
            Provider: WafProvider.Other,
            ChallengeClass: WafChallengeClass.BotBlock,
            HeaderMarkersJson: "[\"cf-mitigated~challenge|0.6\",\"server~cloudflare|0.5\",\"cookie:__cf_bm|0.5\"]",
            BodyMarkersJson: "[\"challenge-platform|0.6\"]",
            IsEnabled: true,
            CreatedAt: FixedTimeProvider.FixedInstant,
            UpdatedAt: FixedTimeProvider.FixedInstant));

        var refreshed = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var redetector = new WafStoredBodyRedetector(refreshed, _db.ContentStore, _db.Provider);
        var result = await redetector.RedetectAsync(1, 1);

        Assert.NotNull(result);
        Assert.True(result.IsChallenge);
        Assert.Equal(WafProvider.Other, result.Provider);
        Assert.Equal(WafChallengeClass.BotBlock, result.ChallengeClass);
    }

    /// <summary>The stored-body read honors the bounded pre-parse limit.</summary>
    [Fact]
    public async Task ReDetectionRespectsBoundedRead()
    {
        var body = new string('a', 69000) + "please verify you are human";
        await StoreBodyAsync(1, body);

        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var redetector = new WafStoredBodyRedetector(evaluator, _db.ContentStore, _db.Provider);
        var result = await redetector.RedetectAsync(1, 1);

        Assert.NotNull(result);
        Assert.False(result.IsChallenge);
        Assert.Empty(result.MatchedSignatureKeys);
    }

    /// <summary>The compressed store round-trips the stored body byte-for-byte.</summary>
    [Fact]
    public async Task StoredBodyArtifactRoundTrips()
    {
        const string body = "<html><body>Just a moment... challenge-platform</body></html>";
        var artifact = await StoreBodyAsync(1, body);
        Assert.True(artifact.SizeBytes > 0);
        Assert.False(string.IsNullOrEmpty(artifact.ContentHash));
        Assert.False(string.IsNullOrEmpty(artifact.StorageKey));

        await using var stream = await _db.ContentStore.OpenReadAsync(artifact.StorageKey);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal(body, await reader.ReadToEndAsync());
    }

    private async Task<Contracts.Entities.ContentArtifactDto> StoreBodyAsync(long urlId, string body)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return await _db.ContentStore.PutAsync(1, urlId, ContentArtifactKind.Body, stream);
    }
}
