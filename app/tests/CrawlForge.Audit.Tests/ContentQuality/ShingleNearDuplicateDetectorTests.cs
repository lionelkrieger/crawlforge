using CrawlForge.Audit.ContentQuality;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// FS-CRAWL-001 §7.5 near-duplicate evidence: overlapping token shingles,
/// Jaccard similarity, threshold filtering, and scope-aware text consumption —
/// over locked content-store contracts with in-memory doubles (no re-fetch).
/// </summary>
public class ShingleNearDuplicateDetectorTests
{
    private const long AuditId = 42;

    private static (ShingleNearDuplicateDetector Detector, InMemoryContentStore Store, InMemoryArtifactRepository Repository, ScriptedDomParser Parser)
        CreateFixture(ShingleOptions? options = null, RecordingScopeResolver? resolver = null)
    {
        var store = new InMemoryContentStore();
        var repository = new InMemoryArtifactRepository();
        var parser = new ScriptedDomParser();
        var detector = new ShingleNearDuplicateDetector(repository, store, parser, resolver, options);
        return (detector, store, repository, parser);
    }

    private static async Task<long> AddPageAsync(
        InMemoryContentStore store, InMemoryArtifactRepository repository, ScriptedDomParser parser,
        long urlId, string bodyText)
    {
        var html = $"<html><body><p>{bodyText}</p></body></html>";
        parser.Register(html, DomBuilder.Page(title: null, description: null, bodyText));
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
        var artifact = await store.PutAsync(AuditId, urlId, ContentArtifactKind.Body, stream);
        repository.Add(artifact);
        return urlId;
    }

    /// <summary>Identical pages are near-duplicates with similarity 1.0.</summary>
    [Fact]
    public async Task Identical_Pages_Have_Similarity_One()
    {
        var (detector, store, repository, parser) = CreateFixture();
        await AddPageAsync(store, repository, parser, 1, "the quick brown fox jumps over the lazy dog");
        await AddPageAsync(store, repository, parser, 2, "the quick brown fox jumps over the lazy dog");

        var pairs = await detector.FindNearDuplicatesAsync(AuditId, 0.8);

        var pair = Assert.Single(pairs);
        Assert.Equal(1, pair.UrlIdA);
        Assert.Equal(2, pair.UrlIdB);
        Assert.Equal(1.0, pair.Similarity, 6);
    }

    /// <summary>Disjoint pages produce no pairs.</summary>
    [Fact]
    public async Task Disjoint_Pages_Produce_No_Pairs()
    {
        var (detector, store, repository, parser) = CreateFixture();
        await AddPageAsync(store, repository, parser, 1, "alpha beta gamma delta epsilon zeta");
        await AddPageAsync(store, repository, parser, 2, "one two three four five six");

        var pairs = await detector.FindNearDuplicatesAsync(AuditId, 0.1);

        Assert.Empty(pairs);
    }

    /// <summary>Reported similarity equals the Jaccard index of the shingle sets.</summary>
    [Fact]
    public async Task Similarity_Equals_Single_Jaccard_Index()
    {
        var (detector, store, repository, parser) = CreateFixture();
        const string textA = "the quick brown fox jumps over the lazy dog near the river";
        const string textB = "the quick brown fox leaps over the lazy dog near the river";
        await AddPageAsync(store, repository, parser, 1, textA);
        await AddPageAsync(store, repository, parser, 2, textB);

        var pairs = await detector.FindNearDuplicatesAsync(AuditId, 0.2);

        var pair = Assert.Single(pairs);
        var setA = detector.BuildShingles(textA);
        var setB = detector.BuildShingles(textB);
        var intersection = setA.Count(s => setB.Contains(s));
        var union = setA.Count + setB.Count - intersection;
        Assert.Equal((double)intersection / union, pair.Similarity, 6);
        Assert.True(pair.Similarity is > 0.2 and < 1.0);
    }

    /// <summary>The threshold filters out weaker pairs.</summary>
    [Fact]
    public async Task Threshold_Filters_Weak_Pairs()
    {
        var (detector, store, repository, parser) = CreateFixture();
        await AddPageAsync(store, repository, parser, 1, "the quick brown fox jumps over the lazy dog near the river");
        await AddPageAsync(store, repository, parser, 2, "the quick brown fox leaps over the lazy dog near the river");

        var pairs = await detector.FindNearDuplicatesAsync(AuditId, 1.0);

        Assert.Empty(pairs);
    }

    /// <summary>An empty audit yields no pairs.</summary>
    [Fact]
    public async Task Empty_Audit_Yields_No_Pairs()
    {
        var (detector, _, _, _) = CreateFixture();

        var pairs = await detector.FindNearDuplicatesAsync(AuditId, 0.5);

        Assert.Empty(pairs);
    }

    /// <summary>Other audits' artifacts are excluded by the audit filter.</summary>
    [Fact]
    public async Task Other_Audits_Are_Excluded()
    {
        var (detector, store, repository, parser) = CreateFixture();
        await AddPageAsync(store, repository, parser, 1, "the quick brown fox jumps over the lazy dog");
        const string otherHtml = "<html><body><p>the quick brown fox jumps over the lazy dog</p></body></html>";
        parser.Register(otherHtml, DomBuilder.Page(null, null, "the quick brown fox jumps over the lazy dog"));
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(otherHtml));
        var foreign = await store.PutAsync(auditId: 999, urlId: 2, ContentArtifactKind.Body, stream);
        repository.Add(foreign);

        var pairs = await detector.FindNearDuplicatesAsync(AuditId, 0.8);

        Assert.Empty(pairs);
    }

    /// <summary>The MaxUrls bound caps the comparison set.</summary>
    [Fact]
    public async Task Max_Urls_Bounds_The_Pass()
    {
        var (detector, store, repository, parser) = CreateFixture(new ShingleOptions(MaxUrls: 1));
        await AddPageAsync(store, repository, parser, 1, "the quick brown fox jumps over the lazy dog");
        await AddPageAsync(store, repository, parser, 2, "the quick brown fox jumps over the lazy dog");

        var pairs = await detector.FindNearDuplicatesAsync(AuditId, 0.8);

        Assert.Empty(pairs);
    }

    /// <summary>With a scope, shingles consume the Scoped Visible Text Tree:
    /// pages with different boilerplate but identical scoped content match.</summary>
    [Fact]
    public async Task Scope_Aware_Overload_Uses_Scoped_Text()
    {
        var scoped = new ScopedVisibleText("shared core content paragraph", 4, []);
        var resolver = new RecordingScopeResolver(scoped);
        var (detector, store, repository, parser) = CreateFixture(resolver: resolver);
        await AddPageAsync(store, repository, parser, 1, "boilerplate navigation menu alpha");
        await AddPageAsync(store, repository, parser, 2, "boilerplate footer links beta");
        var scope = new ContentScopeDefinition(["main"], ["nav"]);

        var scopedPairs = await detector.FindNearDuplicatesAsync(AuditId, 0.9, scope);
        var baselinePairs = await detector.FindNearDuplicatesAsync(AuditId, 0.9);

        Assert.Single(scopedPairs);
        Assert.Empty(baselinePairs);
        Assert.Equal(2, resolver.Calls.Count);
        Assert.All(resolver.Calls, call => Assert.Same(scope, call.Scope));
    }

    /// <summary>Invalid thresholds are rejected.</summary>
    [Fact]
    public async Task Invalid_Thresholds_Throw()
    {
        var (detector, _, _, _) = CreateFixture();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => detector.FindNearDuplicatesAsync(AuditId, -0.1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => detector.FindNearDuplicatesAsync(AuditId, 1.1));
    }
}
