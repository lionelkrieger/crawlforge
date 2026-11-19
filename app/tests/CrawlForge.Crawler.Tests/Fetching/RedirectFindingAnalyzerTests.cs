using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Fetching;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Redirect findings (CAP-B-049/050/051): chain reporting, inappropriate
/// redirects (configurable check set — documented rule definition pending
/// owner confirmation), loop and chain-cap violations with prefix evidence.
/// </summary>
public sealed class RedirectFindingAnalyzerTests
{
    private const string A = "http://site.example/a";
    private const string B = "http://site.example/b";
    private const string C = "http://site.example/c";
    private readonly RedirectFindingAnalyzer _analyzer = new();

    [Fact]
    public void AnalyzeChain_ThreeHops_ChainAndInappropriateFindings()
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

        FindingDto inappropriate = findings.Single(f => f.RuleKey == RedirectRuleKeys.Inappropriate);
        Assert.Equal(FindingSeverity.Warning, inappropriate.Severity);
        using (var evidence = JsonDocument.Parse(inappropriate.EvidenceJson))
        {
            JsonElement reason = evidence.RootElement.GetProperty("reasons")[0];
            Assert.Equal(1, reason.GetProperty("hopOrdinal").GetInt32());
            Assert.Equal("temporary-status", reason.GetProperty("check").GetString());
            Assert.Equal(302, reason.GetProperty("statusCode").GetInt32());
        }
    }

    [Fact]
    public void AnalyzeChain_SinglePermanentSameHostHop_NoFindings()
    {
        var chain = new[] { new RedirectHop(0, A, B, 301) };

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(1, 10, A, chain, B, 200);

        Assert.Empty(findings);
    }

    [Fact]
    public void AnalyzeChain_CrossHostHop_InappropriateWithCrossHostReason()
    {
        var chain = new[] { new RedirectHop(0, A, "http://other.example/landing", 301) };

        IReadOnlyList<FindingDto> findings = _analyzer.AnalyzeChain(1, 10, A, chain, "http://other.example/landing", 200);

        FindingDto finding = Assert.Single(findings);
        Assert.Equal(RedirectRuleKeys.Inappropriate, finding.RuleKey);
        using var evidence = JsonDocument.Parse(finding.EvidenceJson);
        Assert.Equal("cross-host", evidence.RootElement.GetProperty("reasons")[0].GetProperty("check").GetString());
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
