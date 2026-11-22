using System.Text.Json;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.Tests.Indexability.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Indexability;

/// <summary>
/// Soft-404 detection (FS-CRAWL-001 §7.12, criterion #28): success-status
/// pages with error-like content are flagged distinctly from hard HTTP
/// errors; WAF challenge pages are excluded; phrase list is configurable.
/// </summary>
public sealed class Soft404DetectorTests
{
    [Fact]
    public async Task ErrorTitleOnSuccessPage_FlaggedWithHighConfidence()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("404 - Page Not Found", null, new[] { DomBuilder.Text("p", "The requested page was not found.") }));
        var detector = new Soft404Detector();

        Soft404Verdict verdict = await detector.DetectAsync(IndexabilityContextFactory.Context(document: document));

        Assert.True(verdict.IsSoft404);
        Assert.True(verdict.Confidence >= 0.9);
        using JsonDocument evidence = JsonDocument.Parse(verdict.EvidenceJson);
        Assert.Equal("error-phrase-match", evidence.RootElement.GetProperty("reason").GetString());
        Assert.Contains(
            evidence.RootElement.GetProperty("matchedPhrases").EnumerateArray(),
            m => m.GetProperty("location").GetString() == "title");
    }

    [Fact]
    public async Task ErrorPhraseInBodyOnly_Flagged()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Our site", null, new[] { DomBuilder.Text("p", "Sorry, we couldn't find the page you were looking for.") }));
        var detector = new Soft404Detector();

        Soft404Verdict verdict = await detector.DetectAsync(IndexabilityContextFactory.Context(document: document));

        Assert.True(verdict.IsSoft404);
        Assert.True(verdict.Confidence >= 0.7);
    }

    [Fact]
    public async Task HealthyContent_NotFlagged()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Product catalogue", null, new[] { DomBuilder.Text("p", "Browse our full range of products with detailed descriptions and reviews.") }));
        var detector = new Soft404Detector();

        Soft404Verdict verdict = await detector.DetectAsync(IndexabilityContextFactory.Context(document: document));

        Assert.False(verdict.IsSoft404);
        Assert.Equal(0, verdict.Confidence);
    }

    [Theory]
    [InlineData(CrawlStatusClass.ClientError, 404)]
    [InlineData(CrawlStatusClass.ServerError, 500)]
    [InlineData(CrawlStatusClass.FetchError, null)]
    [InlineData(CrawlStatusClass.NotCrawled, null)]
    public async Task HardErrors_NeverClassifiedAsSoft404(CrawlStatusClass statusClass, int? statusCode)
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("404 - Page Not Found", null, null));
        var detector = new Soft404Detector();
        PageAuditContext context = IndexabilityContextFactory.Context(
            document: document,
            url: IndexabilityContextFactory.UrlRecord(statusClass, statusCode));

        Soft404Verdict verdict = await detector.DetectAsync(context);

        Assert.False(verdict.IsSoft404);
        using JsonDocument evidence = JsonDocument.Parse(verdict.EvidenceJson);
        Assert.Equal("not-a-success-page", evidence.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task WafChallengePage_ExcludedFromSoft404()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("404 - Page Not Found", null, null));
        UrlRecordDto url = IndexabilityContextFactory.UrlRecord(CrawlStatusClass.Blocked, 200) with
        {
            ChallengeClass = WafChallengeClass.Interstitial,
        };
        var detector = new Soft404Detector();

        Soft404Verdict verdict = await detector.DetectAsync(IndexabilityContextFactory.Context(document: document, url: url));

        Assert.False(verdict.IsSoft404);
    }

    [Fact]
    public async Task ThinPageWithErrorTokenTitle_StructuralHeuristicFlag()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Error", null, new[] { DomBuilder.Text("p", "Oops.") }));
        var detector = new Soft404Detector();

        Soft404Verdict verdict = await detector.DetectAsync(IndexabilityContextFactory.Context(document: document));

        Assert.True(verdict.IsSoft404);
        Assert.True(verdict.Confidence >= 0.6);
        using JsonDocument evidence = JsonDocument.Parse(verdict.EvidenceJson);
        Assert.Equal("structural-heuristic", evidence.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ConfigurablePhraseList_SuppressesDefaultMatches()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Page Not Found", null, null));
        var detector = new Soft404Detector(new Soft404Options
        {
            ErrorPhrases = new[] { "custom-missing-marker" },
            ConfidenceThreshold = 0.95,
        });

        Soft404Verdict verdict = await detector.DetectAsync(IndexabilityContextFactory.Context(document: document));

        Assert.False(verdict.IsSoft404);
    }

    [Fact]
    public async Task ConfigurablePhraseList_AddsCustomDetection()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Welcome", null, new[] { DomBuilder.Text("p", "This item has been custom-missing-marker removed.") }));
        var detector = new Soft404Detector(new Soft404Options { ErrorPhrases = new[] { "custom-missing-marker" } });

        Soft404Verdict verdict = await detector.DetectAsync(IndexabilityContextFactory.Context(document: document));

        Assert.True(verdict.IsSoft404);
    }

    [Fact]
    public async Task ScopedText_UsedWhenPresent()
    {
        // Full document is healthy; the scoped content area carries the error.
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Healthy title", null, new[] { DomBuilder.Text("p", "Navigation and boilerplate text.") }));
        var scoped = new ScopedVisibleText("Page not found in the content area.", 7, Array.Empty<string>());
        var detector = new Soft404Detector();

        Soft404Verdict verdict = await detector.DetectAsync(
            IndexabilityContextFactory.Context(document: document, scopedText: scoped));

        Assert.True(verdict.IsSoft404);
        using JsonDocument evidence = JsonDocument.Parse(verdict.EvidenceJson);
        Assert.Equal(7, evidence.RootElement.GetProperty("wordCount").GetInt32());
    }

    [Fact]
    public async Task Rerun_IsDeterministic()
    {
        DomDocument document = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("404 - Page Not Found", null, null));
        PageAuditContext context = IndexabilityContextFactory.Context(document: document);
        var detector = new Soft404Detector();

        Soft404Verdict first = await detector.DetectAsync(context);
        Soft404Verdict second = await detector.DetectAsync(context);

        Assert.Equal(first, second);
    }

    [Fact]
    public void InvalidOptions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Soft404Detector(new Soft404Options { ConfidenceThreshold = 1.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Soft404Detector(new Soft404Options { ThinContentWordThreshold = -1 }));
    }

    // ── WP-10-04: client-route soft 404 (rendered document evidence) ─────────

    [Fact]
    public async Task ClientRoute_RawShellClean_RenderedErrorPage_ClassifiedFromRendered()
    {
        // SPA shell: clean 200 document, no error signal in the raw markup.
        DomDocument raw = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("SPA application", null, new[] { DomBuilder.Text("div", "Loading application shell.") }));
        // Rendered unknown route: the client router shows the error page.
        DomDocument rendered = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Page not found", null, new[] { DomBuilder.Text("h1", "Page not found") }));
        var detector = new Soft404Detector();

        Soft404Verdict verdict = await detector.DetectAsync(
            IndexabilityContextFactory.Context(document: raw, renderedDocument: rendered));

        Assert.True(verdict.IsSoft404);
        Assert.True(verdict.Confidence >= 0.9);
        using JsonDocument evidence = JsonDocument.Parse(verdict.EvidenceJson);
        Assert.Equal("rendered-client-route", evidence.RootElement.GetProperty("reason").GetString());
        Assert.Equal("rendered", evidence.RootElement.GetProperty("source").GetString());
        Assert.Equal("Page not found", evidence.RootElement.GetProperty("title").GetString());
        Assert.Equal("SPA application", evidence.RootElement.GetProperty("rawTitle").GetString());
    }

    [Fact]
    public async Task ClientRoute_RawAlreadyError_KeepsRawClassification()
    {
        // Dedup guard: when the raw document already carries the error signal,
        // the rendered pass must not re-label or double-classify it.
        DomDocument raw = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("404 - Page Not Found", null, null));
        DomDocument rendered = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Page not found", null, null));
        var detector = new Soft404Detector();

        Soft404Verdict verdict = await detector.DetectAsync(
            IndexabilityContextFactory.Context(document: raw, renderedDocument: rendered));

        Assert.True(verdict.IsSoft404);
        using JsonDocument evidence = JsonDocument.Parse(verdict.EvidenceJson);
        Assert.Equal("error-phrase-match", evidence.RootElement.GetProperty("reason").GetString());
        Assert.Equal("raw", evidence.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task ClientRoute_BothCapturesHealthy_NotFlagged()
    {
        DomDocument raw = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("SPA application", null, new[] { DomBuilder.Text("div", "Loading application shell.") }));
        DomDocument rendered = DomBuilder.Document(
            IndexabilityContextFactory.PageUrl,
            DomBuilder.Page("Product dashboard", null, new[] { DomBuilder.Text("p", "Welcome back — your products and orders are listed below.") }));
        var detector = new Soft404Detector();

        Soft404Verdict verdict = await detector.DetectAsync(
            IndexabilityContextFactory.Context(document: raw, renderedDocument: rendered));

        Assert.False(verdict.IsSoft404);
    }
}