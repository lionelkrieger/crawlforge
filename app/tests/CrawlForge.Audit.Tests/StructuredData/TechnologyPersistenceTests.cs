using CrawlForge.Audit.StructuredData;
using CrawlForge.Audit.Tests.StructuredData.Support;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Audit.Tests.StructuredData;

/// <summary>
/// Real-stack persistence suite: technology detections persist into the
/// migrated technology_detections table with idempotent re-audit semantics,
/// and analyzer findings flow through the shared finding model.
/// </summary>
public class TechnologyPersistenceTests
{
    private static TechnologyDetector CreateDetector() =>
        new(TechnologyFingerprintCatalogue.Load(StructuredDataRealStack.CatalogueAssetPath), new TestDomParser());

    private const string WordPressHtml = """
        <html><head><meta name="generator" content="WordPress 6.4.2" />
        <script src="https://example.test/wp-content/themes/x/app.js"></script></head><body></body></html>
        """;

    [Fact]
    public async Task Detections_PersistAndReadBack()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        var context = StructuredDataTestContext.Create(WordPressHtml, urlId: stack.UrlId, auditId: stack.AuditId);
        var detections = await CreateDetector().DetectAsync(context);
        var recorder = new TechnologyDetectionRecorder(stack.Provider);

        var written = await recorder.RecordAsync(stack.AuditId, stack.UrlId, detections);

        Assert.True(written > 0);
        var stored = await recorder.GetDetectionsAsync(stack.AuditId, stack.UrlId);
        Assert.Contains(stored, d => d.Key == "wordpress" && d.Category == "CMS" && d.Confidence == 1.0);

        var repository = new SqlRepository<TechnologyDetectionDto>(stack.Provider);
        var page = await repository.ListAsync(new ListQuery(0, 100));
        Assert.Equal(written, page.TotalCount);
        Assert.All(page.Items, item =>
        {
            Assert.Equal(stack.AuditId, item.AuditId);
            Assert.Equal(stack.UrlId, item.UrlId);
            Assert.InRange(item.Confidence, 0.0, 1.0);
        });
    }

    [Fact]
    public async Task ReRecording_ReplacesPriorSet_Idempotently()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        var recorder = new TechnologyDetectionRecorder(stack.Provider);
        var first = new[]
        {
            new Contracts.Audit.TechnologyDetectionDraft("wordpress", "CMS", 1.0),
            new Contracts.Audit.TechnologyDetectionDraft("php", "Language", 0.7),
        };
        var second = new[]
        {
            new Contracts.Audit.TechnologyDetectionDraft("wordpress", "CMS", 0.9),
        };

        await recorder.RecordAsync(stack.AuditId, stack.UrlId, first);
        await recorder.RecordAsync(stack.AuditId, stack.UrlId, first);
        await recorder.RecordAsync(stack.AuditId, stack.UrlId, second);

        var stored = await recorder.GetDetectionsAsync(stack.AuditId, stack.UrlId);
        var single = Assert.Single(stored);
        Assert.Equal("wordpress", single.Key);
        Assert.Equal(0.9, single.Confidence);
    }

    [Fact]
    public async Task EmptyDetectionSet_ClearsPriorRows()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        var recorder = new TechnologyDetectionRecorder(stack.Provider);
        await recorder.RecordAsync(stack.AuditId, stack.UrlId,
            [new Contracts.Audit.TechnologyDetectionDraft("nginx", "Web Server", 0.9)]);

        await recorder.RecordAsync(stack.AuditId, stack.UrlId, []);

        Assert.Empty(await recorder.GetDetectionsAsync(stack.AuditId, stack.UrlId));
    }

    [Fact]
    public async Task AnalyzerFindings_FlowThroughSharedFindingModel()
    {
        await using var stack = await StructuredDataRealStack.CreateAsync();
        var context = StructuredDataTestContext.Create(
            """<html><head></head><body><p style="font-size: 9px">tiny</p></body></html>""",
            urlId: stack.UrlId, auditId: stack.AuditId);
        var findings = await new MobileFriendlinessAnalyzer().AnalyzeAsync(context);
        Assert.NotEmpty(findings);

        var repository = new SqlRepository<FindingDto>(stack.Provider);
        foreach (var draft in findings)
        {
            var persisted = await repository.CreateAsync(new FindingDto(
                0, stack.AuditId, draft.RuleKey, draft.Category, draft.Severity,
                draft.UrlId, draft.EvidenceJson, draft.RecommendationKey, false));
            var readBack = await repository.GetByIdAsync(persisted.Id);
            Assert.NotNull(readBack);
            Assert.Equal(draft.RuleKey, readBack!.RuleKey);
            Assert.Equal(AuditCategory.StructuredData, readBack.Category);
            Assert.Equal(draft.EvidenceJson, readBack.EvidenceJson);
        }
    }
}
