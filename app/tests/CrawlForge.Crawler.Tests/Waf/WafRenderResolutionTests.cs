using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Waf;
using Xunit;

namespace CrawlForge.Crawler.Tests.Waf;

/// <summary>
/// Render-resolution policy: challenge-resolved-by-render and
/// suspected-challenge notices, persistent challenges, and the no-notice
/// paths. Pure decision logic — detection and reporting only.
/// </summary>
public class WafRenderResolutionTests
{
    private static readonly WafEvaluationResult Challenge = new(
        IsChallenge: true,
        WafProvider.Cloudflare,
        WafChallengeClass.Interstitial,
        MatchedSignatureKeys: ["cloudflare-interstitial"],
        EvidenceJson: "{}");

    private static readonly WafEvaluationResult Clean = new(
        IsChallenge: false,
        WafProvider.Unknown,
        WafChallengeClass.Unknown,
        MatchedSignatureKeys: [],
        EvidenceJson: "{}");

    private static readonly WafEvaluationResult Suspected = new(
        IsChallenge: false,
        WafProvider.Unknown,
        WafChallengeClass.Unknown,
        MatchedSignatureKeys: ["generic-challenge"],
        EvidenceJson: "{}");

    /// <summary>A challenge that disappears after rendering produces the resolved-by-render notice.</summary>
    [Fact]
    public void ChallengeResolvedByRenderProducesNotice()
    {
        var resolution = WafRenderResolutionPolicy.Resolve(Challenge, Clean);
        Assert.Equal(WafRenderOutcome.ResolvedByRender, resolution.Outcome);
        Assert.Equal(FindingRuleKeys.ChallengeResolvedByRender, resolution.NoticeRuleKey);
    }

    /// <summary>A challenge that persists after rendering stays blocked with no notice.</summary>
    [Fact]
    public void PersistentChallengeStaysBlocked()
    {
        var resolution = WafRenderResolutionPolicy.Resolve(Challenge, Challenge);
        Assert.Equal(WafRenderOutcome.StillBlocked, resolution.Outcome);
        Assert.Null(resolution.NoticeRuleKey);
    }

    /// <summary>When rendering was not attempted the challenge stays blocked.</summary>
    [Fact]
    public void NoRenderingStaysBlocked()
    {
        var resolution = WafRenderResolutionPolicy.Resolve(Challenge, rendered: null);
        Assert.Equal(WafRenderOutcome.StillBlocked, resolution.Outcome);
        Assert.Null(resolution.NoticeRuleKey);
    }

    /// <summary>A sub-threshold hit produces the suspected-challenge notice.</summary>
    [Fact]
    public void SuspectedHitProducesNotice()
    {
        var resolution = WafRenderResolutionPolicy.Resolve(Suspected, Clean);
        Assert.Equal(WafRenderOutcome.SuspectedOnly, resolution.Outcome);
        Assert.Equal(FindingRuleKeys.SuspectedChallenge, resolution.NoticeRuleKey);
    }

    /// <summary>A clean response produces no outcome notice.</summary>
    [Fact]
    public void CleanResponseHasNoNotice()
    {
        var resolution = WafRenderResolutionPolicy.Resolve(Clean, Clean);
        Assert.Equal(WafRenderOutcome.NoChallenge, resolution.Outcome);
        Assert.Null(resolution.NoticeRuleKey);
    }
}
