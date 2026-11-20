using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Tests.ContentQuality;
using CrawlForge.Audit.Tests.Indexability.TestSupport;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Indexability;

/// <summary>
/// CR-024 IndexabilityStateEvaluator tests (04/01 §7.4 as amended by CR-024,
/// D-020): indexable means SEARCH-indexable — final status 200 AND not
/// robots-blocked AND no noindex in meta robots OR X-Robots-Tag AND canonical
/// self-referencing or absent. Covers the six required cases plus directive
/// expansion, agent scopes, canonical normalization/conflicts, the engine
/// write-path factory, and the probe-2 noindex defect regression.
/// </summary>
public class IndexabilityStateEvaluatorTests
{
    private const string PageUrl = IndexabilityContextFactory.PageUrl;

    private static IndexabilityStateInput Input(
        int? status = 200,
        bool robotsBlocked = false,
        IReadOnlyList<HtmlRobotsDirective>? meta = null,
        IReadOnlyList<RobotsHeaderDirective>? header = null,
        IReadOnlyList<string>? canonicals = null,
        string pageUrl = PageUrl) =>
        new(status, robotsBlocked, meta ?? [], header ?? [], canonicals ?? [], pageUrl);

    private static HtmlRobotsDirective Meta(string directives, string agent = "*") =>
        new(agent, directives.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(d => d.ToLowerInvariant()).ToArray(), directives);

    private static RobotsHeaderDirective Header(string directives, string agent = "*") =>
        new(agent, directives.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(d => d.ToLowerInvariant()).ToArray(), directives);

    // ---- The six required CR-024 cases ------------------------------------

    /// <summary>Case 1: clean fetchable page — 200, robots allowed, no
    /// directives, no canonical — is search-indexable.</summary>
    [Fact]
    public void CleanPage_IsIndexable()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input());

        Assert.True(state.IsIndexable);
        Assert.Equal(IndexabilityFlags.None, state.Flags);
        Assert.Empty(state.Reasons);
    }

    /// <summary>Case 2: meta-robots noindex disqualifies even though the page
    /// is fetchable (the probe-2 noindex.html defect: is_indexable was 1).</summary>
    [Fact]
    public void MetaRobotsNoindex_IsNotIndexable()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(meta: [Meta("noindex, follow")]));

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.Noindex));
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.Nofollow) is false);
        Assert.Contains(IndexabilityStateEvaluator.ReasonNoindexMetaRobots, state.Reasons);
    }

    /// <summary>Case 3: X-Robots-Tag noindex disqualifies with no HTML meta.</summary>
    [Fact]
    public void XRobotsTagNoindex_IsNotIndexable()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(header: [Header("noindex")]));

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.Noindex));
        Assert.Contains(IndexabilityStateEvaluator.ReasonNoindexXRobotsTag, state.Reasons);
    }

    /// <summary>Case 4: robots-blocked URLs are not search-indexable.</summary>
    [Fact]
    public void RobotsBlocked_IsNotIndexable()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(robotsBlocked: true));

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.RobotsBlocked));
        Assert.Contains(IndexabilityStateEvaluator.ReasonRobotsBlocked, state.Reasons);
    }

    /// <summary>Case 5: non-200 final statuses are not search-indexable.</summary>
    [Theory]
    [InlineData(301)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(204)]
    public void Non200Status_IsNotIndexable(int status)
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(status: status));

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.NonIndexableStatus));
        Assert.Contains(IndexabilityStateEvaluator.ReasonFinalStatusNot200, state.Reasons);
    }

    /// <summary>Case 6: a canonical pointing at another URL disqualifies.</summary>
    [Fact]
    public void CanonicalToOtherUrl_IsNotIndexable()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(canonicals: ["https://example.com/other"]));

        Assert.False(state.IsIndexable);
        Assert.Contains(IndexabilityStateEvaluator.ReasonCanonicalToOtherUrl, state.Reasons);
    }

    // ---- Canonical self-reference / absence -------------------------------

    /// <summary>A self-referencing canonical keeps the page indexable.</summary>
    [Fact]
    public void SelfCanonical_IsIndexable()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(canonicals: [PageUrl]));

        Assert.True(state.IsIndexable);
        Assert.Empty(state.Reasons);
    }

    /// <summary>Relative and trailing-slash variants of the self URL normalize
    /// to a self-reference (matching IndexabilityAnalyzer's canonical checks).</summary>
    [Theory]
    [InlineData("/page")]
    [InlineData("page")]
    [InlineData("https://example.com/page/")]
    [InlineData("https://EXAMPLE.com/page")]
    [InlineData("")]
    public void SelfCanonicalVariants_AreIndexable(string href)
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(canonicals: [href]));

        Assert.True(state.IsIndexable);
        Assert.Empty(state.Reasons);
    }

    /// <summary>Multiple DISTINCT canonical targets are an unresolved conflict
    /// (§15.4: never auto-resolve) and disqualify.</summary>
    [Fact]
    public void MultipleDistinctCanonicals_AreNotIndexable()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(canonicals: [PageUrl, "https://example.com/other"]));

        Assert.False(state.IsIndexable);
        Assert.Contains(IndexabilityStateEvaluator.ReasonCanonicalMultiple, state.Reasons);
    }

    /// <summary>HTML and header canonicals resolving to the SAME URL count as
    /// one (agreement is a Pass per §15.4, not a conflict).</summary>
    [Fact]
    public void HtmlAndHeaderCanonicalAgreement_IsIndexable()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(canonicals: [PageUrl, "/page"]));

        Assert.True(state.IsIndexable);
        Assert.Empty(state.Reasons);
    }

    /// <summary>An unresolvable or non-http(s) canonical is neither
    /// self-referencing nor absent: it disqualifies. (A nonsense-but-parseable
    /// relative href like "not a url at all" resolves to another URL and is
    /// covered by the canonical-to-other-url case instead.)</summary>
    [Theory]
    [InlineData("http://")]
    [InlineData("https://[bad")]
    [InlineData("ftp://example.com/page")]
    [InlineData("mailto:seo@example.com")]
    public void InvalidCanonical_IsNotIndexable(string href)
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(canonicals: [href]));

        Assert.False(state.IsIndexable);
        Assert.Contains(IndexabilityStateEvaluator.ReasonCanonicalInvalid, state.Reasons);
    }

    // ---- Directive semantics ----------------------------------------------

    /// <summary>"none" expands to noindex+nofollow: not indexable, both flags.</summary>
    [Fact]
    public void NoneDirective_ExpandsToNoindexAndNofollow()
    {
        var metaState = IndexabilityStateEvaluator.Evaluate(Input(meta: [Meta("none")]));
        var headerState = IndexabilityStateEvaluator.Evaluate(Input(header: [Header("none")]));

        Assert.False(metaState.IsIndexable);
        Assert.True(metaState.Flags.HasFlag(IndexabilityFlags.Noindex));
        Assert.True(metaState.Flags.HasFlag(IndexabilityFlags.Nofollow));
        Assert.False(headerState.IsIndexable);
        Assert.True(headerState.Flags.HasFlag(IndexabilityFlags.Noindex));
    }

    /// <summary>nofollow alone restricts link following, not indexing.</summary>
    [Fact]
    public void NofollowOnly_RemainsIndexable_WithFlag()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(meta: [Meta("nofollow")]));

        Assert.True(state.IsIndexable);
        Assert.Equal(IndexabilityFlags.Nofollow, state.Flags);
        Assert.Empty(state.Reasons);
    }

    /// <summary>Agent-scoped noindex (googlebot) still disqualifies: the URL is
    /// not indexable by that search engine.</summary>
    [Fact]
    public void AgentScopedNoindex_IsNotIndexable()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(
            meta: [Meta("noindex", agent: "googlebot")],
            header: [Header("noindex", agent: "bingbot")]));

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.Noindex));
        Assert.Contains(IndexabilityStateEvaluator.ReasonNoindexMetaRobots, state.Reasons);
        Assert.Contains(IndexabilityStateEvaluator.ReasonNoindexXRobotsTag, state.Reasons);
    }

    /// <summary>"index, follow" and "all" are affirmative: indexable.</summary>
    [Theory]
    [InlineData("index, follow")]
    [InlineData("all")]
    public void AffirmativeDirectives_AreIndexable(string directives)
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(meta: [Meta(directives)], header: [Header(directives)]));

        Assert.True(state.IsIndexable);
        Assert.Equal(IndexabilityFlags.None, state.Flags);
    }

    /// <summary>noindex in BOTH sources sets the Noindex flag once and records
    /// both reason codes (dual-source evidence).</summary>
    [Fact]
    public void NoindexInBothSources_RecordsBothReasons()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(meta: [Meta("noindex")], header: [Header("noindex")]));

        Assert.False(state.IsIndexable);
        Assert.Equal(IndexabilityFlags.Noindex, state.Flags);
        Assert.Equal(
            [IndexabilityStateEvaluator.ReasonNoindexMetaRobots, IndexabilityStateEvaluator.ReasonNoindexXRobotsTag],
            state.Reasons);
    }

    /// <summary>Unknown/never-fetched URLs (null status) are not indexable.</summary>
    [Fact]
    public void NullStatus_IsNotIndexable()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(status: null));

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.NonIndexableStatus));
    }

    /// <summary>All disqualifiers compose: every flag and reason is present.</summary>
    [Fact]
    public void AllDisqualifiers_ComposeFlagsAndReasons()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(
            status: 404,
            robotsBlocked: true,
            meta: [Meta("noindex, nofollow")],
            canonicals: ["https://example.com/other"]));

        Assert.False(state.IsIndexable);
        Assert.Equal(
            IndexabilityFlags.NonIndexableStatus | IndexabilityFlags.RobotsBlocked
            | IndexabilityFlags.Noindex | IndexabilityFlags.Nofollow
            | IndexabilityFlags.CanonicalElsewhere,
            state.Flags);
        Assert.Equal(
            [
                IndexabilityStateEvaluator.ReasonFinalStatusNot200,
                IndexabilityStateEvaluator.ReasonRobotsBlocked,
                IndexabilityStateEvaluator.ReasonNoindexMetaRobots,
                IndexabilityStateEvaluator.ReasonCanonicalToOtherUrl,
            ],
            state.Reasons);
    }

    // ---- Context / fetch factories ----------------------------------------

    /// <summary>FromContext reads HTML directives and the stored fetch headers;
    /// a noindex meta in the document disqualifies.</summary>
    [Fact]
    public void FromContext_ReadsDocumentAndHeaders()
    {
        var parser = new AngleSharpDomParser();
        var document = parser.Parse(FixtureSitePages.NoindexHtml, PageUrl);
        var context = IndexabilityContextFactory.Context(document: document);

        var state = IndexabilityStateEvaluator.EvaluateContext(context);

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.Noindex));
        Assert.Contains(IndexabilityStateEvaluator.ReasonNoindexMetaRobots, state.Reasons);
    }

    /// <summary>Rendered-first (CR-009 philosophy): a rendered-only noindex
    /// disqualifies even when the response document is clean (§7.18 treats
    /// rendered-only noindex as critical).</summary>
    [Fact]
    public void FromContext_PrefersRenderedDocument()
    {
        var parser = new AngleSharpDomParser();
        var response = parser.Parse(FixtureSitePages.RedirectTargetHtml, PageUrl);
        var rendered = parser.Parse(FixtureSitePages.NoindexHtml, PageUrl);
        var context = IndexabilityContextFactory.Context(document: response, renderedDocument: rendered);

        var state = IndexabilityStateEvaluator.EvaluateContext(context);

        Assert.False(state.IsIndexable);
        Assert.Contains(IndexabilityStateEvaluator.ReasonNoindexMetaRobots, state.Reasons);
    }

    /// <summary>
    /// PROBE-2 DEFECT REGRESSION (G-NOINDEX-FLAG-QUESTION): the engine write
    /// path (NewUrlRecord) held only fetchability facts and stored
    /// is_indexable=1 for the noindex fixture. EvaluateFetch — the exact
    /// factory the lead wires at AuditExecutionEngine.cs:1574 — MUST return
    /// false for the real noindex.html fixture body.
    /// </summary>
    [Fact]
    public void EvaluateFetch_RealNoindexFixture_IsNotIndexable()
    {
        var fetch = IndexabilityContextFactory.Fetch(
            url: FixtureSitePages.BaseUrl + "/pages/noindex.html",
            body: FixtureSitePages.NoindexHtml);

        var state = IndexabilityStateEvaluator.EvaluateFetch(
            fetch.FinalUrl, fetch, RobotsState.Allowed, new AngleSharpDomParser());

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.Noindex));
        Assert.Contains(IndexabilityStateEvaluator.ReasonNoindexMetaRobots, state.Reasons);
    }

    /// <summary>EvaluateFetch without a parser still observes status, robots,
    /// and X-Robots-Tag (the header-only subset available pre-parse).</summary>
    [Fact]
    public void EvaluateFetch_WithoutParser_ObservesHeadersAndStatus()
    {
        var headers = new Dictionary<string, string> { ["X-Robots-Tag"] = "noindex" };
        var fetch = IndexabilityContextFactory.Fetch(headers: headers, body: FixtureSitePages.NoindexHtml);

        var state = IndexabilityStateEvaluator.EvaluateFetch(PageUrl, fetch, RobotsState.Allowed);

        Assert.False(state.IsIndexable);
        Assert.Contains(IndexabilityStateEvaluator.ReasonNoindexXRobotsTag, state.Reasons);
    }

    /// <summary>EvaluateFetch with a clean fixture page is indexable; a
    /// never-fetched URL (null fetch) is not.</summary>
    [Fact]
    public void EvaluateFetch_CleanFixture_IsIndexable_NullFetch_IsNot()
    {
        var fetch = IndexabilityContextFactory.Fetch(body: FixtureSitePages.RedirectTargetHtml);

        var clean = IndexabilityStateEvaluator.EvaluateFetch(PageUrl, fetch, RobotsState.Allowed, new AngleSharpDomParser());
        var neverFetched = IndexabilityStateEvaluator.EvaluateFetch(PageUrl, null, RobotsState.Unknown);

        Assert.True(clean.IsIndexable);
        Assert.False(neverFetched.IsIndexable);
        Assert.True(neverFetched.Flags.HasFlag(IndexabilityFlags.NonIndexableStatus));
    }

    /// <summary>EvaluateFetch observes header canonicals without a parser:
    /// a Link-header canonical to another URL disqualifies.</summary>
    [Fact]
    public void EvaluateFetch_HeaderCanonicalToOtherUrl_IsNotIndexable()
    {
        var headers = new Dictionary<string, string> { ["Link"] = "</other>; rel=\"canonical\"" };
        var fetch = IndexabilityContextFactory.Fetch(headers: headers);

        var state = IndexabilityStateEvaluator.EvaluateFetch(PageUrl, fetch, RobotsState.Allowed);

        Assert.False(state.IsIndexable);
        Assert.Contains(IndexabilityStateEvaluator.ReasonCanonicalToOtherUrl, state.Reasons);
    }

    /// <summary>Non-HTML responses are not parsed (PDF bodies carry no meta).</summary>
    [Fact]
    public void EvaluateFetch_NonHtmlBody_IsNotParsed()
    {
        var headers = new Dictionary<string, string> { ["Content-Type"] = "application/pdf" };
        var fetch = IndexabilityContextFactory.Fetch(headers: headers, body: "%PDF-1.4 fake");

        var state = IndexabilityStateEvaluator.EvaluateFetch(PageUrl, fetch, RobotsState.Allowed, new AngleSharpDomParser());

        Assert.True(state.IsIndexable);
    }

    // ---- CanonicalElsewhere bit (CR-024 / D-021, WP-10-A1) ------------------

    /// <summary>The locked bit value is 16 (additive WP-10-A1 member).</summary>
    [Fact]
    public void CanonicalElsewhereBit_HasLockedValue16() =>
        Assert.Equal(16, (int)IndexabilityFlags.CanonicalElsewhere);

    /// <summary>A canonical pointing elsewhere sets the CanonicalElsewhere bit
    /// alongside the reason code; the page stays non-indexable.</summary>
    [Theory]
    [InlineData("https://example.com/other")]
    [InlineData("/other")]
    [InlineData("https://EXAMPLE.com/other/")]
    public void CanonicalToOtherUrl_SetsCanonicalElsewhereBit(string href)
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(canonicals: [href]));

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.CanonicalElsewhere));
        Assert.Contains(IndexabilityStateEvaluator.ReasonCanonicalToOtherUrl, state.Reasons);
    }

    /// <summary>Self-referencing or absent canonicals never set the bit.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(PageUrl)]
    [InlineData("/page")]
    [InlineData("https://example.com/page/")]
    public void SelfOrAbsentCanonical_DoesNotSetCanonicalElsewhereBit(string? href)
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(canonicals: href is null ? [] : [href]));

        Assert.True(state.IsIndexable);
        Assert.False(state.Flags.HasFlag(IndexabilityFlags.CanonicalElsewhere));
    }

    /// <summary>Conflicting canonicals set the bit when at least one target is
    /// elsewhere (a self-target among conflicts does not neutralize it).</summary>
    [Fact]
    public void MultipleCanonicals_WithElsewhereTarget_SetTheBit()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(canonicals: [PageUrl, "https://example.com/other"]));

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.CanonicalElsewhere));
        Assert.Contains(IndexabilityStateEvaluator.ReasonCanonicalMultiple, state.Reasons);
    }

    /// <summary>Conflicting canonicals that all point elsewhere set the bit.</summary>
    [Fact]
    public void MultipleElsewhereCanonicals_SetTheBit()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(canonicals: ["https://example.com/x", "https://example.com/y"]));

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.CanonicalElsewhere));
    }

    /// <summary>An invalid/unresolvable canonical disqualifies via reasons but
    /// does NOT set CanonicalElsewhere (no observable elsewhere target).</summary>
    [Theory]
    [InlineData("http://")]
    [InlineData("ftp://example.com/page")]
    [InlineData("mailto:seo@example.com")]
    public void InvalidCanonical_DoesNotSetCanonicalElsewhereBit(string href)
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(canonicals: [href]));

        Assert.False(state.IsIndexable);
        Assert.False(state.Flags.HasFlag(IndexabilityFlags.CanonicalElsewhere));
        Assert.Contains(IndexabilityStateEvaluator.ReasonCanonicalInvalid, state.Reasons);
    }

    /// <summary>The bit composes with the other flags without changing them.</summary>
    [Fact]
    public void CanonicalElsewhere_ComposesWithNoindex()
    {
        var state = IndexabilityStateEvaluator.Evaluate(Input(
            meta: [Meta("noindex")],
            canonicals: ["https://example.com/other"]));

        Assert.False(state.IsIndexable);
        Assert.Equal(IndexabilityFlags.Noindex | IndexabilityFlags.CanonicalElsewhere, state.Flags);
    }

    /// <summary>The engine write-path factory carries the bit: a Link-header
    /// canonical to another URL sets it pre-parse.</summary>
    [Fact]
    public void EvaluateFetch_HeaderCanonicalElsewhere_SetsTheBit()
    {
        var headers = new Dictionary<string, string> { ["Link"] = "</other>; rel=\"canonical\"" };
        var fetch = IndexabilityContextFactory.Fetch(headers: headers);

        var state = IndexabilityStateEvaluator.EvaluateFetch(PageUrl, fetch, RobotsState.Allowed);

        Assert.False(state.IsIndexable);
        Assert.True(state.Flags.HasFlag(IndexabilityFlags.CanonicalElsewhere));
    }

    // ---- Determinism / thread safety ---------------------------------------

    /// <summary>Pure function: repeated evaluation is byte-identical.</summary>
    [Fact]
    public void Evaluation_IsDeterministic()
    {
        var input = Input(status: 404, meta: [Meta("noindex")], canonicals: ["https://example.com/other"]);

        var first = IndexabilityStateEvaluator.Evaluate(input);
        var second = IndexabilityStateEvaluator.Evaluate(input);

        Assert.Equal(first, second);
    }

    /// <summary>Concurrent evaluation on shared inputs is safe (engine
    /// MaxConcurrency=2 write path).</summary>
    [Fact]
    public async Task ConcurrentEvaluation_IsThreadSafe()
    {
        var input = Input(meta: [Meta("noindex, follow")], canonicals: [PageUrl]);
        var expected = IndexabilityStateEvaluator.Evaluate(input);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => IndexabilityStateEvaluator.Evaluate(input))));

        Assert.All(results, state => Assert.Equal(expected, state));
    }
}
