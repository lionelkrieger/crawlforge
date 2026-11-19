using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Tests.Waf.Support;
using CrawlForge.Crawler.Waf;
using Xunit;

namespace CrawlForge.Crawler.Tests.Waf;

/// <summary>
/// G26 five-facet upgrade (FS-CRAWL-002 §2.3.1/§2.3.3/§2.4.1): title,
/// dom-marker and script facets join header/cookie/body; the corroboration
/// guard accepts two independent facets OR one high-specificity
/// provider-specific header/cookie marker; generic title-only never blocks;
/// only 2xx HTML responses may transition to waf-challenge.
/// </summary>
public class WafFiveFacetTests
{
    private static WafEvaluationInput Html(string body, int status = 200, string? contentType = "text/html; charset=utf-8")
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (contentType is not null)
        {
            headers["content-type"] = contentType;
        }

        return new WafEvaluationInput("https://example.com/page", status, headers, body);
    }

    private static WafSignature Signature(string key, string[] headerMarkers, string[] bodyMarkers) =>
        new(key, WafProvider.Cloudflare, WafChallengeClass.Interstitial, headerMarkers, bodyMarkers, IsBundled: false);

    /// <summary>One high-specificity header marker alone satisfies the guard (§2.3.3).</summary>
    [Fact]
    public async Task HighSpecificityHeaderAloneBlocks()
    {
        var catalogue = new InMemoryWafCatalogue(Signature("cf-high", ["cf-mitigated~challenge|0.9|high"], []));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var input = Html("<html><body>Ordinary content.</body></html>") with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "text/html",
                ["cf-mitigated"] = "challenge",
            },
        };
        var result = evaluator.Evaluate(input);
        Assert.True(result.IsChallenge);
        Assert.Equal(WafProvider.Cloudflare, result.Provider);
        Assert.Equal(["cf-high"], result.MatchedSignatureKeys);

        using var evidence = JsonDocument.Parse(result.EvidenceJson);
        Assert.Equal(["Header"], evidence.RootElement.GetProperty("matchedFacets").EnumerateArray().Select(e => e.GetString()));
        Assert.True(evidence.RootElement.GetProperty("matches")[0].GetProperty("highSpecificity").GetBoolean());
    }

    /// <summary>One high-specificity cookie marker alone satisfies the guard.</summary>
    [Fact]
    public async Task HighSpecificityCookieAloneBlocks()
    {
        var catalogue = new InMemoryWafCatalogue(Signature("dd-high", ["cookie:datadome|0.9|high"], []));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var input = Html("<html><body>Ordinary content.</body></html>") with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "text/html",
                ["set-cookie"] = "datadome=session; Path=/",
            },
        };
        Assert.True(evaluator.Evaluate(input).IsChallenge);
    }

    /// <summary>An unflagged single header facet still never blocks (baseline guard preserved).</summary>
    [Fact]
    public async Task UnflaggedSingleHeaderStillSuspected()
    {
        var catalogue = new InMemoryWafCatalogue(Signature("cf-plain", ["cf-mitigated~challenge|0.9"], []));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var input = Html("<html><body>Ordinary content.</body></html>") with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "text/html",
                ["cf-mitigated"] = "challenge",
            },
        };
        var result = evaluator.Evaluate(input);
        Assert.False(result.IsChallenge);
        Assert.Equal(["cf-plain"], result.MatchedSignatureKeys);
    }

    /// <summary>Generic title-only matches never block alone — suspected Notice path (§2.3.3).</summary>
    [Fact]
    public async Task TitleOnlyNeverBlocks()
    {
        var catalogue = new InMemoryWafCatalogue(Signature("generic-title", [], ["title:Just a moment|0.95"]));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var result = evaluator.Evaluate(Html("<html><head><title>Just a moment...</title></head><body>Checking your browser.</body></html>"));
        Assert.False(result.IsChallenge);
        Assert.Equal(["generic-title"], result.MatchedSignatureKeys);

        using var evidence = JsonDocument.Parse(result.EvidenceJson);
        Assert.True(evidence.RootElement.GetProperty("suspected").GetBoolean());
        Assert.Equal(["Title"], evidence.RootElement.GetProperty("matchedFacets").EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>Title + script are two independent facets and corroborate.</summary>
    [Fact]
    public async Task TitleAndScriptCorroborate()
    {
        var catalogue = new InMemoryWafCatalogue(Signature(
            "cf-title-script",
            [],
            ["title:Just a moment|0.5", "script:challenge-platform|0.5"]));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var result = evaluator.Evaluate(Html(
            "<html><head><title>Just a moment...</title></head><body>" +
            "<script src=\"/cdn-cgi/challenge-platform/scripts.js\"></script></body></html>"));
        Assert.True(result.IsChallenge);

        using var evidence = JsonDocument.Parse(result.EvidenceJson);
        var facets = evidence.RootElement.GetProperty("matchedFacets").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Title", facets);
        Assert.Contains("Script", facets);
    }

    /// <summary>Dom-marker selector + inline script marker corroborate.</summary>
    [Fact]
    public async Task DomMarkerAndInlineScriptCorroborate()
    {
        var catalogue = new InMemoryWafCatalogue(Signature(
            "px-dom-script",
            [],
            ["dom:div#px-captcha|0.5", "script:perimeterx|0.5"]));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var result = evaluator.Evaluate(Html(
            "<html><body>Access denied<div id=\"px-captcha\"></div>" +
            "<script>window.px = 'perimeterx';</script></body></html>"));
        Assert.True(result.IsChallenge);

        using var evidence = JsonDocument.Parse(result.EvidenceJson);
        var facets = evidence.RootElement.GetProperty("matchedFacets").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("DomMarker", facets);
        Assert.Contains("Script", facets);
    }

    /// <summary>Explicit non-HTML content types are never eligible (§2.4.1).</summary>
    [Fact]
    public async Task NonHtmlContentTypeIsNeverEligible()
    {
        var catalogue = new InMemoryWafCatalogue(Signature(
            "cf-two-facet",
            ["cf-mitigated~challenge|0.5"],
            ["title:Just a moment|0.5"]));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var input = Html("<html><head><title>Just a moment...</title></head></html>", contentType: "application/json") with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "application/json",
                ["cf-mitigated"] = "challenge",
            },
        };
        var result = evaluator.Evaluate(input);
        Assert.False(result.IsChallenge);
        Assert.NotEmpty(result.MatchedSignatureKeys);

        using var evidence = JsonDocument.Parse(result.EvidenceJson);
        Assert.False(evidence.RootElement.GetProperty("eligible").GetBoolean());
        Assert.True(evidence.RootElement.GetProperty("statusEligible").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("contentTypeEligible").GetBoolean());
    }

    /// <summary>An absent content type stays eligible (fetch layer records it when sent).</summary>
    [Fact]
    public async Task AbsentContentTypeIsEligible()
    {
        var catalogue = new InMemoryWafCatalogue(Signature(
            "cf-two-facet",
            ["cf-mitigated~challenge|0.5"],
            ["title:Just a moment|0.5"]));
        var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
        var input = Html("<html><head><title>Just a moment...</title></head></html>", contentType: null) with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["cf-mitigated"] = "challenge",
            },
        };
        Assert.True(evaluator.Evaluate(input).IsChallenge);
    }

    /// <summary>The |high flag is rejected on non-header/cookie facets (grammar guard).</summary>
    [Fact]
    public void HighFlagIsRejectedOnOtherFacets()
    {
        Assert.Throws<FormatException>(() => WafMarker.ParseBodyMarker("title:x|high"));
        Assert.Throws<FormatException>(() => WafMarker.ParseBodyMarker("dom:div#x|high"));
        Assert.Throws<FormatException>(() => WafMarker.ParseBodyMarker("script:x|high"));
        Assert.Throws<FormatException>(() => WafMarker.ParseBodyMarker("x|high"));

        var header = WafMarker.ParseHeaderMarker("cf-mitigated~challenge|0.9|high");
        Assert.True(header.HighSpecificity);
        Assert.Equal(0.9, header.Weight);
        var cookie = WafMarker.ParseHeaderMarker("cookie:datadome|high");
        Assert.True(cookie.HighSpecificity);
        Assert.Equal(WafFacet.Cookie, cookie.Facet);
    }

    /// <summary>Facet prefixes parse into the five-facet set with per-facet default weights.</summary>
    [Fact]
    public void FacetPrefixesParse()
    {
        Assert.Equal(WafFacet.Title, WafMarker.ParseBodyMarker("title:Just a moment").Facet);
        Assert.Equal(WafFacet.DomMarker, WafMarker.ParseBodyMarker("dom:div#x").Facet);
        Assert.Equal(WafFacet.Script, WafMarker.ParseBodyMarker("script:challenge-platform").Facet);
        Assert.Equal(WafFacet.Body, WafMarker.ParseBodyMarker("plain-substring").Facet);
        Assert.Equal(WafMarker.DefaultTitleWeight, WafMarker.ParseBodyMarker("title:x").Weight);
        Assert.Equal(0.7, WafMarker.ParseBodyMarker("title:x|0.7").Weight);
        Assert.Throws<FormatException>(() => WafMarker.ParseBodyMarker("dom:div#[unterminated"));
    }

    /// <summary>Dom selectors match id, class and attribute constraints.</summary>
    [Fact]
    public void DomSelectorsMatchConstraints()
    {
        var document = StageBExtractor.Extract(
            "<html><body><div id=\"challenge\" class=\"cf-wrapper outer\" data-ray=\"abc\">" +
            "<script src=\"/cdn-cgi/challenge-platform/scripts.js\"></script></body></html>",
            nodeLimit: 100);

        Assert.True(WafMarker.ParseBodyMarker("dom:div#challenge").MatchesStageB(document));
        Assert.True(WafMarker.ParseBodyMarker("dom:div.cf-wrapper").MatchesStageB(document));
        Assert.True(WafMarker.ParseBodyMarker("dom:div.cf-wrapper.outer").MatchesStageB(document));
        Assert.True(WafMarker.ParseBodyMarker("dom:div[data-ray]").MatchesStageB(document));
        Assert.True(WafMarker.ParseBodyMarker("dom:div[data-ray=abc]").MatchesStageB(document));
        Assert.True(WafMarker.ParseBodyMarker("dom:script[src]").MatchesStageB(document));
        Assert.False(WafMarker.ParseBodyMarker("dom:div#other").MatchesStageB(document));
        Assert.False(WafMarker.ParseBodyMarker("dom:div[data-ray=xyz]").MatchesStageB(document));
        Assert.False(WafMarker.ParseBodyMarker("dom:span.cf-wrapper").MatchesStageB(document));
    }

    /// <summary>The Stage B extractor collects title, script sources and inline scripts.</summary>
    [Fact]
    public void StageBExtractorCollectsEvidence()
    {
        var document = StageBExtractor.Extract(
            "<html><head><title> Just a moment... </title></head><body>" +
            "<script src=\"https://captcha-delivery.com/interstitial/\"></script>" +
            "<script>window.guard = 'datadome';</script></body></html>",
            nodeLimit: 100);

        Assert.Equal("Just a moment...", document.Title);
        Assert.Equal(["https://captcha-delivery.com/interstitial/"], document.ScriptSources);
        Assert.Single(document.InlineScripts);
        Assert.Contains("datadome", document.InlineScripts[0]);
        Assert.Contains(document.Tags, t => t.Name == "div" || t.Name == "script");

        var empty = StageBExtractor.Extract("", 100);
        Assert.Null(empty.Title);
        Assert.Empty(empty.ScriptSources);
        Assert.Empty(empty.Tags);
    }

    /// <summary>The Stage B tag scan is bounded by the policy node limit.</summary>
    [Fact]
    public void StageBNodeLimitBoundsTheScan()
    {
        var body = "<html><body>" + string.Concat(Enumerable.Repeat("<div class=\"x\">", 50)) + "</body></html>";
        var document = StageBExtractor.Extract(body, nodeLimit: 10);
        Assert.True(document.Tags.Count <= 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => StageBExtractor.Extract(body, 0));
    }

    /// <summary>The bundled asset shape accepts additive title/dom/script marker arrays.</summary>
    [Fact]
    public async Task BundledAssetFacetArraysAreLoaded()
    {
        var db = new WafTestDatabase();
        await db.InitializeAsync();

        var assetPath = Path.Combine(Path.GetTempPath(), $"cf-waf-facet-asset-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(assetPath, """
        {
          "version": "test-1",
          "revision": "test-rev",
          "signatures": [
            {
              "key": "facet-test",
              "provider": "Cloudflare",
              "challengeClass": "Interstitial",
              "headerMarkers": [],
              "cookieMarkers": [],
              "bodyMarkers": [],
              "titleMarkers": ["Just a moment|0.5"],
              "domMarkers": ["div#challenge|0.5"],
              "scriptMarkers": []
            }
          ]
        }
        """);
        try
        {
            var catalogue = new WafSignatureCatalogue(db.Provider, assetPath);
            var evaluator = await WafChallengeEvaluator.CreateAsync(catalogue);
            var result = evaluator.Evaluate(Html(
                "<html><head><title>Just a moment...</title></head><body><div id=\"challenge\"></div></body></html>"));
            Assert.True(result.IsChallenge);
            Assert.Equal(["facet-test"], result.MatchedSignatureKeys);

            using var evidence = JsonDocument.Parse(result.EvidenceJson);
            var facets = evidence.RootElement.GetProperty("matchedFacets").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("Title", facets);
            Assert.Contains("DomMarker", facets);
        }
        finally
        {
            File.Delete(assetPath);
            await db.DisposeAsync();
        }
    }
}
