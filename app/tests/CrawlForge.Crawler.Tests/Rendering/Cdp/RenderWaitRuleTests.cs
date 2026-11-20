using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Wait-rule component proof (§5.6.2, CR-009): DOM + network quiet 500 ms
/// under a 20 s hard cap, both configurable; no fixed delays; the hard cap
/// captures the current state instead of failing.
/// </summary>
public sealed class RenderWaitRuleTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var rule = new RenderWaitRule();
        Assert.Equal(500, rule.QuietMs);
        Assert.Equal(20_000, rule.HardCapMs);
    }

    [Fact]
    public void Settled_WhenDomAndNetworkQuietForThePeriod()
    {
        var rule = new RenderWaitRule(quietMs: 500, hardCapMs: 20_000);
        var decision = rule.Evaluate(
            domReady: true,
            domQuietAgeMs: 500,
            inFlightNonStreaming: 0,
            networkQuietAgeMs: 500,
            elapsedMs: 1200);
        Assert.Equal(RenderWaitDecision.Settled, decision);
    }

    [Theory]
    [InlineData(false, 600, 0, 600)] // DOM not ready
    [InlineData(true, 499, 0, 600)] // DOM mutated too recently
    [InlineData(true, 600, 1, 600)] // a non-streaming request still in flight
    [InlineData(true, 600, 0, 499)] // network active too recently
    public void Waiting_WhenAnyQuietConditionUnmet(bool domReady, long domQuiet, int inFlight, long networkQuiet)
    {
        var rule = new RenderWaitRule(quietMs: 500, hardCapMs: 20_000);
        var decision = rule.Evaluate(domReady, domQuiet, inFlight, networkQuiet, elapsedMs: 1000);
        Assert.Equal(RenderWaitDecision.Waiting, decision);
    }

    [Fact]
    public void HardCapReached_WhenNotSettledAndCapElapsed()
    {
        var rule = new RenderWaitRule(quietMs: 500, hardCapMs: 20_000);
        var decision = rule.Evaluate(
            domReady: true,
            domQuietAgeMs: 0, // page mutates forever
            inFlightNonStreaming: 0,
            networkQuietAgeMs: 600,
            elapsedMs: 20_000);
        Assert.Equal(RenderWaitDecision.HardCapReached, decision);
    }

    [Fact]
    public void SettledWins_ExactlyAtTheCap()
    {
        var rule = new RenderWaitRule(quietMs: 500, hardCapMs: 20_000);
        var decision = rule.Evaluate(true, 500, 0, 500, elapsedMs: 20_000);
        Assert.Equal(RenderWaitDecision.Settled, decision);
    }

    [Fact]
    public void Knobs_AreConfigurable()
    {
        var rule = new RenderWaitRule(quietMs: 150, hardCapMs: 1500);
        Assert.Equal(RenderWaitDecision.Settled, rule.Evaluate(true, 150, 0, 150, elapsedMs: 400));
        Assert.Equal(RenderWaitDecision.HardCapReached, rule.Evaluate(true, 0, 0, 150, elapsedMs: 1500));
    }

    [Fact]
    public void InvalidKnobs_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderWaitRule(quietMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderWaitRule(hardCapMs: 0));
    }
}
