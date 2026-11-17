using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Intelligence.NGrams;
using Xunit;

namespace CrawlForge.Intelligence.Tests.NGrams;

/// <summary>
/// Analyzer tests over the real SQLite stack (FS-CRAWL-002 §5): per-URL
/// counts/TF, stop-word pipeline, top-K discard-and-count, idempotent
/// re-audit persistence, inbound anchors, materialized site summaries,
/// retention bounds, and density-weighted H1-only primaries.
/// </summary>
public sealed class NGramAnalyzerTests
{
    /// <summary>Per-zone counts and TF are exact and persisted.</summary>
    [Fact]
    public async Task AnalyzeUrl_Computes_Counts_And_TF_Per_Zone()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());

        var input = new NgramUrlInput(auditId, urlId, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "red shoes red shoes" }, [1, 2]);
        var rows = await analyzer.AnalyzeUrlAsync(input);

        Assert.Equal(4, rows.Count);
        var red = rows.Single(r => r.Order == 1 && r.Term == "red");
        Assert.Equal(2, red.Count);
        Assert.Equal(0.5, red.TermFrequency, 6);
        var bigram = rows.Single(r => r.Order == 2 && r.Term == "red shoes");
        Assert.Equal(2, bigram.Count);
        Assert.Equal(2.0 / 3.0, bigram.TermFrequency, 6);
        var persisted = (long)(await stack.ScalarAsync(
            "SELECT COUNT(*) FROM `ngram_frequencies` WHERE `audit_id` = @a AND `url_id` = @u", ("@a", auditId), ("@u", urlId)) ?? 0L);
        Assert.Equal(4, persisted);
    }

    /// <summary>Stop words are removed before n-gram construction (§5.2 step 4).</summary>
    [Fact]
    public async Task AnalyzeUrl_Removes_Stopwords_Before_Ngram_Construction()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());

        var input = new NgramUrlInput(auditId, urlId, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "the red shoes" }, [2]);
        var rows = await analyzer.AnalyzeUrlAsync(input);

        var term = Assert.Single(rows);
        Assert.Equal("red shoes", term.Term);
    }

    /// <summary>Top-K bound discards the tail and counts it — never fails (§5.6).</summary>
    [Fact]
    public async Task AnalyzeUrl_TopK_Discards_And_Counts_Tail()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter(),
            options: new NGramAnalyzerOptions(TopKPerUrlZoneOrder: 2));

        var input = new NgramUrlInput(auditId, urlId, "en",
            new Dictionary<TextZone, string> { [TextZone.Body] = "alpha beta gamma delta" }, [1]);
        var analysis = await analyzer.AnalyzeUrlCoreAsync(input);

        Assert.Equal(2, analysis.Rows.Count);
        Assert.Equal(2, analysis.Counters.TailTermsDiscarded);
        Assert.True(analysis.Counters.AnyDiscarded);
        Assert.Equal(["alpha", "beta"], analysis.Rows.Select(r => r.Term).Order().ToArray());
    }

    /// <summary>Re-audit replaces prior rows for the URL (idempotent persistence).</summary>
    [Fact]
    public async Task AnalyzeUrl_Reaudit_Replaces_Prior_Rows()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());

        await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, urlId, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "alpha beta" }, [1, 2]));
        await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, urlId, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "gamma delta epsilon" }, [1, 2]));

        var persisted = (long)(await stack.ScalarAsync(
            "SELECT COUNT(*) FROM `ngram_frequencies` WHERE `audit_id` = @a AND `url_id` = @u", ("@a", auditId), ("@u", urlId)) ?? 0L);
        Assert.Equal(5, persisted);
        var stale = (long)(await stack.ScalarAsync(
            "SELECT COUNT(*) FROM `ngram_frequencies` WHERE `audit_id` = @a AND `term` = 'alpha'", ("@a", auditId)) ?? 0L);
        Assert.Equal(0, stale);
    }

    /// <summary>Inbound-anchor zone text comes from recorded internal link edges.</summary>
    [Fact]
    public async Task GetInboundAnchorText_Joins_Internal_Edge_Anchors()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var target = await stack.SeedUrlAsync(auditId, "https://example.com/target");
        var source1 = await stack.SeedUrlAsync(auditId, "https://example.com/s1");
        var source2 = await stack.SeedUrlAsync(auditId, "https://example.com/s2");
        await stack.SeedLinkEdgeAsync(auditId, source1, target, "buy shoes");
        await stack.SeedLinkEdgeAsync(auditId, source2, target, "best shoes");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());

        var anchors = await analyzer.GetInboundAnchorTextAsync(auditId, target);

        Assert.Equal("buy shoes\nbest shoes", anchors);
    }

    /// <summary>Site summaries materialize DF, total frequency, and mean TF on the provider.</summary>
    [Fact]
    public async Task SummarizeSite_Materializes_DF_Total_And_MeanTF()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var url1 = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var url2 = await stack.SeedUrlAsync(auditId, "https://example.com/b");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, url1, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "red shoes red" }, [2]));
        await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, url2, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "red shoes" }, [2]));

        var summaries = await analyzer.SummarizeSiteAsync(auditId);

        var redShoes = summaries.Single(s => s.Term == "red shoes" && s.Zone == TextZone.Title && s.Order == 2);
        Assert.Equal(2, redShoes.DocumentFrequency);
        Assert.Equal(2, redShoes.TotalFrequency);
        Assert.Equal(0.75, redShoes.MeanTermFrequency, 6);
        var materialized = (long)(await stack.ScalarAsync(
            "SELECT COUNT(*) FROM `ngram_site_summaries` WHERE `audit_id` = @a", ("@a", auditId)) ?? 0L);
        Assert.Equal(summaries.Count, materialized);
    }

    /// <summary>Retention bound keeps DF≥2 plus top-M; discards are counted (§5.6).</summary>
    [Fact]
    public async Task SummarizeSite_Retention_Bound_Discards_And_Counts()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var url1 = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var url2 = await stack.SeedUrlAsync(auditId, "https://example.com/b");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter(),
            options: new NGramAnalyzerOptions(SiteMinDocumentFrequency: 2, TopMSiteTerms: 0));
        await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, url1, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "red shoes red" }, [2]));
        await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, url2, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "red shoes" }, [2]));

        var (rows, counters) = await analyzer.SummarizeSiteCoreAsync(auditId);

        Assert.All(rows, r => Assert.True(r.DocumentFrequency >= 2));
        Assert.DoesNotContain(rows, r => r.Term == "shoes red");
        Assert.True(counters.SiteRowsDiscarded > 0);
    }

    /// <summary>Primary terms are density-weighted; the H1 weight applies to H1 text only.</summary>
    [Fact]
    public async Task PrimaryTerms_Are_Density_Weighted_And_H1_Only()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        var document = DomBuilder.Page("https://example.com/a", "alpha beta gamma", "delta epsilon zeta", "body text",
            extraHeadings: [DomBuilder.Element("h2", "eta theta iota")]);
        await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, urlId, "en",
            new Dictionary<TextZone, string>
            {
                [TextZone.Title] = "alpha beta gamma",
                [TextZone.Headings] = "delta epsilon zeta\neta theta iota",
            }, [2]));
        analyzer.CachePageFacts(auditId, urlId, document, null);

        var primaries = await analyzer.ComputePrimaryTermsAsync(auditId);

        var url = Assert.Single(primaries);
        var titlePrimary = url.Terms.Single(t => t.Zone == TextZone.Title && t.Order == 2);
        Assert.Equal("alpha beta", titlePrimary.Term);
        var h1Primary = url.Terms.Single(t => t.Zone == TextZone.Headings && t.Order == 2);
        Assert.Equal("delta epsilon", h1Primary.Term);
        Assert.DoesNotContain(url.Terms, t => t.Term is "eta theta" or "theta iota" or "eta" or "theta" or "iota");
        // Equal TF and DF: title weight 2.0 beats H1 weight 1.5.
        Assert.True(titlePrimary.Density > h1Primary.Density);
        Assert.Equal(0.5 * Math.Log(2) * 2.0, titlePrimary.Density, 6);
        Assert.Equal(0.5 * Math.Log(2) * 1.5, h1Primary.Density, 6);
    }

    /// <summary>H1 facts and canonical targets recompute from stored bodies (re-audit, no cache).</summary>
    [Fact]
    public async Task PrimaryTerms_Recompute_From_Stored_Bodies()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var urlId = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        const string html = "<html><head><title>alpha beta gamma</title></head><body><h1>delta epsilon zeta</h1></body></html>";
        var store = new InMemoryContentStore();
        store.Put("body-1", html);
        await stack.SeedArtifactAsync(auditId, urlId, "body-1");
        var parser = new ScriptedDomParser();
        parser.RegisterHtml(html, DomBuilder.Page("https://example.com/a", "alpha beta gamma", "delta epsilon zeta", "body",
            canonicalHref: "https://example.com/target"));
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter(), store, parser);
        await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, urlId, "en",
            new Dictionary<TextZone, string> { [TextZone.Title] = "alpha beta gamma" }, [2]));

        var primaries = await analyzer.ComputePrimaryTermsAsync(auditId);

        var url = Assert.Single(primaries);
        Assert.Contains(url.Terms, t => t.Zone == TextZone.Headings && t.Term == "delta epsilon");
        Assert.Equal("https://example.com/target", url.DeclaredCanonicalTarget);
    }

    /// <summary>primary_term_count counts URLs where the term is primary.</summary>
    [Fact]
    public async Task SummarizeSite_Counts_Primary_Term_URLs()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var auditId = await stack.SeedAuditAsync();
        var url1 = await stack.SeedUrlAsync(auditId, "https://example.com/a");
        var url2 = await stack.SeedUrlAsync(auditId, "https://example.com/b");
        var analyzer = new NGramAnalyzer(stack.Provider, new StopWordFilter());
        foreach (var urlId in new[] { url1, url2 })
        {
            await analyzer.AnalyzeUrlAsync(new NgramUrlInput(auditId, urlId, "en",
                new Dictionary<TextZone, string> { [TextZone.Title] = "alpha beta" }, [2]));
        }

        var summaries = await analyzer.SummarizeSiteAsync(auditId);

        var primary = summaries.Single(s => s.Term == "alpha beta" && s.Zone == TextZone.Title);
        Assert.Equal(2, primary.PrimaryTermCount);
    }
}
