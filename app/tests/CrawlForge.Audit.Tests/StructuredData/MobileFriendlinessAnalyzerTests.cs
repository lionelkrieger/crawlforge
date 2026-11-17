using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.StructuredData.Support;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.StructuredData;

/// <summary>
/// Mobile-friendliness suite (FS-CRAWL-001 §7.14): viewport validity, font
/// legibility, responsive signals, render-dependent tap-target/fit checks, and
/// the render-required notice when rendering is off.
/// </summary>
public class MobileFriendlinessAnalyzerTests
{
    private readonly MobileFriendlinessAnalyzer _analyzer = new();

    private const string GoodViewport = """<meta name="viewport" content="width=device-width, initial-scale=1" />""";

    private static string Page(string head, string body = "") =>
        $"""<html lang="en"><head>{head}</head><body>{body}</body></html>""";

    [Fact]
    public async Task ViewportMissing_ProducesWarning()
    {
        var context = StructuredDataTestContext.Create(Page(""));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileViewportMissing && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public async Task ValidViewport_ProducesNoViewportFindings()
    {
        var context = StructuredDataTestContext.Create(Page(GoodViewport + """<style>@media (max-width: 600px) { body { margin: 0; } }</style>"""));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.DoesNotContain(findings, f => f.RuleKey is StructuredDataRuleKeys.MobileViewportMissing or StructuredDataRuleKeys.MobileViewportInvalid);
        Assert.DoesNotContain(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileResponsiveSignalsMissing);
    }

    [Theory]
    [InlineData("""<meta name="viewport" content="width=device-width, user-scalable=no" />""")]
    [InlineData("""<meta name="viewport" content="width=device-width, maximum-scale=1" />""")]
    [InlineData("""<meta name="viewport" content="width=1024" />""")]
    [InlineData("""<meta name="viewport" content="width=device-width, initial-scale=0" />""")]
    public async Task InvalidViewport_ProducesWarning(string viewport)
    {
        var context = StructuredDataTestContext.Create(Page(viewport));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileViewportInvalid);
    }

    [Fact]
    public async Task SmallInlineFont_ProducesWarning()
    {
        var context = StructuredDataTestContext.Create(
            Page(GoodViewport, """<p style="font-size: 10px">tiny</p><p style="font-size: 16px">fine</p>"""));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileFontTooSmall);
    }

    [Fact]
    public async Task SmallFontInStyleBlock_ProducesWarning()
    {
        var context = StructuredDataTestContext.Create(
            Page(GoodViewport + "<style>.small { font-size: 9px; }</style>"));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileFontTooSmall);
    }

    [Fact]
    public async Task NoResponsiveSignals_ProducesNotice()
    {
        var context = StructuredDataTestContext.Create(Page(GoodViewport));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileResponsiveSignalsMissing && f.Severity == FindingSeverity.Notice);
    }

    [Fact]
    public async Task SrcsetCountsAsResponsiveSignal()
    {
        var context = StructuredDataTestContext.Create(
            Page(GoodViewport, """<img src="a.jpg" srcset="a-2x.jpg 2x" sizes="100vw" />"""));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.DoesNotContain(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileResponsiveSignalsMissing);
    }

    [Fact]
    public async Task RenderingOff_FlagsRenderDependentChecks()
    {
        var context = StructuredDataTestContext.Create(Page(GoodViewport), renderMode: RenderMode.None);

        var findings = await _analyzer.AnalyzeAsync(context);

        var notice = Assert.Single(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileRenderRequired);
        Assert.Equal(FindingSeverity.Notice, notice.Severity);
        Assert.Contains("tap-target-sizing", notice.EvidenceJson);
        Assert.Contains("content-to-viewport-fit", notice.EvidenceJson);
    }

    [Fact]
    public async Task RenderingOff_SkipsTapTargetChecksEvenWithSmallTargets()
    {
        var context = StructuredDataTestContext.Create(
            Page(GoodViewport, """<a href="/x" style="width: 20px; height: 20px">x</a>"""));

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.DoesNotContain(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileTapTargetTooSmall);
    }

    [Fact]
    public async Task RenderedSmallTapTarget_ProducesWarning()
    {
        var rendered = Page(GoodViewport, """<a href="/x" style="width: 20px; height: 20px">x</a><button style="width: 60px; height: 60px">ok</button>""");
        var context = StructuredDataTestContext.Create(Page(GoodViewport), renderedHtml: rendered, renderMode: RenderMode.Always);

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileTapTargetTooSmall);
        Assert.DoesNotContain(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileRenderRequired);
    }

    [Fact]
    public async Task RenderedCloseTapTargets_ProduceSpacingWarning()
    {
        var rendered = Page(GoodViewport,
            """<div><button style="width: 60px; height: 60px; margin: 2px">a</button><button style="width: 60px; height: 60px; margin: 2px">b</button></div>""");
        var context = StructuredDataTestContext.Create(Page(GoodViewport), renderedHtml: rendered, renderMode: RenderMode.Always);

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileTapTargetsTooClose);
    }

    [Fact]
    public async Task RenderedFixedWidthOverflow_ProducesWarning()
    {
        var rendered = Page(GoodViewport, """<div width="800">wide</div>""");
        var context = StructuredDataTestContext.Create(Page(GoodViewport), renderedHtml: rendered, renderMode: RenderMode.Always);

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileContentWiderThanViewport);
    }

    [Fact]
    public async Task RenderedWellSizedPage_ProducesNoRenderFindings()
    {
        var rendered = Page(GoodViewport,
            """<a href="/x" style="width: 48px; height: 48px">x</a><div style="width: 360px">fits</div>""");
        var context = StructuredDataTestContext.Create(Page(GoodViewport), renderedHtml: rendered, renderMode: RenderMode.Always);

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.DoesNotContain(findings, f => f.RuleKey is
            StructuredDataRuleKeys.MobileTapTargetTooSmall or
            StructuredDataRuleKeys.MobileTapTargetsTooClose or
            StructuredDataRuleKeys.MobileContentWiderThanViewport or
            StructuredDataRuleKeys.MobileRenderRequired);
    }

    [Fact]
    public async Task WidthAttributeTapTarget_IsMeasured()
    {
        var rendered = Page(GoodViewport, """<a href="/x" width="24" height="24">x</a>""");
        var context = StructuredDataTestContext.Create(Page(GoodViewport), renderedHtml: rendered, renderMode: RenderMode.Always);

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.MobileTapTargetTooSmall);
    }
}
