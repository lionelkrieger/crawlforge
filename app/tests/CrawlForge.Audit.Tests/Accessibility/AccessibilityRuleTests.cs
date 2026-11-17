using CrawlForge.Audit.Accessibility;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Accessibility;

/// <summary>
/// Rule-family unit tests (FS-CRAWL-001 §7.10): each of the seven families
/// over realistic parsed HTML fixtures, with locator/remediation evidence.
/// </summary>
public sealed class AccessibilityRuleTests
{
    private static async Task<IReadOnlyList<FindingDraft>> Analyze(
        string html,
        AccessibilityAnalyzerOptions? options = null,
        RenderMode mode = RenderMode.None,
        string? renderedHtml = null)
    {
        var analyzer = new AccessibilityAnalyzer(options);
        var context = AuditContexts.Create(html: html, renderedHtml: renderedHtml, renderingMode: mode);
        return await analyzer.AnalyzeAsync(context);
    }

    private static FindingDraft Single(IReadOnlyList<FindingDraft> findings, string ruleKey) =>
        Assert.Single(findings, finding => finding.RuleKey == ruleKey);

    // ── Image alternative text ────────────────────────────────────────────

    [Fact]
    public async Task ImageAlt_MissingAlt_EmitsError_WithLocatorAndRemediation()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <img src="/hero.png">
            </main></body></html>
            """);
        var finding = Single(findings, AccessibilityRuleKeys.ImageAlt);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        Assert.Equal(AuditCategory.Accessibility, finding.Category);
        Assert.Equal(1, finding.UrlId);
        Assert.Contains("img", finding.EvidenceJson);
        Assert.Contains("alt", finding.EvidenceJson);
    }

    [Fact]
    public async Task ImageAlt_EmptyAltIsDecorative_AndPresentationRolePass()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <img src="/decoration.png" alt="">
            <img src="/spacer.gif" role="presentation">
            <img src="/hidden.png" aria-hidden="true">
            </main></body></html>
            """);
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.ImageAlt);
    }

    [Fact]
    public async Task ImageAlt_FigcaptionProvidesAlternative()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <figure><img src="/chart.png"><figcaption>Quarterly revenue chart</figcaption></figure>
            </main></body></html>
            """);
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.ImageAlt);
    }

    [Fact]
    public async Task ImageAlt_ImageInputAndAreaRequireAlt()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <input type="image" src="/go.png">
            <map name="m"><area href="/x" shape="rect" coords="0,0,10,10"></map>
            </main></body></html>
            """);
        Assert.Equal(2, findings.Count(finding => finding.RuleKey == AccessibilityRuleKeys.ImageAlt));
    }

    // ── ARIA validity ─────────────────────────────────────────────────────

    [Fact]
    public async Task Aria_UnknownRole_EmitsError()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1><div role="banana">x</div></main></body></html>
            """);
        var finding = Single(findings, AccessibilityRuleKeys.AriaValidRole);
        Assert.Contains("banana", finding.EvidenceJson);
    }

    [Fact]
    public async Task Aria_UnknownAttribute_AndInvalidValue_EmitErrors()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <div aria-bogus="1" aria-checked="maybe">x</div>
            </main></body></html>
            """);
        var attributeFindings = findings.Where(finding => finding.RuleKey == AccessibilityRuleKeys.AriaValidAttr).ToList();
        Assert.Equal(2, attributeFindings.Count);
        Assert.Contains(attributeFindings, finding => finding.EvidenceJson.Contains("aria-bogus"));
        Assert.Contains(attributeFindings, finding => finding.EvidenceJson.Contains("maybe"));
    }

    [Fact]
    public async Task Aria_RoleMissingRequiredAttribute_EmitsWarning()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1><div role="checkbox">option</div></main></body></html>
            """);
        var finding = Single(findings, AccessibilityRuleKeys.AriaRequiredAttr);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("aria-checked", finding.EvidenceJson);
    }

    [Fact]
    public async Task Aria_DanglingIdReference_EmitsWarning()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <input aria-labelledby="missing-label">
            </main></body></html>
            """);
        Assert.Contains(findings, finding => finding.RuleKey == AccessibilityRuleKeys.AriaIdRef
            && finding.EvidenceJson.Contains("missing-label"));
    }

    [Fact]
    public async Task Aria_ValidUsage_ProducesNoAriaFindings()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <div role="checkbox" aria-checked="false" tabindex="0">option</div>
            <span id="lbl">Name</span><input aria-labelledby="lbl" aria-required="true">
            <div aria-live="polite" aria-level="2" role="heading">H</div>
            </main></body></html>
            """);
        Assert.DoesNotContain(findings, finding => finding.RuleKey.StartsWith("accessibility.aria-", StringComparison.Ordinal));
    }

    // ── Form-label association ────────────────────────────────────────────

    [Fact]
    public async Task Form_UnlabelledInput_EmitsError()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1><input type="text" name="q"></main></body></html>
            """);
        var finding = Single(findings, AccessibilityRuleKeys.FormLabel);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task Form_LabelFor_WrappingLabel_AriaLabel_AllPass()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <label for="a">Alpha</label><input id="a" type="text">
            <label>Beta <input type="text"></label>
            <input type="text" aria-label="Gamma">
            <input type="text" title="Delta">
            <input type="submit" value="Send">
            </main></body></html>
            """);
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.FormLabel);
    }

    [Fact]
    public async Task Form_RadioGroupWithoutFieldset_EmitsWarning()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <input type="radio" name="size" value="s" aria-label="Small">
            <input type="radio" name="size" value="l" aria-label="Large">
            </main></body></html>
            """);
        var finding = Single(findings, AccessibilityRuleKeys.FieldsetLegend);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
    }

    [Fact]
    public async Task Form_RadioGroupWithFieldsetLegend_Passes()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <fieldset><legend>Size</legend>
            <input type="radio" name="size" value="s" aria-label="Small">
            <input type="radio" name="size" value="l" aria-label="Large">
            </fieldset>
            </main></body></html>
            """);
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.FieldsetLegend);
    }

    // ── Landmark and heading structure ────────────────────────────────────

    [Fact]
    public async Task Structure_NoMainLandmark_EmitsWarning()
    {
        var findings = await Analyze("""
            <html lang="en"><body><h1>T</h1><p>content</p></body></html>
            """);
        Assert.Contains(findings, finding => finding.RuleKey == AccessibilityRuleKeys.LandmarkMain);
    }

    [Fact]
    public async Task Structure_HeadingSkip_EmitsWarning()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1><h3>Skipped</h3></main></body></html>
            """);
        var finding = Single(findings, AccessibilityRuleKeys.HeadingOrder);
        Assert.Contains("h1", finding.EvidenceJson);
        Assert.Contains("h3", finding.EvidenceJson);
    }

    [Fact]
    public async Task Structure_EmptyHeading_EmitsWarning()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1><h2></h2></main></body></html>
            """);
        Assert.Contains(findings, finding => finding.RuleKey == AccessibilityRuleKeys.HeadingEmpty);
    }

    [Fact]
    public async Task Structure_MultipleH1_EmitsNotice()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>One</h1><h1>Two</h1></main></body></html>
            """);
        var finding = Single(findings, AccessibilityRuleKeys.HeadingH1);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
    }

    [Fact]
    public async Task Structure_DuplicateNavWithoutLabels_EmitsNotice()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1></main>
            <nav><a href="/a">A</a></nav><nav><a href="/b">B</a></nav>
            </body></html>
            """);
        Assert.Contains(findings, finding => finding.RuleKey == AccessibilityRuleKeys.LandmarkUnique);
    }

    [Fact]
    public async Task Structure_DuplicateNavWithDistinctLabels_Passes()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1></main>
            <nav aria-label="Primary"><a href="/a">A</a></nav>
            <nav aria-label="Footer"><a href="/b">B</a></nav>
            </body></html>
            """);
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.LandmarkUnique);
    }

    // ── Link purpose ──────────────────────────────────────────────────────

    [Fact]
    public async Task Link_EmptyAnchor_EmitsError()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1><a href="/x"></a></main></body></html>
            """);
        Assert.Contains(findings, finding => finding.RuleKey == AccessibilityRuleKeys.LinkPurpose);
    }

    [Fact]
    public async Task Link_ImageOnlyAnchorWithAlt_Passes()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <a href="/x"><img src="/icon.png" alt="Open settings"></a>
            </main></body></html>
            """);
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.LinkPurpose);
    }

    [Fact]
    public async Task Link_GenericClickHere_EmitsNotice()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1><a href="/report">click here</a></main></body></html>
            """);
        var finding = Single(findings, AccessibilityRuleKeys.LinkGenericText);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
    }

    [Fact]
    public async Task Link_AriaLabel_Passes()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1><a href="/x" aria-label="Download report"></a></main></body></html>
            """);
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.LinkPurpose);
    }

    // ── Document language ─────────────────────────────────────────────────

    [Fact]
    public async Task Language_MissingLang_EmitsError()
    {
        var findings = await Analyze("""
            <html><body><main><h1>T</h1></main></body></html>
            """);
        Assert.Contains(findings, finding => finding.RuleKey == AccessibilityRuleKeys.DocumentLanguage);
    }

    [Fact]
    public async Task Language_MalformedTag_EmitsWarning()
    {
        var findings = await Analyze("""
            <html lang="english123"><body><main><h1>T</h1></main></body></html>
            """);
        Assert.Contains(findings, finding => finding.RuleKey == AccessibilityRuleKeys.DocumentLanguageValid);
    }

    [Fact]
    public async Task Language_ValidTag_Passes()
    {
        var findings = await Analyze("""
            <html lang="en-GB"><body><main><h1>T</h1></main></body></html>
            """);
        Assert.DoesNotContain(findings, finding => finding.RuleKey.StartsWith("accessibility.document-language", StringComparison.Ordinal));
    }

    // ── Color contrast (render-dependent) ─────────────────────────────────

    [Fact]
    public async Task Contrast_LowContrastInline_WhenRendered_EmitsWarning()
    {
        const string html = """
            <html lang="en"><body><main><h1>T</h1>
            <p style="color:#777777;background-color:#ffffff">faint text</p>
            </main></body></html>
            """;
        var findings = await Analyze(html, mode: RenderMode.Always, renderedHtml: html);
        var finding = Single(findings, AccessibilityRuleKeys.ColorContrast);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("ratio", finding.EvidenceJson);
    }

    [Fact]
    public async Task Contrast_GoodContrast_ProducesNoSignal()
    {
        const string html = """
            <html lang="en"><body><main><h1>T</h1>
            <p style="color:#000000;background-color:#ffffff">clear text</p>
            </main></body></html>
            """;
        var findings = await Analyze(html, mode: RenderMode.Always, renderedHtml: html);
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.ColorContrast
            && !finding.EvidenceJson.Contains("requiresRendering"));
    }

    [Fact]
    public async Task Contrast_RenderingOff_EmitsRequiresRenderingNotice()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1>
            <p style="color:#777777;background-color:#ffffff">faint text</p>
            </main></body></html>
            """);
        var finding = Single(findings, AccessibilityRuleKeys.ColorContrast);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
        Assert.Contains("requiresRendering", finding.EvidenceJson);
    }

    [Fact]
    public async Task Contrast_RenderingEnabledButNoRenderedTree_EmitsNotice()
    {
        var findings = await Analyze("""
            <html lang="en"><body><main><h1>T</h1></main></body></html>
            """, mode: RenderMode.Auto);
        var finding = Single(findings, AccessibilityRuleKeys.ColorContrast);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
        Assert.Contains("no rendered tree", finding.EvidenceJson);
    }
}
