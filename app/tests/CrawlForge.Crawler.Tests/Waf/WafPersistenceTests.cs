using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Tests.Waf.Support;
using CrawlForge.Crawler.Waf;
using Xunit;

namespace CrawlForge.Crawler.Tests.Waf;

/// <summary>
/// Detection persistence: waf_detections round-trip, the blocked URL
/// classification with the waf-challenge sub-classification, per-URL findings,
/// idempotent re-recording, render-resolution persistence, and the
/// content-finding exclusion for blocked URLs.
/// </summary>
public class WafPersistenceTests : IAsyncLifetime
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

    /// <summary>Recording a challenge persists the detection and classifies the URL blocked with provider attribution.</summary>
    [Fact]
    public async Task RecordChallengePersistsDetectionAndClassifiesUrl()
    {
        var (evaluator, recorder) = await CreatePipelineAsync();
        var verdict = evaluator.Evaluate(WafResponses.CloudflareInterstitial());
        var detection = await recorder.RecordChallengeAsync(1, 1, verdict);

        Assert.True(detection.Id > 0);
        Assert.Equal(WafProvider.Cloudflare, detection.Provider);
        Assert.Equal(WafChallengeClass.Interstitial, detection.ChallengeClass);
        Assert.False(detection.ResolvedByRendering);

        Assert.Equal((int)CrawlStatusClass.Blocked, Convert.ToInt32(await _db.ScalarAsync("SELECT status_class FROM url_records WHERE id = 1;")));
        Assert.Equal(WafDetectionRecorder.WafChallengeBlockReason, await _db.ScalarAsync("SELECT block_reason FROM url_records WHERE id = 1;"));
        Assert.Equal((int)WafProvider.Cloudflare, Convert.ToInt32(await _db.ScalarAsync("SELECT waf_provider FROM url_records WHERE id = 1;")));
        Assert.Equal((int)WafChallengeClass.Interstitial, Convert.ToInt32(await _db.ScalarAsync("SELECT challenge_class FROM url_records WHERE id = 1;")));
    }

    /// <summary>Detection rows round-trip every field through the database.</summary>
    [Fact]
    public async Task DetectionRoundTripsAllFields()
    {
        var (evaluator, recorder) = await CreatePipelineAsync();
        var verdict = evaluator.Evaluate(WafResponses.PerimeterXCaptcha());
        var recorded = await recorder.RecordChallengeAsync(1, 1, verdict);

        var stored = (await recorder.GetDetectionsAsync(1)).Single();
        Assert.Equal(recorded.Id, stored.Id);
        Assert.Equal(1, stored.AuditId);
        Assert.Equal(1, stored.UrlId);
        Assert.Equal(WafProvider.PerimeterX, stored.Provider);
        Assert.Equal(WafChallengeClass.Captcha, stored.ChallengeClass);
        Assert.Equal(verdict.EvidenceJson, stored.EvidenceJson);
        Assert.Equal("perimeterx-challenge", stored.SignatureKey);
        Assert.False(stored.ResolvedByRendering);
        Assert.Equal(FixedTimeProvider.FixedInstant, stored.DetectedAt);
    }

    /// <summary>Re-recording the same (audit, URL) updates in place: one detection, one finding.</summary>
    [Fact]
    public async Task RecordIsIdempotentPerAuditUrl()
    {
        var (evaluator, recorder) = await CreatePipelineAsync();
        var verdict = evaluator.Evaluate(WafResponses.CloudflareInterstitial());
        var first = await recorder.RecordChallengeAsync(1, 1, verdict);
        var second = await recorder.RecordChallengeAsync(1, 1, verdict);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await _db.CountRowsAsync("waf_detections"));
        Assert.Equal(1, await _db.CountRowsAsync("findings"));
    }

    /// <summary>The per-URL waf-challenge-detected finding is reported in the Crawl category.</summary>
    [Fact]
    public async Task ChallengeFindingIsReported()
    {
        var (evaluator, recorder) = await CreatePipelineAsync();
        await recorder.RecordChallengeAsync(1, 1, evaluator.Evaluate(WafResponses.CloudflareInterstitial()));

        Assert.Equal(FindingRuleKeys.WafChallengeDetected, await _db.ScalarAsync("SELECT rule_key FROM findings;"));
        Assert.Equal((int)AuditCategory.Crawl, Convert.ToInt32(await _db.ScalarAsync("SELECT category FROM findings;")));
        Assert.Equal((int)FindingSeverity.Warning, Convert.ToInt32(await _db.ScalarAsync("SELECT severity FROM findings;")));
        Assert.Equal(1L, Convert.ToInt64(await _db.ScalarAsync("SELECT url_id FROM findings;")));
    }

    /// <summary>Non-challenge verdicts cannot be recorded as detections.</summary>
    [Fact]
    public async Task RecordRejectsNonChallengeVerdicts()
    {
        var (evaluator, recorder) = await CreatePipelineAsync();
        var clean = evaluator.Evaluate(WafResponses.Clean());
        await Assert.ThrowsAsync<ArgumentException>(() => recorder.RecordChallengeAsync(1, 1, clean));
        Assert.Equal(0, await _db.CountRowsAsync("waf_detections"));
    }

    /// <summary>A suspected notice never changes the URL classification and creates no detection row.</summary>
    [Fact]
    public async Task SuspectedNoticeDoesNotChangeClassification()
    {
        var (evaluator, recorder) = await CreatePipelineAsync();
        var suspected = evaluator.Evaluate(WafResponses.ChallengeBodyOnly());
        await recorder.RecordSuspectedNoticeAsync(1, 1, suspected);

        Assert.Equal(FindingRuleKeys.SuspectedChallenge, await _db.ScalarAsync("SELECT rule_key FROM findings;"));
        Assert.Equal((int)FindingSeverity.Notice, Convert.ToInt32(await _db.ScalarAsync("SELECT severity FROM findings;")));
        Assert.Equal((int)CrawlStatusClass.Success, Convert.ToInt32(await _db.ScalarAsync("SELECT status_class FROM url_records WHERE id = 1;")));
        Assert.Equal(0, await _db.CountRowsAsync("waf_detections"));
    }

    /// <summary>Render resolution marks the detection resolved, reclassifies the URL, and reports the notice.</summary>
    [Fact]
    public async Task MarkResolvedByRenderingReclassifiesUrl()
    {
        var (evaluator, recorder) = await CreatePipelineAsync();
        await recorder.RecordChallengeAsync(1, 1, evaluator.Evaluate(WafResponses.CloudflareInterstitial()));

        var resolved = await recorder.MarkResolvedByRenderingAsync(1, 1, "{\"rendered\":true}");
        Assert.True(resolved);
        Assert.True((await recorder.GetDetectionsAsync(1)).Single().ResolvedByRendering);
        Assert.Equal((int)CrawlStatusClass.Success, Convert.ToInt32(await _db.ScalarAsync("SELECT status_class FROM url_records WHERE id = 1;")));
        Assert.Equal(DBNull.Value, await _db.ScalarAsync("SELECT block_reason FROM url_records WHERE id = 1;"));
        // The original challenge finding plus the resolution notice.
        Assert.Equal(2, await _db.CountRowsAsync("findings"));
        Assert.Equal(1L, Convert.ToInt64(await _db.ScalarAsync(
            $"SELECT COUNT(*) FROM findings WHERE rule_key = '{FindingRuleKeys.ChallengeResolvedByRender}';")));
    }

    /// <summary>Marking resolution without a detection row is a no-op returning false.</summary>
    [Fact]
    public async Task MarkResolvedWithoutDetectionReturnsFalse()
    {
        var recorder = new WafDetectionRecorder(_db.Provider, new FixedTimeProvider());
        Assert.False(await recorder.MarkResolvedByRenderingAsync(1, 1, "{}"));
        Assert.Equal(0, await _db.CountRowsAsync("findings"));
    }

    /// <summary>Blocked waf-challenge URLs are excluded from thin-content and duplicate-title findings.</summary>
    [Fact]
    public async Task BlockedUrlsAreExcludedFromContentFindings()
    {
        var (evaluator, recorder) = await CreatePipelineAsync();
        await recorder.RecordChallengeAsync(1, 1, evaluator.Evaluate(WafResponses.CloudflareInterstitial()));

        var statusClass = (CrawlStatusClass)Convert.ToInt32(await _db.ScalarAsync("SELECT status_class FROM url_records WHERE id = 1;"));
        var blockReason = (string?)await _db.ScalarAsync("SELECT block_reason FROM url_records WHERE id = 1;");
        Assert.True(WafContentFindingExclusion.IsExcluded(statusClass, blockReason));

        // The untouched sibling URL remains eligible for content findings.
        var siblingStatus = (CrawlStatusClass)Convert.ToInt32(await _db.ScalarAsync("SELECT status_class FROM url_records WHERE id = 2;"));
        Assert.False(WafContentFindingExclusion.IsExcluded(siblingStatus, null));
        Assert.False(WafContentFindingExclusion.IsExcluded(CrawlStatusClass.Blocked, "other-reason"));
        Assert.False(WafContentFindingExclusion.IsExcluded(CrawlStatusClass.Success, WafDetectionRecorder.WafChallengeBlockReason));
    }

    private async Task<(WafChallengeEvaluator Evaluator, WafDetectionRecorder Recorder)> CreatePipelineAsync()
    {
        var evaluator = await WafChallengeEvaluator.CreateAsync(_db.Catalogue);
        var recorder = new WafDetectionRecorder(_db.Provider, new FixedTimeProvider());
        return (evaluator, recorder);
    }
}
