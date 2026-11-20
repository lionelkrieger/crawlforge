using System.Globalization;
using CrawlForge.Audit.ContentQuality;
using CrawlForge.Audit.Scoring;
using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.Scoring.Support;
using CrawlForge.Audit.Tests.StructuredData.Support;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Audit.Tests.Scoring;

/// <summary>
/// WP-10-A1/D-021 wire-conformance contract test (dispatch contract_tests):
/// category, WAF-class and canonical-bit values on finding, score and URL wire
/// payloads use the locked WP-10-A1 members. The single wire authority is
/// <see cref="EnumWireNames"/> (the Api layer's ApiEnumConverter delegates to
/// it — ApiJsonOptions.cs), so conformance is proven here against that
/// authority plus the REAL producer → persistence → scoring → read-back path:
/// findings stamped by the real analyzers persist their category verbatim
/// (FindingEmitter convention), scores are enum-driven over all 14 categories,
/// and the recorder stores category ints that map back to the locked tokens.
/// </summary>
public class AuditScoreWireConformanceTests
{
    // ---- Locked vocabulary (WP-10-A1 members) -------------------------------

    /// <summary>AuditCategory carries 14 members; the three D-021 additions
    /// have the locked values and lowercase wire tokens.</summary>
    [Fact]
    public void AuditCategory_FourteenMembers_WithLockedNewTokens()
    {
        var members = Enum.GetValues<AuditCategory>();
        Assert.Equal(14, members.Length);
        Assert.Equal(11, (int)AuditCategory.Images);
        Assert.Equal(12, (int)AuditCategory.SecurityAndProtocol);
        Assert.Equal(13, (int)AuditCategory.Internationalization);

        Assert.Equal("images", EnumWireNames.ToWireValue(AuditCategory.Images));
        Assert.Equal("securityandprotocol", EnumWireNames.ToWireValue(AuditCategory.SecurityAndProtocol));
        Assert.Equal("internationalization", EnumWireNames.ToWireValue(AuditCategory.Internationalization));

        foreach (var category in members)
        {
            var wire = EnumWireNames.ToWireValue(category);
            Assert.Equal(wire.ToLowerInvariant(), wire);
            Assert.True(EnumWireNames.TryParse<AuditCategory>(wire, out var parsed));
            Assert.Equal(category, parsed);
        }
    }

    /// <summary>WafChallengeClass carries the five spec classes; new wires are
    /// hyphenated, existing interstitial/botblock wires stay locked.</summary>
    [Fact]
    public void WafChallengeClass_FiveClasses_WithLockedWireTokens()
    {
        Assert.Equal(6, Enum.GetValues<WafChallengeClass>().Length); // five + Unknown
        Assert.Equal("managed-challenge", EnumWireNames.ToWireValue(WafChallengeClass.ManagedChallenge));
        Assert.Equal("rate-limit-soft-block", EnumWireNames.ToWireValue(WafChallengeClass.RateLimitSoftBlock));
        Assert.Equal("captcha", EnumWireNames.ToWireValue(WafChallengeClass.Captcha));
        Assert.Equal("interstitial", EnumWireNames.ToWireValue(WafChallengeClass.Interstitial));
        Assert.Equal("botblock", EnumWireNames.ToWireValue(WafChallengeClass.BotBlock));

        foreach (var challengeClass in Enum.GetValues<WafChallengeClass>())
        {
            Assert.True(EnumWireNames.TryParse<WafChallengeClass>(EnumWireNames.ToWireValue(challengeClass), out var parsed));
            Assert.Equal(challengeClass, parsed);
        }
    }

    /// <summary>The canonical-elsewhere observation is the locked bit 16 and
    /// composes with the phase-09 flags.</summary>
    [Fact]
    public void IndexabilityFlags_CanonicalElsewhere_IsLockedBit16()
    {
        Assert.Equal(16, (int)IndexabilityFlags.CanonicalElsewhere);
        var composed = IndexabilityFlags.Noindex | IndexabilityFlags.CanonicalElsewhere;
        Assert.True(composed.HasFlag(IndexabilityFlags.CanonicalElsewhere));
        Assert.True(composed.HasFlag(IndexabilityFlags.Noindex));
        Assert.Equal(17, (int)composed);
    }

    // ---- Enum-driven scoring over all 14 categories -------------------------

    /// <summary>Scoring is enum-driven: an absent enabledCategories snapshot
    /// scores ALL 14 categories (including the three D-021 additions) plus the
    /// overall row — no hardcoded category list.</summary>
    [Fact]
    public async Task Scoring_EnumDriven_ScoresAllFourteenCategories()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var service = new AuditScoringService(stack.Provider);

        Assert.Equal(Enum.GetValues<AuditCategory>(), AuditScoringService.ParseEnabledCategories("{}"));
        Assert.Equal(Enum.GetValues<AuditCategory>(), AuditScoringService.ParseEnabledCategories(null));

        var drafts = await service.ComputeAsync(stack.AuditId);

        Assert.Equal(15, drafts.Count); // 14 categories + overall
        foreach (var category in Enum.GetValues<AuditCategory>())
        {
            var draft = Assert.Single(drafts, d => d.Category == category);
            Assert.Equal(100.0, draft.Score); // no findings yet
        }

        Assert.Null(drafts[^1].Category);
        Assert.Equal(100.0, drafts[^1].Score);
    }

    /// <summary>The enabledCategories convention accepts the NEW members by
    /// name and by numeric value; disabled categories contribute no row.</summary>
    [Fact]
    public async Task EnabledCategories_NewMembersByNameAndNumber()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        await stack.SetSnapshotAsync("""{"enabledCategories":["images","internationalization",12]}""");
        var service = new AuditScoringService(stack.Provider);

        var drafts = await service.ComputeAsync(stack.AuditId);

        Assert.Equal(4, drafts.Count);
        Assert.Contains(drafts, d => d.Category == AuditCategory.Images);
        Assert.Contains(drafts, d => d.Category == AuditCategory.SecurityAndProtocol);
        Assert.Contains(drafts, d => d.Category == AuditCategory.Internationalization);
        Assert.DoesNotContain(drafts, d => d.Category == AuditCategory.StructuredData);
    }

    // ---- Real producer → finding/score/URL wire payloads --------------------

    /// <summary>End-to-end: REAL producer findings (image-alt under Images,
    /// hreflang under Internationalization) persist their stamped category,
    /// deduct from the matching category score, and the recorder's stored
    /// category ints map back to the locked wire tokens. A SecurityAndProtocol
    /// finding is scored too (no producer exists yet — the category is
    /// wire-ready and enum-scored; future producers stamp it).</summary>
    [Fact]
    public async Task RealProducerFindings_ScoreAndPersistUnderNewCategories()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var url = await stack.AddUrlAsync(stack.AuditId, "https://example.test/images.html");

        // Real image producer: missing alt → accessibility.image-alt / Images / Error.
        var imageAnalyzer = new ImageRuleAnalyzer();
        var imageContext = FixtureAuditContext.Create(
            "<html><body><img src=\"/assets/logo.png\"></body></html>",
            url: "https://example.test/images.html", urlId: url.Id, auditId: stack.AuditId);
        var imageFindings = await imageAnalyzer.AnalyzeAsync(imageContext);
        var alt = Assert.Single(imageFindings, f => f.RuleKey == FindingRuleKeys_ImageAlt);
        Assert.Equal(AuditCategory.Images, alt.Category);
        Assert.Equal(FindingSeverity.Error, alt.Severity);
        await PersistDraftAsync(stack, alt);

        // Real hreflang producer: invalid code → Internationalization / Warning.
        var hreflangAnalyzer = new HreflangAnalyzer();
        var hreflangContext = FixtureAuditContext.Create(
            "<html><head><link rel=\"alternate\" hreflang=\"xx\" href=\"https://example.test/page\" /></head><body></body></html>",
            url: "https://example.test/images.html", urlId: url.Id, auditId: stack.AuditId);
        var hreflangFindings = await hreflangAnalyzer.AnalyzeAsync(hreflangContext);
        var invalidCode = Assert.Single(hreflangFindings, f => f.RuleKey == StructuredDataRuleKeys.HreflangInvalidCode);
        Assert.Equal(AuditCategory.Internationalization, invalidCode.Category);
        await PersistDraftAsync(stack, invalidCode);

        // SecurityAndProtocol: wire-ready category, scored like every other.
        await stack.AddFindingAsync(stack.AuditId, "security.cookie-attributes", AuditCategory.SecurityAndProtocol, FindingSeverity.Warning, url.Id);

        var service = new AuditScoringService(stack.Provider);
        var drafts = await service.ComputeAsync(stack.AuditId);

        // Capacity = 4 indexable URLs × 10 = 40.
        Assert.Equal(75.0, Assert.Single(drafts, d => d.Category == AuditCategory.Images).Score);        // 1 Error = 10/40
        Assert.Equal(92.5, Assert.Single(drafts, d => d.Category == AuditCategory.Internationalization).Score); // 1 Warning = 3/40
        Assert.Equal(92.5, Assert.Single(drafts, d => d.Category == AuditCategory.SecurityAndProtocol).Score);

        // Recorder persists category ints; read-back maps to locked tokens.
        var recorder = new AuditScoreRecorder(stack.Provider);
        Assert.Equal(15, await recorder.RecordAsync(stack.AuditId, drafts));
        var stored = await recorder.GetScoresAsync(stack.AuditId);
        Assert.Equal(15, stored.Count);
        foreach (var score in stored.Where(s => s.Category is not null))
        {
            var wire = EnumWireNames.ToWireValue(score.Category!.Value);
            Assert.True(EnumWireNames.TryParse<AuditCategory>(wire, out var roundTrip));
            Assert.Equal(score.Category, roundTrip);
        }

        Assert.Equal("images", EnumWireNames.ToWireValue(Assert.Single(stored, s => s.Category == AuditCategory.Images).Category!.Value));
        Assert.Equal(75.0, Assert.Single(stored, s => s.Category == AuditCategory.Images).Score);
        Assert.Equal(1, Assert.Single(stored, s => s.Category == AuditCategory.Images).ErrorCount);
    }

    /// <summary>URL wire payload: the CanonicalElsewhere bit survives
    /// persistence and read-back on the URL record (CR-024/D-021).</summary>
    [Fact]
    public async Task UrlRecord_CanonicalElsewhereBit_RoundTrips()
    {
        await using var stack = await ScoringRealStack.CreateAsync();
        var repository = new SqlRepository<UrlRecordDto>(stack.Provider);
        var created = await repository.CreateAsync(new UrlRecordDto(
            0, stack.AuditId, "https://example.test/canonical-elsewhere", "example.test", "/canonical-elsewhere", 0,
            DiscoverySource.Seed, CrawlStatusClass.Success, 200, null, "text/html", 0, null,
            RobotsState.Allowed, IndexabilityFlags.CanonicalElsewhere, false));

        var readBack = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(readBack);
        Assert.True(readBack!.IndexabilityFlags.HasFlag(IndexabilityFlags.CanonicalElsewhere));
        Assert.False(readBack.IsIndexable);
        Assert.Equal(16, (int)readBack.IndexabilityFlags & 16);
    }

    /// <summary>Redirect case findings (CR-029 locked keys) carry the
    /// Indexability category on the finding wire payload.</summary>
    [Fact]
    public void RedirectCaseKeys_AreLockedFindingRuleKeys()
    {
        Assert.Equal("redirects.inappropriate-cross-host", FindingRuleKeys.RedirectsInappropriateCrossHost);
        Assert.Equal("redirects.inappropriate-temporary-status", FindingRuleKeys.RedirectsInappropriateTemporaryStatus);
        Assert.Equal("redirects.inappropriate-non-indexable-target", FindingRuleKeys.RedirectsInappropriateNonIndexableTarget);
        Assert.Equal("redirects.inappropriate-https-downgrade", FindingRuleKeys.RedirectsInappropriateHttpsDowngrade);
        Assert.Equal("structured-data.deprecated-term", FindingRuleKeys.StructuredDataDeprecatedTerm);
    }

    // The exact locked alt key string (kept verbatim per coordinator ruling;
    // not a FindingRuleKeys member — stability wins over promotion).
    private const string FindingRuleKeys_ImageAlt = "accessibility.image-alt";

    private static async Task PersistDraftAsync(ScoringRealStack stack, FindingDraft draft) =>
        await new SqlRepository<FindingDto>(stack.Provider).CreateAsync(new FindingDto(
            0, stack.AuditId, draft.RuleKey, draft.Category, draft.Severity, draft.UrlId,
            draft.EvidenceJson, draft.RecommendationKey, false));
}
