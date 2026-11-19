using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Politeness;
using Xunit;

namespace CrawlForge.Crawler.Tests.Politeness;

/// <summary>
/// Cookieless redirect-loop diagnostic (FS-CRAWL-001 §5.11, fixture trap
/// cookie-gate cookieless mode): loop detection over the hop chain and the
/// finding gate (cookieless + observed Set-Cookie + actual loop).
/// </summary>
public class CookielessRedirectLoopDiagnosticTests
{
    private static RedirectHop Hop(int ordinal, string from, string to) => new(ordinal, from, to, 302);

    /// <summary>A repeated FromUrl is the loop anchor.</summary>
    [Fact]
    public void RepeatedFromUrlIsDetected()
    {
        var chain = new[]
        {
            Hop(0, "http://host/cookie-gate/", "http://host/cookie-gate/"),
            Hop(1, "http://host/cookie-gate/", "http://host/cookie-gate/"),
        };
        Assert.True(CookielessRedirectLoopDiagnostic.TryFindLoop(chain, out var loopUrl));
        Assert.Equal("http://host/cookie-gate/", loopUrl);
    }

    /// <summary>A terminating chain has no loop.</summary>
    [Fact]
    public void TerminatingChainHasNoLoop()
    {
        var chain = new[]
        {
            Hop(0, "http://host/a", "http://host/b"),
            Hop(1, "http://host/b", "http://host/c"),
        };
        Assert.False(CookielessRedirectLoopDiagnostic.TryFindLoop(chain, out var loopUrl));
        Assert.Null(loopUrl);
        Assert.False(CookielessRedirectLoopDiagnostic.TryFindLoop([], out _));
    }

    /// <summary>The finding requires cookieless mode, observed Set-Cookie and a loop.</summary>
    [Fact]
    public void FindingGateRequiresAllThreeConditions()
    {
        var chain = new[]
        {
            Hop(0, "http://host/gate/", "http://host/gate/"),
            Hop(1, "http://host/gate/", "http://host/gate/"),
        };

        // All conditions: Warning finding with guidance evidence.
        var finding = CookielessRedirectLoopDiagnostic.Create(5, "http://host/gate/", chain, ["gate"], cookieJarEnabled: false);
        Assert.NotNull(finding);
        Assert.Equal("crawl.cookieless-redirect-loop", finding!.RuleKey);
        Assert.Equal(AuditCategory.Indexability, finding.Category);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(5, finding.UrlId);
        Assert.Contains("cookie jar", finding.EvidenceJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gate", finding.EvidenceJson);

        // Jar enabled: the jar resolves the gate; no finding.
        Assert.Null(CookielessRedirectLoopDiagnostic.Create(5, "http://host/gate/", chain, ["gate"], cookieJarEnabled: true));
        // No Set-Cookie observed: not a cookie gate.
        Assert.Null(CookielessRedirectLoopDiagnostic.Create(5, "http://host/gate/", chain, [], cookieJarEnabled: false));
        // No loop: nothing to diagnose.
        Assert.Null(CookielessRedirectLoopDiagnostic.Create(5, "http://host/gate/", [], ["gate"], cookieJarEnabled: false));
    }
}
