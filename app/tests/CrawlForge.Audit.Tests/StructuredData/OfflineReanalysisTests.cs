using System.Text;
using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.StructuredData.Support;
using Xunit;

namespace CrawlForge.Audit.Tests.StructuredData;

/// <summary>
/// Offline re-analysis suite (FS-CRAWL-001 §9.6): every structured-data rule
/// re-runs from the stored body/DOM without refetching. The offline context is
/// rebuilt from the real content store + migrated database.
/// </summary>
public class OfflineReanalysisTests
{
    private const string StructuredHtml = """
        <html lang="en"><head>
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <meta name="generator" content="WordPress 6.4.2" />
        <link rel="alternate" hreflang="en" href="https://example.test/page" />
        <link rel="alternate" hreflang="de" href="https://example.test/de/page" />
        <script type="application/ld+json">
        { "@context": "https://schema.org", "@type": "Article", "headline": "Offline",
          "image": "https://example.test/i.jpg", "datePublished": "2026-01-15" }
        </script>
        </head><body><p>stored body</p></body></html>
        """;

    private static TechnologyDetector CreateDetector() =>
        new(TechnologyFingerprintCatalogue.Load(StructuredDataRealStack.CatalogueAssetPath), new TestDomParser());

    [Fact]
    public async Task OfflineContext_RebuildsFromStoredBody()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        await stack.StoreBodyAsync(StructuredHtml);
        var builder = new OfflinePageContextBuilder(stack.Provider, stack.ContentStore, new TestDomParser());

        var context = await builder.BuildAsync(stack.AuditId, stack.UrlId);

        Assert.NotNull(context);
        Assert.Equal(stack.AuditId, context!.AuditId);
        Assert.Equal("https://example.test/page", context.Url.CanonicalUrl);
        Assert.NotNull(context.Document);
        Assert.NotNull(context.Fetch);
        Assert.Equal(StructuredHtml, Encoding.UTF8.GetString(context.Fetch!.Body.Span));
        Assert.Equal(Contracts.Enums.RenderMode.None, context.Configuration.RenderingMode);
    }

    [Fact]
    public async Task OfflineValidator_ReproducesOriginalBlocks()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        await stack.StoreBodyAsync(StructuredHtml);
        var validator = new StructuredDataValidator(new TestDomParser());
        var original = validator.Validate(StructuredHtml, "https://example.test/page");

        var builder = new OfflinePageContextBuilder(stack.Provider, stack.ContentStore, new TestDomParser());
        var context = await builder.BuildAsync(stack.AuditId, stack.UrlId);
        var offline = validator.Validate(Encoding.UTF8.GetString(context!.Fetch!.Body.Span), context.Url.CanonicalUrl);

        Assert.Equal(original.Blocks.Count, offline.Blocks.Count);
        Assert.Equal(original.Blocks[0].Type, offline.Blocks[0].Type);
        Assert.Equal(original.Blocks[0].IsValid, offline.Blocks[0].IsValid);
        Assert.Equal(original.Blocks[0].RichResultEligible, offline.Blocks[0].RichResultEligible);
    }

    [Fact]
    public async Task OfflineTechnologyDetection_MatchesOriginal()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        await stack.StoreBodyAsync(StructuredHtml);
        var detector = CreateDetector();
        var originalContext = StructuredDataTestContext.Create(StructuredHtml, urlId: stack.UrlId, auditId: stack.AuditId);
        var original = await detector.DetectAsync(originalContext);

        var builder = new OfflinePageContextBuilder(stack.Provider, stack.ContentStore, new TestDomParser());
        var offlineContext = await builder.BuildAsync(stack.AuditId, stack.UrlId);
        var offline = await detector.DetectAsync(offlineContext!);

        Assert.Equal(original.Count, offline.Count);
        Assert.Equal(original.Select(d => d.TechnologyKey), offline.Select(d => d.TechnologyKey));
        Assert.Equal(original.Select(d => d.Confidence), offline.Select(d => d.Confidence));
        Assert.Contains(offline, d => d.TechnologyKey == "wordpress" && d.Confidence == 1.0);
    }

    [Fact]
    public async Task OfflineI18n_WithRecordedHeaders_MatchesOriginal()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        await stack.StoreBodyAsync(StructuredHtml);
        var headers = new Dictionary<string, string>
        {
            ["Link"] = """<https://example.test/fr/page>; rel="alternate"; hreflang="fr" """,
        };
        var analyzer = new HreflangAnalyzer();
        var originalContext = StructuredDataTestContext.Create(StructuredHtml, headers: headers, urlId: stack.UrlId, auditId: stack.AuditId);
        var original = await analyzer.AnalyzeAsync(originalContext);

        var builder = new OfflinePageContextBuilder(stack.Provider, stack.ContentStore, new TestDomParser());
        var offlineContext = await builder.BuildAsync(stack.AuditId, stack.UrlId, headers);
        var offline = await analyzer.AnalyzeAsync(offlineContext!);

        Assert.Equal(original.Select(f => f.RuleKey), offline.Select(f => f.RuleKey));
        Assert.NotEmpty(offline);
    }

    [Fact]
    public async Task OfflineMobile_RerunsStaticChecks()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        await stack.StoreBodyAsync(StructuredHtml);
        var analyzer = new MobileFriendlinessAnalyzer();
        var originalContext = StructuredDataTestContext.Create(StructuredHtml, urlId: stack.UrlId, auditId: stack.AuditId);
        var original = await analyzer.AnalyzeAsync(originalContext);

        var builder = new OfflinePageContextBuilder(stack.Provider, stack.ContentStore, new TestDomParser());
        var offlineContext = await builder.BuildAsync(stack.AuditId, stack.UrlId);
        var offline = await analyzer.AnalyzeAsync(offlineContext!);

        Assert.Equal(original.Select(f => f.RuleKey), offline.Select(f => f.RuleKey));
        Assert.Contains(offline, f => f.RuleKey == StructuredDataRuleKeys.MobileRenderRequired);
    }

    [Fact]
    public async Task BuildAsync_WithoutStoredBody_ReturnsNull()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        var builder = new OfflinePageContextBuilder(stack.Provider, stack.ContentStore, new TestDomParser());

        Assert.Null(await builder.BuildAsync(stack.AuditId, stack.UrlId));
    }

    [Fact]
    public async Task BuildAsync_UnknownUrl_ReturnsNull()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        var builder = new OfflinePageContextBuilder(stack.Provider, stack.ContentStore, new TestDomParser());

        Assert.Null(await builder.BuildAsync(stack.AuditId, urlId: 9999));
    }

    [Fact]
    public async Task OfflineDetections_PersistThroughReAudit()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        await stack.StoreBodyAsync(StructuredHtml);
        var detector = CreateDetector();
        var recorder = new TechnologyDetectionRecorder(stack.Provider);

        var builder = new OfflinePageContextBuilder(stack.Provider, stack.ContentStore, new TestDomParser());
        var context = await builder.BuildAsync(stack.AuditId, stack.UrlId);
        var detections = await detector.DetectAsync(context!);
        await recorder.RecordAsync(stack.AuditId, stack.UrlId, detections);

        // Second offline pass (re-audit) replaces, never duplicates.
        var again = await detector.DetectAsync(context!);
        await recorder.RecordAsync(stack.AuditId, stack.UrlId, again);

        var stored = await recorder.GetDetectionsAsync(stack.AuditId, stack.UrlId);
        Assert.Equal(detections.Count, stored.Count);
        Assert.Contains(stored, d => d.Key == "wordpress");
    }
}
