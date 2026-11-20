using System.Globalization;
using System.Text;
using System.Text.Json;
using CrawlForge.Audit.ContentQuality;
using CrawlForge.Audit.Tests.StructuredData.Support;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Xunit;

namespace CrawlForge.Audit.Tests.Scoring;

/// <summary>
/// D-021/CR-031 component stand-ins for the phase-gate checks (WP-10-05):
/// missing-alt appears UNDER IMAGES with the exact locked key
/// <c>accessibility.image-alt</c> (coordinator-approved takeover of the
/// accessibility rule — identical semantics and evidence shape), and the
/// oversized-image check uses transferred bytes where captured (stored
/// NetworkWaterfall artifact, fake repositories here) else reports
/// "not measured" — NEVER a pass. Placement note: the ContentQuality test
/// folder is outside this workpiece's owned paths, so the image component
/// tests live in the owned Scoring folder (category conformance is a scoring
/// concern); the unowned ImageRuleAnalyzerTests updates are a handoff item.
/// </summary>
public class ImagesCategoryComponentTests
{
    private const string PageUrl = "https://example.test/images.html";
    private const string AltKey = "accessibility.image-alt";

    private static Contracts.Audit.PageAuditContext Context(
        string html,
        string? renderedHtml = null,
        long urlId = 7,
        long auditId = 3) =>
        FixtureAuditContext.Create(html, url: PageUrl, renderedHtml: renderedHtml, urlId: urlId, auditId: auditId);

    // ---- D-021: missing-alt under Images ------------------------------------

    /// <summary>Phase-gate stand-in: an img without alt emits the EXACT locked
    /// key under the Images category with Error severity and the accessibility
    /// evidence shape (locator, remediation, element details).</summary>
    [Fact]
    public async Task MissingAlt_EmitsLockedKeyUnderImages_WithAccessibilityEvidence()
    {
        var analyzer = new ImageRuleAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context("<html><body><img src=\"/assets/logo.png\"></body></html>"));

        var alt = Assert.Single(findings, f => f.RuleKey == AltKey);
        Assert.Equal(AuditCategory.Images, alt.Category);
        Assert.Equal(FindingSeverity.Error, alt.Severity);
        Assert.Equal(7, alt.UrlId);
        Assert.Equal(AltKey, alt.RecommendationKey);

        using var evidence = JsonDocument.Parse(alt.EvidenceJson);
        var root = evidence.RootElement;
        Assert.Equal(AltKey, root.GetProperty("ruleKey").GetString());
        Assert.Equal("images", root.GetProperty("family").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("ruleSetVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("locator").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("xPath").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("remediation").GetString()));
        Assert.Equal("Image has no alt attribute.", root.GetProperty("message").GetString());
        Assert.Equal("img", root.GetProperty("details").GetProperty("element").GetString());
        Assert.Equal("/assets/logo.png", root.GetProperty("details").GetProperty("src").GetString());
    }

    /// <summary>Every exemption of the accessibility rule is preserved by the
    /// takeover (decorative markers, present alt including empty, figcaption).</summary>
    [Theory]
    [InlineData("<img src=\"/a.png\" role=\"presentation\">")]
    [InlineData("<img src=\"/a.png\" role=\"none\">")]
    [InlineData("<img src=\"/a.png\" aria-hidden=\"true\">")]
    [InlineData("<img src=\"/a.png\" alt=\"\">")]
    [InlineData("<img src=\"/a.png\" alt=\"A logo\">")]
    [InlineData("<figure><img src=\"/a.png\"><figcaption>A descriptive caption</figcaption></figure>")]
    public async Task AltExemptions_ArePreserved(string markup)
    {
        var analyzer = new ImageRuleAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context($"<html><body>{markup}</body></html>"));

        Assert.DoesNotContain(findings, f => f.RuleKey == AltKey);
    }

    /// <summary>input[type=image] and area coverage matches the accessibility
    /// rule exactly (element details distinguish them).</summary>
    [Fact]
    public async Task InputImageAndArea_AreCoveredWithElementDetails()
    {
        var analyzer = new ImageRuleAnalyzer();
        var html = """
            <html><body>
            <input type="image" src="/go.png">
            <map name="m"><area shape="rect" coords="0,0,10,10" href="/x"></map>
            <input type="image" src="/ok.png" alt="Go">
            </body></html>
            """;

        var findings = await analyzer.AnalyzeAsync(Context(html));

        var alts = findings.Where(f => f.RuleKey == AltKey).ToArray();
        Assert.Equal(2, alts.Length);
        Assert.All(alts, f => Assert.Equal(AuditCategory.Images, f.Category));
        var elements = alts
            .Select(f => JsonDocument.Parse(f.EvidenceJson).RootElement.GetProperty("details").GetProperty("element").GetString())
            .OrderBy(e => e)
            .ToArray();
        Assert.Equal(new[] { "area", "input[type=image]" }, elements);
    }

    /// <summary>Rendered-first (CR-009): a rendered-only image without alt is
    /// flagged even when the response document is clean.</summary>
    [Fact]
    public async Task RenderedOnlyImage_MissingAlt_IsFlagged()
    {
        var analyzer = new ImageRuleAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context(
            "<html><body><p>clean</p></body></html>",
            renderedHtml: "<html><body><img src=\"/late.png\"></body></html>"));

        Assert.Contains(findings, f => f.RuleKey == AltKey && f.Category == AuditCategory.Images);
    }

    /// <summary>EVERY image-family finding (payload, format, alt) carries the
    /// Images category — the D-021 move is complete, not partial.</summary>
    [Fact]
    public async Task AllImageFamilyFindings_UseImagesCategory()
    {
        var analyzer = new ImageRuleAnalyzer();
        var payload = new string('A', 280_000); // 210000 decoded bytes > 200000
        var html = $"<html><body><img src=\"data:image/png;base64,{payload}\"><img src=\"/scan.bmp\"></body></html>";

        var findings = await analyzer.AnalyzeAsync(Context(html));

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal(AuditCategory.Images, f.Category));
        Assert.Contains(findings, f => f.RuleKey == ImageRuleKeys.Oversized && f.Severity == FindingSeverity.Warning);
        Assert.Contains(findings, f => f.RuleKey == ImageRuleKeys.Format && f.Severity == FindingSeverity.Warning);
        Assert.Contains(findings, f => f.RuleKey == AltKey);
    }

    // ---- CR-031: transferred bytes where captured, else not measured --------

    /// <summary>Stored NetworkWaterfall artifact bytes measure a network image:
    /// relative src resolves against the page URL; the finding is a measured
    /// Warning naming the byte source.</summary>
    [Fact]
    public async Task WaterfallArtifact_MeasuresOversizedNetworkImage()
    {
        var (repository, store) = FakeArtifacts();
        await PutWaterfallAsync(repository, store, auditId: 3, urlId: 7, $$"""
            [
              { "url": "https://example.test/assets/big.jpg", "resourceType": "image", "method": "GET", "statusCode": 200, "transferredBytes": 250000 },
              { "url": "https://example.test/assets/small.jpg", "resourceType": "image", "method": "GET", "statusCode": 200, "transferredBytes": 1000 }
            ]
            """);
        var analyzer = new ImageRuleAnalyzer(artifacts: repository, contentStore: store);
        var html = "<html><body><img src=\"/assets/big.jpg\" alt=\"big\"><img src=\"/assets/small.jpg\" alt=\"small\"></body></html>";

        var findings = await analyzer.AnalyzeAsync(Context(html));

        var oversized = Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
        Assert.Equal(FindingSeverity.Warning, oversized.Severity);
        Assert.Equal(AuditCategory.Images, oversized.Category);
        using var evidence = JsonDocument.Parse(oversized.EvidenceJson);
        var root = evidence.RootElement;
        Assert.Equal("measured", root.GetProperty("measurement").GetString());
        Assert.Equal("network-waterfall", root.GetProperty("bytesSource").GetString());
        Assert.Equal(250000, root.GetProperty("bytes").GetInt64());
        Assert.Equal("/assets/big.jpg", root.GetProperty("src").GetString());
    }

    /// <summary>Without capture bytes the check reports NOT MEASURED (Notice)
    /// — never a silent pass and never a Pass-severity finding.</summary>
    [Fact]
    public async Task NoCaptureBytes_ReportsNotMeasured_NeverPass()
    {
        var analyzer = new ImageRuleAnalyzer();

        var findings = await analyzer.AnalyzeAsync(Context("<html><body><img src=\"/assets/photo.jpg\" alt=\"p\"></body></html>"));

        var oversized = Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
        Assert.Equal(FindingSeverity.Notice, oversized.Severity);
        Assert.DoesNotContain(findings, f => f.RuleKey == ImageRuleKeys.Oversized && f.Severity == FindingSeverity.Pass);
        using var evidence = JsonDocument.Parse(oversized.EvidenceJson);
        Assert.Equal("not-measured", evidence.RootElement.GetProperty("measurement").GetString());
        Assert.False(string.IsNullOrWhiteSpace(evidence.RootElement.GetProperty("reason").GetString()));
    }

    /// <summary>TransferredBytes=0 means unknown per the locked contract —
    /// the image stays not measured.</summary>
    [Fact]
    public async Task ZeroTransferredBytes_IsUnknown_NotMeasured()
    {
        var (repository, store) = FakeArtifacts();
        await PutWaterfallAsync(repository, store, 3, 7, """
            [ { "url": "https://example.test/assets/photo.jpg", "resourceType": "image", "method": "GET", "statusCode": 200, "transferredBytes": 0 } ]
            """);
        var analyzer = new ImageRuleAnalyzer(artifacts: repository, contentStore: store);

        var findings = await analyzer.AnalyzeAsync(Context("<html><body><img src=\"/assets/photo.jpg\" alt=\"p\"></body></html>"));

        var oversized = Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
        using var evidence = JsonDocument.Parse(oversized.EvidenceJson);
        Assert.Equal("not-measured", evidence.RootElement.GetProperty("measurement").GetString());
    }

    /// <summary>The wrapped artifact payload shape ({"networkWaterfall":[...]})
    /// reads identically to the bare JSON array.</summary>
    [Fact]
    public async Task WrappedWaterfallPayload_IsRead()
    {
        var (repository, store) = FakeArtifacts();
        await PutWaterfallAsync(repository, store, 3, 7, """
            { "networkWaterfall": [ { "url": "https://example.test/assets/big.jpg", "resourceType": "image", "method": "GET", "statusCode": 200, "transferredBytes": 300000 } ] }
            """);
        var analyzer = new ImageRuleAnalyzer(artifacts: repository, contentStore: store);

        var findings = await analyzer.AnalyzeAsync(Context("<html><body><img src=\"/assets/big.jpg\" alt=\"b\"></body></html>"));

        var oversized = Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
        Assert.Equal(FindingSeverity.Warning, oversized.Severity);
        using var evidence = JsonDocument.Parse(oversized.EvidenceJson);
        Assert.Equal(300000, evidence.RootElement.GetProperty("bytes").GetInt64());
    }

    /// <summary>Inline base64 data-URI payloads are measured fully offline.</summary>
    [Fact]
    public async Task DataUriPayload_IsMeasuredOffline()
    {
        var analyzer = new ImageRuleAnalyzer();
        var payload = new string('A', 280_000); // 210000 bytes

        var findings = await analyzer.AnalyzeAsync(Context($"<html><body><img src=\"data:image/png;base64,{payload}\" alt=\"inline\"></body></html>"));

        var oversized = Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
        Assert.Equal(FindingSeverity.Warning, oversized.Severity);
        using var evidence = JsonDocument.Parse(oversized.EvidenceJson);
        Assert.Equal("measured", evidence.RootElement.GetProperty("measurement").GetString());
        Assert.Equal("data-uri", evidence.RootElement.GetProperty("bytesSource").GetString());
        Assert.Equal(210000, evidence.RootElement.GetProperty("bytes").GetInt64());
    }

    /// <summary>A measured image within budget is a genuine pass: no oversized
    /// finding at all (the not-measured report never masquerades as one).</summary>
    [Fact]
    public async Task MeasuredWithinBudget_EmitsNoOversizedFinding()
    {
        var (repository, store) = FakeArtifacts();
        await PutWaterfallAsync(repository, store, 3, 7, """
            [ { "url": "https://example.test/assets/small.jpg", "resourceType": "image", "method": "GET", "statusCode": 200, "transferredBytes": 50000 } ]
            """);
        var analyzer = new ImageRuleAnalyzer(artifacts: repository, contentStore: store);

        var findings = await analyzer.AnalyzeAsync(Context("<html><body><img src=\"/assets/small.jpg\" alt=\"s\"></body></html>"));

        Assert.DoesNotContain(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
    }

    /// <summary>Artifacts of OTHER urls/audits/kinds never measure this page's
    /// images (the query filters auditId+urlId+kind).</summary>
    [Fact]
    public async Task ForeignArtifacts_DoNotMeasureThisUrl()
    {
        var (repository, store) = FakeArtifacts();
        await PutWaterfallAsync(repository, store, auditId: 99, urlId: 7, """
            [ { "url": "https://example.test/assets/big.jpg", "resourceType": "image", "method": "GET", "statusCode": 200, "transferredBytes": 250000 } ]
            """);
        await PutWaterfallAsync(repository, store, auditId: 3, urlId: 42, """
            [ { "url": "https://example.test/assets/big.jpg", "resourceType": "image", "method": "GET", "statusCode": 200, "transferredBytes": 250000 } ]
            """);
        var analyzer = new ImageRuleAnalyzer(artifacts: repository, contentStore: store);

        var findings = await analyzer.AnalyzeAsync(Context("<html><body><img src=\"/assets/big.jpg\" alt=\"b\"></body></html>"));

        var oversized = Assert.Single(findings, f => f.RuleKey == ImageRuleKeys.Oversized);
        using var evidence = JsonDocument.Parse(oversized.EvidenceJson);
        Assert.Equal("not-measured", evidence.RootElement.GetProperty("measurement").GetString());
    }

    // ---- Fake stored-artifact stack -----------------------------------------

    private static (FakeArtifactRepository Repository, FakeContentStore Store) FakeArtifacts() =>
        (new FakeArtifactRepository(), new FakeContentStore());

    private static async Task PutWaterfallAsync(
        FakeArtifactRepository repository,
        FakeContentStore store,
        long auditId,
        long urlId,
        string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var artifact = await store.PutAsync(auditId, urlId, ContentArtifactKind.NetworkWaterfall, stream);
        repository.Rows.Add(artifact);
    }

    private sealed class FakeArtifactRepository : IReadRepository<ContentArtifactDto>
    {
        public List<ContentArtifactDto> Rows { get; } = [];

        public Task<ContentArtifactDto?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));

        public Task<PagedResult<ContentArtifactDto>> ListAsync(ListQuery query, CancellationToken cancellationToken = default)
        {
            IEnumerable<ContentArtifactDto> rows = Rows;
            if (query.Filters is not null)
            {
                foreach (var (key, value) in query.Filters)
                {
                    rows = key switch
                    {
                        "auditId" => rows.Where(r => r.AuditId.ToString(CultureInfo.InvariantCulture) == value),
                        "urlId" => rows.Where(r => r.UrlId.ToString(CultureInfo.InvariantCulture) == value),
                        "kind" => rows.Where(r => ((int)r.Kind).ToString(CultureInfo.InvariantCulture) == value),
                        _ => rows,
                    };
                }
            }

            var materialized = rows.ToList();
            var page = materialized.Skip(query.Offset).Take(query.Limit).ToList();
            return Task.FromResult(new PagedResult<ContentArtifactDto>(page, materialized.Count, query.Offset, query.Limit));
        }
    }

    private sealed class FakeContentStore : IContentStore
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
        private long _nextId = 1;

        public async Task<ContentArtifactDto> PutAsync(long auditId, long urlId, ContentArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var storageKey = $"fake/{kind}/{_nextId}";
            _blobs[storageKey] = buffer.ToArray();
            return new ContentArtifactDto(_nextId++, auditId, urlId, kind, storageKey, buffer.Length, "fake-hash");
        }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(_blobs.TryGetValue(storageKey, out var bytes) ? new MemoryStream(bytes) : null);

        public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.Remove(storageKey));
    }
}
