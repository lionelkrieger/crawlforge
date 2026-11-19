using System.Text.Json;
using CrawlForge.Audit.ContentQuality;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// WP-09-05 image rules (FS-CRAWL-001 §7.4 Images row; CAP-B-056 oversized
/// payloads, CAP-B-057 format checks). Category Performance per the lead's
/// locked-enum mapping (the spec's abstract "Images" category has no enum
/// member — handoff notation note). Real fixture images.html through the real
/// parser; oversized bytes observable today for data-URI images and via
/// explicit facts for future capture sources.
/// </summary>
public class ImageRuleAnalyzerTests
{
    private static readonly AngleSharpDomParser Parser = new();

    private static PageAuditContext Context(string html) =>
        ContextFactory.Create(Parser.Parse(html, "https://example.com/images.html"), html: html);

    /// <summary>The real fixture's two images earn legacy-format Notices
    /// (PNG and JPEG), one per unique source.</summary>
    [Fact]
    public async Task RealFixtureImages_EmitLegacyFormatNotices()
    {
        var analyzer = new ImageRuleAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context(FixtureSitePages.ImagesHtml));

        var formats = findings.Where(f => f.RuleKey == ImageRuleKeys.Format).ToArray();
        Assert.Equal(2, formats.Length);
        Assert.All(formats, f =>
        {
            Assert.Equal(FindingSeverity.Notice, f.Severity);
            Assert.Equal(AuditCategory.Performance, f.Category);
            Assert.Equal(ContextFactory.Url().Id, f.UrlId);
        });

        using JsonDocument logo = JsonDocument.Parse(formats[0].EvidenceJson);
        Assert.Equal("/assets/logo.png", logo.RootElement.GetProperty("src").GetString());
        Assert.Equal("png", logo.RootElement.GetProperty("format").GetString());
        Assert.Equal("legacy-format", logo.RootElement.GetProperty("issue").GetString());
        using JsonDocument banner = JsonDocument.Parse(formats[1].EvidenceJson);
        Assert.Equal("/assets/banner.jpg", banner.RootElement.GetProperty("src").GetString());
        Assert.Equal("jpeg", banner.RootElement.GetProperty("format").GetString());

        // Byte sizes are not observable for network images in http mode.
        Assert.DoesNotContain(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
    }

    /// <summary>Explicit transferred bytes above the threshold emit an
    /// oversized Warning with the measurement as evidence.</summary>
    [Fact]
    public void OversizedBytes_EmitWarning_WithMeasurement()
    {
        var analyzer = new ImageRuleAnalyzer();

        var findings = analyzer.EvaluateImages(7, [new ImageFact("https://cdn.example.com/big.jpg", TransferredBytes: 250_000)]);

        var oversized = Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
        Assert.Equal(FindingSeverity.Warning, oversized.Severity);
        Assert.Equal(AuditCategory.Performance, oversized.Category);
        using JsonDocument evidence = JsonDocument.Parse(oversized.EvidenceJson);
        Assert.Equal(250_000, evidence.RootElement.GetProperty("bytes").GetInt64());
        Assert.Equal(200_000, evidence.RootElement.GetProperty("threshold").GetInt64());
    }

    /// <summary>The threshold is strict and locally configurable.</summary>
    [Theory]
    [InlineData(200_000, false)]
    [InlineData(200_001, true)]
    public void ThresholdBoundary_IsStrict(long bytes, bool expectOversized)
    {
        var analyzer = new ImageRuleAnalyzer();

        var findings = analyzer.EvaluateImages(7, [new ImageFact("https://cdn.example.com/img.webp", TransferredBytes: bytes)]);

        if (expectOversized)
        {
            Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
        }
        else
        {
            Assert.DoesNotContain(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
        }
    }

    /// <summary>Configured thresholds govern the rule.</summary>
    [Fact]
    public void ConfiguredThreshold_GovernsOversizedRule()
    {
        var analyzer = new ImageRuleAnalyzer(new ImageRuleOptions(OversizedBytesThreshold: 100_000));

        var findings = analyzer.EvaluateImages(7, [new ImageFact("https://cdn.example.com/img.webp", TransferredBytes: 150_000)]);

        Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
    }

    /// <summary>Non-web formats warn (browsers cannot render them reliably).</summary>
    [Theory]
    [InlineData("https://example.com/photo.bmp", "bmp")]
    [InlineData("https://example.com/scan.tiff", "tiff")]
    [InlineData("https://example.com/design.psd", "psd")]
    [InlineData("https://example.com/iphone.heic", "heic")]
    [InlineData("https://example.com/raw.cr2", "cr2")]
    public void NonWebFormats_EmitWarning(string src, string expectedFormat)
    {
        var analyzer = new ImageRuleAnalyzer();

        var findings = analyzer.EvaluateImages(7, [new ImageFact(src)]);

        var format = Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Format);
        Assert.Equal(FindingSeverity.Warning, format.Severity);
        using JsonDocument evidence = JsonDocument.Parse(format.EvidenceJson);
        Assert.Equal(expectedFormat, evidence.RootElement.GetProperty("format").GetString());
        Assert.Equal("non-web-format", evidence.RootElement.GetProperty("issue").GetString());
    }

    /// <summary>Modern and vector formats pass silently.</summary>
    [Theory]
    [InlineData("https://example.com/photo.webp")]
    [InlineData("https://example.com/photo.avif")]
    [InlineData("https://example.com/icon.svg")]
    [InlineData("https://example.com/photo.jxl")]
    public void ModernFormats_EmitNothing(string src)
    {
        var analyzer = new ImageRuleAnalyzer();

        var findings = analyzer.EvaluateImages(7, [new ImageFact(src)]);

        Assert.Empty(findings);
    }

    /// <summary>Data-URI images contribute decoded payload bytes: an oversized
    /// inline PNG is detectable fully offline through the context path.</summary>
    [Fact]
    public async Task OversizedDataUri_EmitsOversizedWarning_FromContext()
    {
        var payload = new string('A', 280_000); // 280000 * 3 / 4 = 210000 bytes
        var html = FixtureSitePages.Page(
            "Inline image",
            body: $"<img src=\"data:image/png;base64,{payload}\" width=\"800\" height=\"600\">");
        var analyzer = new ImageRuleAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context(html));

        var oversized = Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
        using JsonDocument evidence = JsonDocument.Parse(oversized.EvidenceJson);
        Assert.Equal(210_000, evidence.RootElement.GetProperty("bytes").GetInt64());
        Assert.Equal(800, evidence.RootElement.GetProperty("declaredWidth").GetInt32());
        Assert.Equal(600, evidence.RootElement.GetProperty("declaredHeight").GetInt32());
        Assert.Contains(findings, f => f.RuleKey == ImageRuleKeys.Format); // png legacy notice
    }

    /// <summary>Small data URIs earn only the format check.</summary>
    [Fact]
    public async Task SmallDataUri_EmitsFormatOnly()
    {
        var html = FixtureSitePages.Page(
            "Inline image",
            body: "<img src=\"data:image/webp;base64,QUJDRA==\">");
        var analyzer = new ImageRuleAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context(html));

        Assert.Empty(findings); // webp is modern, 3 bytes is small
    }

    /// <summary>Unknown or absent extensions produce no format finding.</summary>
    [Theory]
    [InlineData("https://example.com/image")]
    [InlineData("https://example.com/photo.xyz")]
    [InlineData("https://example.com/generate?id=5")]
    public void UnknownFormat_EmitsNothing(string src)
    {
        var analyzer = new ImageRuleAnalyzer();

        var findings = analyzer.EvaluateImages(7, [new ImageFact(src)]);

        Assert.Empty(findings);
    }

    /// <summary>Query strings and fragments do not hide the extension.</summary>
    [Fact]
    public void QueryAndFragment_AreStrippedForFormatDetection()
    {
        var analyzer = new ImageRuleAnalyzer();

        var findings = analyzer.EvaluateImages(7, [new ImageFact("https://example.com/photo.jpg?v=2#anchor")]);

        var format = Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Format);
        using JsonDocument evidence = JsonDocument.Parse(format.EvidenceJson);
        Assert.Equal("jpeg", evidence.RootElement.GetProperty("format").GetString());
    }

    /// <summary>Repeated img elements referencing one source report once.</summary>
    [Fact]
    public async Task RepeatedSource_ReportedOnce()
    {
        var html = FixtureSitePages.Page(
            "Repeated",
            body: "<img src=\"/assets/logo.png\"><img src=\"/assets/logo.png\">");
        var analyzer = new ImageRuleAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context(html));

        Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Format);
    }

    /// <summary>Modern-format recommendations are configurable; non-web
    /// warnings are unconditional.</summary>
    [Fact]
    public void RecommendModernFormatsOff_SuppressesNoticesOnly()
    {
        var analyzer = new ImageRuleAnalyzer(new ImageRuleOptions(RecommendModernFormats: false));

        var findings = analyzer.EvaluateImages(7, [
            new ImageFact("https://example.com/a.jpg"),
            new ImageFact("https://example.com/b.bmp"),
        ]);

        Assert.DoesNotContain(findings, f => f.RuleKey == ImageRuleKeys.Format && f.Severity == FindingSeverity.Notice);
        Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Format && f.Severity == FindingSeverity.Warning);
    }

    /// <summary>No document (never parsed) yields no findings.</summary>
    [Fact]
    public async Task NoDocument_EmitsNothing()
    {
        var analyzer = new ImageRuleAnalyzer();
        var context = ContextFactory.Create(document: null);

        var findings = await analyzer.AnalyzeAsync(context);

        Assert.Empty(findings);
    }

    /// <summary>Deterministic re-run and concurrent safety.</summary>
    [Fact]
    public async Task Analysis_IsDeterministic_AndThreadSafe()
    {
        var analyzer = new ImageRuleAnalyzer();
        var context = Context(FixtureSitePages.ImagesHtml);

        var first = await analyzer.AnalyzeAsync(context);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => analyzer.AnalyzeAsync(context))));

        Assert.All(concurrent, findings =>
        {
            Assert.Equal(first.Count, findings.Count);
            for (var i = 0; i < first.Count; i++)
            {
                Assert.Equal(first[i].RuleKey, findings[i].RuleKey);
                Assert.Equal(first[i].EvidenceJson, findings[i].EvidenceJson);
            }
        });
    }
}
