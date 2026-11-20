using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Fetching;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Redirect findings (CAP-B-049/050/051; CR-029/D-021): chain reporting, the
/// FOUR inappropriate-redirect cases as independent configuration toggles
/// (cross-host, temporary-status, non-indexable-target per CR-024,
/// https-downgrade), each emitting its locked <c>redirects.inappropriate-*</c>
/// key with case/hop/final-target evidence; loop and chain-cap violations with
/// prefix evidence. Absent/null <see cref="RedirectCheckSettings"/> = all ON.
/// </summary>
public sealed class RedirectFindingAnalyzerTests
{
    private const string A = "http://site.example/a";
    private const string B = "http://site.example/b";
    private const string C = "http://site.example/c";
    private readonly RedirectFindingAnalyzer _analyzer = new();

    [Fact]
    public void AnalyzeChain_ThreeHops_ChainAndTemporaryStatusCaseFindings()
    {
        var chain = new[]
        {
            new RedirectHop(0, A, B, 301),
            new RedirectHop(1, B, C, 302),
            new RedirectHop(2, C, "http://site.example/d", 301),
        };

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(1, 10, A, chain, "http://site.example/d", 200);

        Assert.Equal(2, findings.Count);
        FindingDto chainFinding = findings.Single(f => f.RuleKey == RedirectRuleKeys.Chain);
        Assert.Equal(FindingSeverity.Warning, chainFinding.Severity);
        Assert.Equal(AuditCategory.Indexability, chainFinding.Category);
        Assert.Equal(10, chainFinding.UrlId);
        using (var evidence = JsonDocument.Parse(chainFinding.EvidenceJson))
        {
            Assert.Equal(3, evidence.RootElement.GetProperty("hopCount").GetInt32());
            Assert.Equal("http://site.example/d", evidence.RootElement.GetProperty("finalUrl").GetString());
            Assert.Equal(3, evidence.RootElement.GetProperty("chain").GetArrayLength());
        }

        FindingDto temporary = findings.Single(f => f.RuleKey == RedirectRuleKeys.InappropriateTemporaryStatus);
        Assert.Equal(FindingSeverity.Warning, temporary.Severity);
        Assert.Equal(AuditCategory.Indexability, temporary.Category);
        using (var evidence = JsonDocument.Parse(temporary.EvidenceJson))
        {
            Assert.Equal("temporary-status", evidence.RootElement.GetProperty("case").GetString());
            Assert.Equal("http://site.example/d", evidence.RootElement.GetProperty("finalUrl").GetString());
            JsonElement hop = evidence.RootElement.GetProperty("triggeringHop");
            Assert.Equal(1, hop.GetProperty("hopOrdinal").GetInt32());
            Assert.Equal(B, hop.GetProperty("fromUrl").GetString());
            Assert.Equal(C, hop.GetProperty("toUrl").GetString());
            Assert.Equal(302, hop.GetProperty("statusCode").GetInt32());
            JsonElement reason = evidence.RootElement.GetProperty("reasons")[0];
            Assert.Equal(1, reason.GetProperty("hopOrdinal").GetInt32());
            Assert.Equal("temporary-status", reason.GetProperty("check").GetString());
            Assert.Equal(302, reason.GetProperty("statusCode").GetInt32());
        }

        // CR-029: the legacy aggregate key is never emitted anymore.
        Assert.DoesNotContain(findings, f => f.RuleKey == RedirectRuleKeys.Inappropriate);
    }

    [Fact]
    public void AnalyzeChain_SinglePermanentSameHostHop_NoFindings()
    {
        var chain = new[] { new RedirectHop(0, A, B, 301) };

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(1, 10, A, chain, B, 200);

        Assert.Empty(findings);
    }

    [Fact]
    public void AnalyzeChain_CrossHostHop_CrossHostCaseKeyWithHopAndTarget()
    {
        var chain = new[] { new RedirectHop(0, A, "http://other.example/landing", 301) };

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(1, 10, A, chain, "http://other.example/landing", 200);

        FindingDto finding = Assert.Single(findings);
        Assert.Equal(RedirectRuleKeys.InappropriateCrossHost, finding.RuleKey);
        Assert.Equal("redirects.inappropriate-cross-host", finding.RuleKey);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("cross-host", evidence.RootElement.GetProperty("case").GetString());
        Assert.Equal("http://other.example/landing", evidence.RootElement.GetProperty("finalUrl").GetString());
        Assert.Equal(0, evidence.RootElement.GetProperty("triggeringHop").GetProperty("hopOrdinal").GetInt32());
        Assert.Equal("cross-host", evidence.RootElement.GetProperty("reasons")[0].GetProperty("check").GetString());
    }

    [Fact]
    public void AnalyzeChain_HttpsToHttpHop_HttpsDowngradeCaseKey()
    {
        var chain = new[] { new RedirectHop(0, "https://site.example/a", "http://site.example/a", 301) };

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(
            1, 10, "https://site.example/a", chain, "http://site.example/a", 200);

        FindingDto finding = Assert.Single(findings);
        Assert.Equal(RedirectRuleKeys.InappropriateHttpsDowngrade, finding.RuleKey);
        Assert.Equal("redirects.inappropriate-https-downgrade", finding.RuleKey);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("https-downgrade", evidence.RootElement.GetProperty("case").GetString());
        Assert.Equal("http://site.example/a", evidence.RootElement.GetProperty("finalUrl").GetString());
    }

    [Fact]
    public void AnalyzeChain_HttpToHttpsHop_NoDowngradeFinding()
    {
        var chain = new[] { new RedirectHop(0, "http://site.example/a", "https://site.example/a", 301) };

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(
            1, 10, "http://site.example/a", chain, "https://site.example/a", 200);

        Assert.Empty(findings);
    }

    /// <summary>Case 3 with status-only facts: a non-200 final target fires.</summary>
    [Fact]
    public void AnalyzeChain_Non200FinalTarget_NonIndexableTargetCaseKey()
    {
        var chain = new[] { new RedirectHop(0, A, B, 301) };

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(1, 10, A, chain, B, 404);

        FindingDto finding = Assert.Single(findings);
        Assert.Equal(RedirectRuleKeys.InappropriateNonIndexableTarget, finding.RuleKey);
        Assert.Equal("redirects.inappropriate-non-indexable-target", finding.RuleKey);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("non-indexable-target", evidence.RootElement.GetProperty("case").GetString());
        Assert.Equal(B, evidence.RootElement.GetProperty("finalUrl").GetString());
        Assert.Equal(404, evidence.RootElement.GetProperty("triggeringHop").GetProperty("statusCode").GetInt32());
        Assert.Contains("CR-024", evidence.RootElement.GetProperty("reasons")[0].GetProperty("description").GetString());
    }

    /// <summary>Case 3 with full CR-024 facts: noindex, robots and
    /// canonical-elsewhere each disqualify an otherwise-200 target.</summary>
    [Theory]
    [InlineData(true, false, false, null)]
    [InlineData(false, true, false, null)]
    [InlineData(false, false, true, null)]
    [InlineData(false, false, false, "http://site.example/elsewhere")]
    public void AnalyzeChain_NonIndexableTargetFacts_FireCase3(
        bool metaNoindex, bool headerNoindex, bool robotsBlocked, string? canonicalElsewhere)
    {
        var chain = new[] { new RedirectHop(0, A, B, 301) };
        var facts = new RedirectTargetIndexabilityFacts(
            FinalStatusCode: 200,
            FinalUrl: B,
            RobotsBlocked: robotsBlocked,
            MetaRobotsNoindex: metaNoindex,
            HeaderRobotsNoindex: headerNoindex,
            CanonicalHrefs: canonicalElsewhere is null ? null : new[] { canonicalElsewhere });

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(1, 10, A, chain, B, 200, targetFacts: facts);

        Assert.Equal(RedirectRuleKeys.InappropriateNonIndexableTarget, Assert.Single(findings).RuleKey);
    }

    /// <summary>Case 3 parity with CR-024: a clean search-indexable target
    /// (200, robots allowed, no noindex, self canonical) stays silent.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(B)]
    [InlineData("http://site.example/b/")]
    public void AnalyzeChain_IndexableTarget_Case3Silent(string? selfCanonical)
    {
        var chain = new[] { new RedirectHop(0, A, B, 301) };
        var facts = new RedirectTargetIndexabilityFacts(
            FinalStatusCode: 200,
            FinalUrl: B,
            CanonicalHrefs: selfCanonical is null ? null : new[] { selfCanonical });

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(1, 10, A, chain, B, 200, targetFacts: facts);

        Assert.Empty(findings);
    }

    /// <summary>Without any terminal facts case 3 cannot be judged and stays
    /// silent (never guessed); the hop cases still fire.</summary>
    [Fact]
    public void AnalyzeChain_NoTerminalFacts_Case3Silent()
    {
        var chain = new[] { new RedirectHop(0, A, "http://other.example/b", 302) };

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(1, 10, A, chain);

        Assert.Equal(2, findings.Count);
        Assert.DoesNotContain(findings, f => f.RuleKey == RedirectRuleKeys.InappropriateNonIndexableTarget);
    }

    /// <summary>CR-029 component proof: each toggle flips exactly its own case
    /// finding on/off while the other cases stay on. One hop fires cross-host,
    /// temporary-status and https-downgrade; the 404 target fires case 3.</summary>
    [Fact]
    public void AnalyzeChain_EachToggleFlipsItsOwnCaseFinding()
    {
        var chain = new[] { new RedirectHop(0, "https://site.example/a", "http://other.example/b", 302) };
        const string target = "http://other.example/b";
        var allCases = new[]
        {
            RedirectRuleKeys.InappropriateCrossHost,
            RedirectRuleKeys.InappropriateTemporaryStatus,
            RedirectRuleKeys.InappropriateNonIndexableTarget,
            RedirectRuleKeys.InappropriateHttpsDowngrade,
        };

        // All four on (default): every case fires.
        var allOn = _analyzer.AnalyzeChain(1, 10, "https://site.example/a", chain, target, 404);
        Assert.Equal(4, allOn.Count);
        foreach (var key in allCases)
        {
            Assert.Contains(allOn, f => f.RuleKey == key);
        }

        // Each single-case disable removes exactly that case's finding.
        var toggles = new (RedirectInappropriatenessChecks Flag, string Key)[]
        {
            (RedirectInappropriatenessChecks.CrossHost, RedirectRuleKeys.InappropriateCrossHost),
            (RedirectInappropriatenessChecks.TemporaryStatus, RedirectRuleKeys.InappropriateTemporaryStatus),
            (RedirectInappropriatenessChecks.NonIndexableTarget, RedirectRuleKeys.InappropriateNonIndexableTarget),
            (RedirectInappropriatenessChecks.HttpsDowngrade, RedirectRuleKeys.InappropriateHttpsDowngrade),
        };
        foreach (var (flag, key) in toggles)
        {
            var findings = _analyzer.AnalyzeChain(1, 10, "https://site.example/a", chain, target, 404, RedirectInappropriatenessChecks.All & ~flag);
            Assert.Equal(3, findings.Count);
            Assert.DoesNotContain(findings, f => f.RuleKey == key);
            foreach (var other in allCases.Where(k => k != key))
            {
                Assert.Contains(findings, f => f.RuleKey == other);
            }
        }
    }

    /// <summary>The locked configuration document maps one-for-one onto the
    /// check flags; absent/null = all four ON (CR-029 default).</summary>
    [Fact]
    public void FromSettings_MapsDocumentAndNullDefaultsAllOn()
    {
        Assert.Equal(RedirectInappropriatenessChecks.All, RedirectInappropriatenessChecksExtensions.FromSettings(null));
        Assert.Equal(RedirectInappropriatenessChecks.All, RedirectInappropriatenessChecksExtensions.FromSettings(new RedirectCheckSettings()));
        Assert.Equal(RedirectInappropriatenessChecks.All, RedirectInappropriatenessChecksExtensions.FromSettings(RedirectCheckSettings.AllEnabled));
        Assert.Equal(RedirectInappropriatenessChecks.None, RedirectInappropriatenessChecksExtensions.FromSettings(RedirectCheckSettings.AllDisabled));

        var onlyCrossHost = new RedirectCheckSettings(CrossHost: true, TemporaryStatus: false, NonIndexableTarget: false, HttpsDowngrade: false);
        Assert.Equal(RedirectInappropriatenessChecks.CrossHost, RedirectInappropriatenessChecksExtensions.FromSettings(onlyCrossHost));

        var noDowngrade = new RedirectCheckSettings(HttpsDowngrade: false);
        Assert.Equal(
            RedirectInappropriatenessChecks.CrossHost | RedirectInappropriatenessChecks.TemporaryStatus | RedirectInappropriatenessChecks.NonIndexableTarget,
            RedirectInappropriatenessChecksExtensions.FromSettings(noDowngrade));
    }

    /// <summary>A null configuration document behaves as all-ON end to end.</summary>
    [Fact]
    public void AnalyzeChain_NullSettingsDocument_AllCasesOn()
    {
        var chain = new[] { new RedirectHop(0, "https://site.example/a", "http://other.example/b", 302) };
        RedirectCheckSettings? settings = null;

        var findings = _analyzer.AnalyzeChain(
            1, 10, "https://site.example/a", chain, "http://other.example/b", 404,
            RedirectInappropriatenessChecksExtensions.FromSettings(settings));

        Assert.Equal(4, findings.Count);
    }

    [Fact]
    public void AnalyzeChain_ChecksNone_NoInappropriateButChainStillReported()
    {
        var chain = new[]
        {
            new RedirectHop(0, A, "http://other.example/b", 302),
            new RedirectHop(1, "http://other.example/b", C, 302),
        };

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(1, 10, A, chain, C, 200, RedirectInappropriatenessChecks.None);

        Assert.Equal(RedirectRuleKeys.Chain, Assert.Single(findings).RuleKey);
    }

    [Fact]
    public void AnalyzeChain_EmptyChain_NoFindings()
    {
        Assert.Empty(_analyzer.AnalyzeChain(1, 10, A, Array.Empty<RedirectHop>()));
    }

    [Fact]
    public void AnalyzeViolation_Loop_ErrorWithPrefixEvidence()
    {
        var partial = new[] { new RedirectHop(0, A, B, 301) };
        var violation = new RedirectChainException(RedirectChainViolation.Loop, "loop detected");

        FindingDto finding = _analyzer.AnalyzeViolation(1, 10, A, violation, partial);

        Assert.Equal(RedirectRuleKeys.Loop, finding.RuleKey);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("loop", evidence.RootElement.GetProperty("violation").GetString());
        Assert.Equal(1, evidence.RootElement.GetProperty("hopCount").GetInt32());
        Assert.Equal(A, evidence.RootElement.GetProperty("chain")[0].GetProperty("url").GetString());
    }

    [Fact]
    public void AnalyzeViolation_ChainCap_ErrorChainKey()
    {
        var violation = new RedirectChainException(RedirectChainViolation.ChainCapExceeded, "cap exceeded");

        FindingDto finding = _analyzer.AnalyzeViolation(1, 10, A, violation, new[] { new RedirectHop(0, A, B, 301) });

        Assert.Equal(RedirectRuleKeys.Chain, finding.RuleKey);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        Assert.Contains("\"violation\":\"chain-cap-exceeded\"", finding.EvidenceJson);
    }

    [Fact]
    public void AnalyzeFetchFailure_RedirectKinds_Findings()
    {
        FindingDto? loop = _analyzer.AnalyzeFetchFailure(1, 10, A,
            new FetchFailedException(FetchFailureKind.RedirectLoop, "loop"), Array.Empty<RedirectHop>());
        Assert.Equal(RedirectRuleKeys.Loop, loop!.RuleKey);

        FindingDto? cap = _analyzer.AnalyzeFetchFailure(1, 10, A,
            new FetchFailedException(FetchFailureKind.RedirectChainExceeded, "cap"), Array.Empty<RedirectHop>());
        Assert.Equal(RedirectRuleKeys.Chain, cap!.RuleKey);

        Assert.Null(_analyzer.AnalyzeFetchFailure(1, 10, A,
            new FetchFailedException(FetchFailureKind.Network, "down"), Array.Empty<RedirectHop>()));
    }
}
