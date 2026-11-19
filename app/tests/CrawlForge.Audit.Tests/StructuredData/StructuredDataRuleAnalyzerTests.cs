using System.Text.Json;
using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.StructuredData.Support;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.StructuredData;

/// <summary>
/// Pipeline rule adapter suite (WP-09-04, FS-CRAWL-001 §7.13): the REAL fixture
/// structured-data-errors page and synthetic blocks flow through the production
/// AngleSharp parser + shared validator core + <see cref="StructuredDataRuleAnalyzer"/>.
/// Asserts the exact finding rule keys, severities, URL attribution and evidence
/// the specification names (error taxonomy, missing required/recommended fields,
/// rich-result states, entity relationships, document order, thread safety).
/// </summary>
public class StructuredDataRuleAnalyzerTests
{
    private readonly StructuredDataValidator _validator = new(FixtureAuditContext.Parser);
    private readonly StructuredDataRuleAnalyzer _analyzer;

    public StructuredDataRuleAnalyzerTests()
    {
        _analyzer = new StructuredDataRuleAnalyzer(_validator);
    }

    private const string CompleteProduct = """
        <html><head>
        <script type="application/ld+json">
        {
          "@context": "https://schema.org", "@type": "Product", "name": "Widget",
          "image": "https://example.test/w.jpg", "description": "A widget",
          "sku": "W-1", "brand": { "@type": "Brand", "name": "Acme" },
          "aggregateRating": { "@type": "AggregateRating", "ratingValue": "4.5", "reviewCount": "10" },
          "offers": { "@type": "Offer", "price": "9.99", "priceCurrency": "EUR" }
        }
        </script>
        </head><body></body></html>
        """;

    private const string RdfaProduct = """
        <html><head></head><body>
        <div vocab="https://schema.org/" typeof="Product">
          <span property="name">Widget</span>
          <img property="image" src="https://example.test/w.jpg" />
          <span property="description">A widget</span>
          <div property="offers" typeof="Offer">
            <span property="price" content="9.99">9.99</span>
            <span property="priceCurrency" content="EUR">EUR</span>
          </div>
        </div>
        </body></html>
        """;

    [Fact]
    public async Task FixtureStructuredDataErrors_ReportsInvalidProductAndMalformedOrganization()
    {
        var context = FixtureAuditContext.Create(FixtureSitePages.StructuredDataErrors, urlId: 7, auditId: 3);

        var findings = await _analyzer.AnalyzeAsync(context);

        // Block 0 (Product, semantic failure) → invalid + advisory recommended gap;
        // block 1 (Organization, unterminated JSON) → malformed-block. Document order.
        Assert.Equal(3, findings.Count);
        Assert.Equal(StructuredDataRuleKeys.StructuredDataInvalid, findings[0].RuleKey);
        Assert.Equal(StructuredDataRuleKeys.StructuredDataMissingRecommended, findings[1].RuleKey);
        Assert.Equal(StructuredDataRuleKeys.StructuredDataMalformedBlock, findings[2].RuleKey);
        Assert.All(findings, f =>
        {
            Assert.Equal(AuditCategory.StructuredData, f.Category);
            Assert.Equal(7, f.UrlId);
        });
        Assert.Equal(FindingSeverity.Error, findings[0].Severity);
        Assert.Equal(FindingSeverity.Notice, findings[1].Severity);
        Assert.Equal(FindingSeverity.Error, findings[2].Severity);
    }

    [Fact]
    public async Task FixtureStructuredDataErrors_InvalidEvidenceNamesEveryMissingRequiredField()
    {
        var context = FixtureAuditContext.Create(FixtureSitePages.StructuredDataErrors, urlId: 7);

        var findings = await _analyzer.AnalyzeAsync(context);

        var invalid = Assert.Single(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataInvalid);
        using var evidence = JsonDocument.Parse(invalid.EvidenceJson);
        var root = evidence.RootElement;
        Assert.Equal(FixtureSitePages.StructuredDataUrl, root.GetProperty("url").GetString());
        Assert.Equal("json-ld", root.GetProperty("format").GetString());
        Assert.Equal(0, root.GetProperty("ordinal").GetInt32());
        Assert.Equal("Product", root.GetProperty("type").GetString());
        Assert.Equal("missing-required-field", root.GetProperty("richResult").GetString());
        Assert.Equal(
            new[] { "name", "image", "offers" },
            root.GetProperty("missingRequired").EnumerateArray().Select(e => e.GetString()).ToArray());
        var errors = root.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("missing-required-field: Product requires 'name'", errors);
        Assert.Contains("missing-required-field: Product requires 'image'", errors);
        Assert.Contains("missing-required-field: Product requires 'offers'", errors);
        Assert.Contains(errors, e => e!.StartsWith("rich-result-missing-required:", StringComparison.Ordinal) &&
                                     e.Contains("name, image, offers"));
    }

    [Fact]
    public async Task FixtureStructuredDataErrors_MalformedEvidenceCarriesSyntaxError()
    {
        var context = FixtureAuditContext.Create(FixtureSitePages.StructuredDataErrors, urlId: 7);

        var findings = await _analyzer.AnalyzeAsync(context);

        var malformed = Assert.Single(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataMalformedBlock);
        using var evidence = JsonDocument.Parse(malformed.EvidenceJson);
        var root = evidence.RootElement;
        Assert.Equal(1, root.GetProperty("ordinal").GetInt32());
        Assert.Equal("json-ld", root.GetProperty("format").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("type").ValueKind);
        var error = Assert.Single(root.GetProperty("errors").EnumerateArray()).GetString();
        Assert.NotNull(error);
        Assert.StartsWith("malformed-block:", error, StringComparison.Ordinal);
        Assert.True(error.Length > "malformed-block:".Length, "the parse error detail must be preserved");
        Assert.Equal("rec." + StructuredDataRuleKeys.StructuredDataMalformedBlock, malformed.RecommendationKey);
    }

    [Fact]
    public async Task FixtureStructuredDataErrors_RecommendedGapNamesTheAdvisoryFields()
    {
        var context = FixtureAuditContext.Create(FixtureSitePages.StructuredDataErrors, urlId: 7);

        var findings = await _analyzer.AnalyzeAsync(context);

        var recommended = Assert.Single(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataMissingRecommended);
        Assert.Equal(FindingSeverity.Notice, recommended.Severity);
        using var evidence = JsonDocument.Parse(recommended.EvidenceJson);
        Assert.Equal("Product", evidence.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            new[] { "sku", "brand", "aggregateRating" },
            evidence.RootElement.GetProperty("fields").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task CompleteProduct_PassesWithEligibleRichResult()
    {
        var context = FixtureAuditContext.Create(CompleteProduct, url: "https://example.test/product", urlId: 11);

        var findings = await _analyzer.AnalyzeAsync(context);

        var pass = Assert.Single(findings);
        Assert.Equal(StructuredDataRuleKeys.StructuredDataValid, pass.RuleKey);
        Assert.Equal(FindingSeverity.Pass, pass.Severity);
        Assert.Equal(11, pass.UrlId);
        Assert.Null(pass.RecommendationKey);
        using var evidence = JsonDocument.Parse(pass.EvidenceJson);
        Assert.Equal("eligible", evidence.RootElement.GetProperty("richResult").GetString());
        Assert.Equal("Product", evidence.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            new[] { "Brand", "AggregateRating", "Offer" },
            evidence.RootElement.GetProperty("nestedEntityTypes").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task NonFeatureType_ReportsRichResultIneligibleNotice()
    {
        var markup = """
            <script type="application/ld+json">{ "@context": "https://schema.org", "@type": "Person", "name": "Ada" }</script>
            """;
        var context = FixtureAuditContext.Create(markup, url: "https://example.test/person");

        var findings = await _analyzer.AnalyzeAsync(context);

        var ineligible = Assert.Single(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataRichResultIneligible);
        Assert.Equal(FindingSeverity.Notice, ineligible.Severity);
        using var evidence = JsonDocument.Parse(ineligible.EvidenceJson);
        Assert.Equal("ineligible", evidence.RootElement.GetProperty("richResult").GetString());
        Assert.DoesNotContain(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataInvalid);
    }

    [Fact]
    public async Task RdfaProduct_FlowsThroughTheSameAdapterWithRelationships()
    {
        var context = FixtureAuditContext.Create(RdfaProduct, url: "https://example.test/rdfa", urlId: 12);

        var findings = await _analyzer.AnalyzeAsync(context);

        var pass = Assert.Single(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataValid);
        using var evidence = JsonDocument.Parse(pass.EvidenceJson);
        Assert.Equal("rdfa", evidence.RootElement.GetProperty("format").GetString());
        Assert.Equal("Product", evidence.RootElement.GetProperty("type").GetString());
        Assert.Equal("eligible", evidence.RootElement.GetProperty("richResult").GetString());
        Assert.Equal(
            new[] { "Offer" },
            evidence.RootElement.GetProperty("nestedEntityTypes").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task RdfaMissingRequired_ReportsInvalidWithNamedFields()
    {
        var markup = """
            <html><body>
            <div vocab="https://schema.org/" typeof="Product"><span property="name">Widget</span></div>
            </body></html>
            """;
        var context = FixtureAuditContext.Create(markup, url: "https://example.test/rdfa-bad");

        var findings = await _analyzer.AnalyzeAsync(context);

        var invalid = Assert.Single(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataInvalid);
        using var evidence = JsonDocument.Parse(invalid.EvidenceJson);
        Assert.Equal("rdfa", evidence.RootElement.GetProperty("format").GetString());
        Assert.Equal(
            new[] { "image", "offers" },
            evidence.RootElement.GetProperty("missingRequired").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task MultipleEntities_AreReportedInDocumentOrderWithOrdinals()
    {
        var markup = """
            <html><head>
            <script type="application/ld+json">{ "@context": "https://schema.org", "@type": "WebSite", "name": "Site", "url": "https://example.test" }</script>
            <script type="application/ld+json">{ "@context": "https://schema.org", "@type": "Person", "name": "Ada" }</script>
            </head><body>
            <div itemscope itemtype="https://schema.org/Organization"><span itemprop="name">Org</span><span itemprop="url">https://example.test</span><span itemprop="logo">https://example.test/l.png</span></div>
            </body></html>
            """;
        var context = FixtureAuditContext.Create(markup, url: "https://example.test/multi");

        var findings = await _analyzer.AnalyzeAsync(context);

        var judgments = findings.Where(f => f.RuleKey is StructuredDataRuleKeys.StructuredDataValid
            or StructuredDataRuleKeys.StructuredDataRichResultIneligible).ToList();
        Assert.Equal(3, judgments.Count);
        var ordinals = judgments.Select(f =>
        {
            using var evidence = JsonDocument.Parse(f.EvidenceJson);
            return evidence.RootElement.GetProperty("ordinal").GetInt32();
        }).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, ordinals);
        var formats = judgments.Select(f =>
        {
            using var evidence = JsonDocument.Parse(f.EvidenceJson);
            return evidence.RootElement.GetProperty("format").GetString();
        }).ToArray();
        Assert.Equal(new[] { "json-ld", "json-ld", "microdata" }, formats);
    }

    [Fact]
    public async Task RenderedDocument_TakesPrecedenceOverRawResponse()
    {
        var rendered = """
            <html><head><script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "Article", "headline": "Rendered", "image": "https://example.test/i.jpg", "datePublished": "2026-01-01", "author": "Ada", "dateModified": "2026-01-02", "publisher": "Pub" }
            </script></head><body></body></html>
            """;
        var context = FixtureAuditContext.Create(
            FixtureSitePages.StructuredDataErrors,
            url: "https://example.test/rendered",
            renderedHtml: rendered);

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.DoesNotContain(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataMalformedBlock);
        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataValid);
    }

    [Fact]
    public async Task NonHtmlContentType_ProducesNoFindings()
    {
        var context = FixtureAuditContext.Create(
            FixtureSitePages.StructuredDataErrors, url: "https://example.test/data.json", contentType: "application/json");

        Assert.Empty(await _analyzer.AnalyzeAsync(context));
    }

    [Fact]
    public async Task NonSuccessStatus_ProducesNoFindings()
    {
        var context = FixtureAuditContext.Create(
            FixtureSitePages.StructuredDataErrors, url: "https://example.test/missing", statusClass: CrawlStatusClass.ClientError);

        Assert.Empty(await _analyzer.AnalyzeAsync(context));
    }

    [Fact]
    public async Task NoDocument_ProducesNoFindings()
    {
        var context = FixtureAuditContext.Create(
            FixtureSitePages.StructuredDataErrors, url: "https://example.test/nodoc", includeDocument: false);

        Assert.Empty(await _analyzer.AnalyzeAsync(context));
    }

    [Fact]
    public async Task PlainPage_ProducesNoFindings()
    {
        var context = FixtureAuditContext.Create(
            FixtureSitePages.Page("Plain", "", "<p>no structured data here</p>"),
            url: "https://example.test/plain");

        Assert.Empty(await _analyzer.AnalyzeAsync(context));
    }

    [Fact]
    public async Task MalformedMarkup_NeverThrows()
    {
        var context = FixtureAuditContext.Create(
            "<html><head><script type=\"application/ld+json\">{{{</script><div itemscope><span itemprop=\"name\">x</div></head><body>",
            url: "https://example.test/garbage");

        var findings = await _analyzer.AnalyzeAsync(context);

        Assert.Contains(findings, f => f.RuleKey == StructuredDataRuleKeys.StructuredDataMalformedBlock);
    }

    [Fact]
    public void SupportedRuleKeys_ExposeTheStructuredDataTaxonomy()
    {
        Assert.Equal(
            new[]
            {
                StructuredDataRuleKeys.StructuredDataMalformedBlock,
                StructuredDataRuleKeys.StructuredDataInvalid,
                StructuredDataRuleKeys.StructuredDataRichResultIneligible,
                StructuredDataRuleKeys.StructuredDataValid,
                StructuredDataRuleKeys.StructuredDataMissingRecommended,
            },
            _analyzer.SupportedRuleKeys);
    }

    [Fact]
    public async Task ConcurrentPages_ProduceTheSameFindingsAsSequentialRuns()
    {
        // Pipeline MaxConcurrency=2: one shared adapter instance, two workers.
        var contexts = Enumerable.Range(0, 40).Select(i => FixtureAuditContext.Create(
            i % 2 == 0 ? FixtureSitePages.StructuredDataErrors : CompleteProduct,
            url: $"https://example.test/page-{i}",
            urlId: i + 1)).ToArray();
        var baseline = new IReadOnlyList<FindingDraft>[contexts.Length];
        for (var i = 0; i < contexts.Length; i++)
        {
            baseline[i] = await _analyzer.AnalyzeAsync(contexts[i]);
        }

        var concurrent = new IReadOnlyList<FindingDraft>[contexts.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, contexts.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 2 },
            async (i, ct) => concurrent[i] = await _analyzer.AnalyzeAsync(contexts[i], ct));

        for (var i = 0; i < contexts.Length; i++)
        {
            Assert.Equal(baseline[i].Count, concurrent[i].Count);
            for (var j = 0; j < baseline[i].Count; j++)
            {
                Assert.Equal(baseline[i][j].RuleKey, concurrent[i][j].RuleKey);
                Assert.Equal(baseline[i][j].Severity, concurrent[i][j].Severity);
                Assert.Equal(baseline[i][j].UrlId, concurrent[i][j].UrlId);
                Assert.Equal(baseline[i][j].EvidenceJson, concurrent[i][j].EvidenceJson);
            }
        }
    }

    [Fact]
    public void ToolCheckerAndPipelineAdapter_ShareTheSameEngineJudgments()
    {
        // UI-19 path: IStructuredDataValidator.Validate(markup, url).
        var toolBlocks = _validator.Validate(FixtureSitePages.StructuredDataErrors, FixtureSitePages.StructuredDataUrl).Blocks;
        // Pipeline path: ValidateDetailed over the already-parsed document.
        var document = FixtureAuditContext.Parser.Parse(FixtureSitePages.StructuredDataErrors, FixtureSitePages.StructuredDataUrl);
        var pipelineBlocks = _validator.ValidateDetailed(document).Blocks.Select(d => d.Block).ToList();

        Assert.Equal(toolBlocks.Count, pipelineBlocks.Count);
        for (var i = 0; i < toolBlocks.Count; i++)
        {
            Assert.Equal(toolBlocks[i].Format, pipelineBlocks[i].Format);
            Assert.Equal(toolBlocks[i].Type, pipelineBlocks[i].Type);
            Assert.Equal(toolBlocks[i].IsValid, pipelineBlocks[i].IsValid);
            Assert.Equal(toolBlocks[i].RichResultEligible, pipelineBlocks[i].RichResultEligible);
            Assert.Equal(toolBlocks[i].Errors, pipelineBlocks[i].Errors);
        }
    }
}
