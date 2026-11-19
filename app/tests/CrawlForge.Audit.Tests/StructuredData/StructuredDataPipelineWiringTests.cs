using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoping;
using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.StructuredData.Support;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Audit.Tests.StructuredData;

/// <summary>
/// Production-path wiring proof (WP-09-04, CODEMAP §12(g)): the adapters are
/// composed into the REAL <see cref="AuditRuleHost"/> and <see cref="AuditPipeline"/>
/// (no pipeline edits — registration itself is WP-09-11) and the REAL fixture
/// pages run through the eight-step orchestrator. Proves the per-URL structured
/// data judgments, the collect-then-site-pass hreflang ordering, rule
/// configurability (§7.6), and offline re-audit convergence (§9.6).
/// </summary>
public class StructuredDataPipelineWiringTests : IAsyncLifetime
{
    private StructuredDataRealStack _stack = null!;
    private readonly AngleSharpDomParser _parser = new();
    private StructuredDataRuleAnalyzer _structuredData = null!;
    private HreflangReciprocityRuleAnalyzer _hreflangReciprocity = null!;

    public async Task InitializeAsync()
    {
        _stack = await StructuredDataRealStack.CreateAsync();
        var validator = new StructuredDataValidator(_parser);
        _structuredData = new StructuredDataRuleAnalyzer(validator);
        _hreflangReciprocity = new HreflangReciprocityRuleAnalyzer(new HreflangReciprocityValidator(), _stack.Provider);
    }

    public async Task DisposeAsync() => await _stack.DisposeAsync();

    private AuditPipeline CreatePipeline(RuleHostOptions? options = null) =>
        new(_parser, new ElementInventoryExtractor(), new ContentScopeResolver(),
            new AuditRuleHost([_structuredData, _hreflangReciprocity], options));

    [Fact]
    public async Task RealPipeline_FixtureStructuredDataPage_EmitsTheValidationFindings()
    {
        var pipeline = CreatePipeline();
        var context = FixtureAuditContext.Create(FixtureSitePages.StructuredDataErrors, urlId: 7, auditId: _stack.AuditId);

        var result = await pipeline.RunAsync(context);

        Assert.Contains(result.Findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataInvalid && f.UrlId == 7);
        Assert.Contains(result.Findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataMalformedBlock && f.UrlId == 7);
        Assert.Contains(result.Inventory, i => i.Group == "structured_data");
    }

    [Fact]
    public async Task RealPipeline_HreflangPages_CollectPerPageThenSitePassEmits()
    {
        var pipeline = CreatePipeline();
        var enId = await InsertUrlAsync(FixtureSitePages.HreflangEnglishUrl);
        var frId = await InsertUrlAsync(FixtureSitePages.HreflangFrenchUrl);
        var enContext = FixtureAuditContext.Create(FixtureSitePages.HreflangEnglish, FixtureSitePages.HreflangEnglishUrl, urlId: enId, auditId: _stack.AuditId);
        var frContext = FixtureAuditContext.Create(FixtureSitePages.HreflangFrench, FixtureSitePages.HreflangFrenchUrl, urlId: frId, auditId: _stack.AuditId);

        var enResult = await pipeline.RunAsync(enContext);
        var frResult = await pipeline.RunAsync(frContext);

        // Per-URL passes never emit the cross-URL judgment themselves.
        Assert.DoesNotContain(enResult.Findings, f => f.RuleKey == StructuredDataRuleKeys.HreflangMissingReciprocity);
        Assert.DoesNotContain(frResult.Findings, f => f.RuleKey == StructuredDataRuleKeys.HreflangMissingReciprocity);

        // The site pass runs once every page has been parsed (D14 ordering).
        var sitePass = await _hreflangReciprocity.RunSitePassAsync(_stack.AuditId);
        var finding = Assert.Single(sitePass.Findings);
        Assert.Equal(StructuredDataRuleKeys.HreflangMissingReciprocity, finding.RuleKey);
        Assert.Equal(enId, finding.UrlId);
    }

    [Fact]
    public void RuleHost_SupportedRuleKeys_IncludeBothAdapterFamilies()
    {
        var host = new AuditRuleHost([_structuredData, _hreflangReciprocity]);

        Assert.Contains(StructuredDataRuleKeys.StructuredDataMalformedBlock, host.SupportedRuleKeys);
        Assert.Contains(StructuredDataRuleKeys.StructuredDataInvalid, host.SupportedRuleKeys);
        Assert.Contains(StructuredDataRuleKeys.StructuredDataMissingRecommended, host.SupportedRuleKeys);
        Assert.Contains(StructuredDataRuleKeys.HreflangMissingReciprocity, host.SupportedRuleKeys);
    }

    [Fact]
    public async Task RuleKeyFilter_RestrictsTheEmittedFindings()
    {
        var pipeline = CreatePipeline(new RuleHostOptions
        {
            RuleKeyFilter = [StructuredDataRuleKeys.StructuredDataMalformedBlock],
        });
        var context = FixtureAuditContext.Create(FixtureSitePages.StructuredDataErrors, urlId: 7, auditId: _stack.AuditId);

        var result = await pipeline.RunAsync(context);

        Assert.Contains(result.Findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataMalformedBlock);
        Assert.DoesNotContain(result.Findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataInvalid);
    }

    [Fact]
    public async Task EnabledCategories_CanDisableTheStructuredDataFamily()
    {
        var pipeline = CreatePipeline(new RuleHostOptions
        {
            EnabledCategories = new HashSet<AuditCategory> { AuditCategory.Accessibility },
        });
        var context = FixtureAuditContext.Create(FixtureSitePages.StructuredDataErrors, urlId: 7, auditId: _stack.AuditId);

        var result = await pipeline.RunAsync(context);

        Assert.DoesNotContain(result.Findings, f => f.Category == AuditCategory.StructuredData);
    }

    [Fact]
    public async Task OfflineReAudit_StoredBodyProducesTheSameJudgments()
    {
        // Store the fixture body, rebuild the context offline (no refetch), and
        // run the same pipeline: re-audit converges with the live judgments.
        await _stack.StoreBodyAsync(FixtureSitePages.StructuredDataErrors);
        var builder = new OfflinePageContextBuilder(_stack.Provider, _stack.ContentStore, _parser);
        var offlineContext = await builder.BuildAsync(_stack.AuditId, _stack.UrlId);
        Assert.NotNull(offlineContext);

        var live = await CreatePipeline().RunAsync(
            FixtureAuditContext.Create(FixtureSitePages.StructuredDataErrors, url: offlineContext!.Url.CanonicalUrl, urlId: _stack.UrlId, auditId: _stack.AuditId));
        var offline = await CreatePipeline().RunAsync(offlineContext);

        Assert.Equal(
            live.Findings.Select(f => (f.RuleKey, f.Severity, f.EvidenceJson)).OrderBy(t => t.RuleKey).ThenBy(t => t.EvidenceJson),
            offline.Findings.Select(f => (f.RuleKey, f.Severity, f.EvidenceJson)).OrderBy(t => t.RuleKey).ThenBy(t => t.EvidenceJson));
    }

    private async Task<long> InsertUrlAsync(string url)
    {
        // The reciprocity site pass reads url_records; persist real rows through
        // the production repository and use their assigned ids in the contexts.
        var uri = new Uri(url);
        var record = await new SqlRepository<UrlRecordDto>(_stack.Provider).CreateAsync(new UrlRecordDto(
            0, _stack.AuditId, url, uri.Host, uri.AbsolutePath, 1,
            DiscoverySource.Link, CrawlStatusClass.Success, 200, null,
            "text/html", 0, null, RobotsState.Allowed, IndexabilityFlags.None, true));
        return record.Id;
    }
}
