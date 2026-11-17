using CrawlForge.Audit.LinkGraph;
using CrawlForge.Audit.Tests.LinkGraph.Support;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.LinkGraph;

/// <summary>
/// Zone classification behavior per FS-CRAWL-002 §15.1: deterministic precedence,
/// breadcrumb markers, content-region scoping, evidence, and stack safety.
/// </summary>
public sealed class LinkZoneClassifierTests
{
    private readonly LinkZoneClassifier _classifier = new();

    [Fact]
    public void BodyAnchorWithoutLandmarksDefaultsToInContent()
    {
        var anchor = DomFactory.Anchor("/target");
        var document = DomFactory.Document(DomFactory.Element("p", children: [anchor]));

        var result = _classifier.Classify(document, anchor);

        Assert.Equal(DomZone.MainContent, result.Zone);
        Assert.Contains("default", result.Evidence);
    }

    [Theory]
    [InlineData("nav", null, DomZone.Navigation)]
    [InlineData("div", "navigation", DomZone.Navigation)]
    [InlineData("header", null, DomZone.Header)]
    [InlineData("div", "banner", DomZone.Header)]
    [InlineData("footer", null, DomZone.Footer)]
    [InlineData("div", "contentinfo", DomZone.Footer)]
    [InlineData("aside", null, DomZone.Sidebar)]
    [InlineData("div", "complementary", DomZone.Sidebar)]
    public void BoilerplateLandmarksClassifyByTagOrRole(string tag, string? role, DomZone expected)
    {
        var anchor = DomFactory.Anchor("/target");
        var attributes = role is null
            ? null
            : new Dictionary<string, string> { ["role"] = role };
        var document = DomFactory.Document(DomFactory.Element(tag, attributes, [anchor]));

        var result = _classifier.Classify(document, anchor);

        Assert.Equal(expected, result.Zone);
        Assert.Contains("landmark", result.Evidence);
    }

    [Fact]
    public void ZonePrecedenceAppliesAcrossTheAncestorChain()
    {
        // nav inside footer: navigation outranks footer regardless of depth.
        var anchor = DomFactory.Anchor("/target");
        var nav = DomFactory.Element("nav", children: [anchor]);
        var document = DomFactory.Document(DomFactory.Element("footer", children: [nav]));

        var result = _classifier.Classify(document, anchor);

        Assert.Equal(DomZone.Navigation, result.Zone);
    }

    [Theory]
    [InlineData("aria-label", "Breadcrumb")]
    [InlineData("aria-label", "breadcrumb trail")]
    [InlineData("class", "breadcrumb-list")]
    [InlineData("class", "nav Breadcrumbs uppercase")]
    [InlineData("id", "site-breadcrumb")]
    [InlineData("itemtype", "https://schema.org/BreadcrumbList")]
    public void BreadcrumbMarkersWinOverOtherLandmarks(string attribute, string value)
    {
        var anchor = DomFactory.Anchor("/target");
        var marker = DomFactory.Element("nav", new Dictionary<string, string> { [attribute] = value }, [anchor]);
        var document = DomFactory.Document(DomFactory.Element("header", children: [marker]));

        var result = _classifier.Classify(document, anchor);

        Assert.Equal(DomZone.Breadcrumb, result.Zone);
        Assert.Contains("breadcrumb marker", result.Evidence);
        Assert.Contains(attribute, result.Evidence);
    }

    [Fact]
    public void BreadcrumbMarkerOnTheAnchorItselfIsHonored()
    {
        var anchor = DomFactory.Element(
            "a",
            new Dictionary<string, string> { ["href"] = "/target", ["aria-label"] = "breadcrumb item" },
            text: "Home");
        var document = DomFactory.Document(anchor);

        var result = _classifier.Classify(document, anchor);

        Assert.Equal(DomZone.Breadcrumb, result.Zone);
        Assert.Contains("depth 0", result.Evidence);
    }

    [Fact]
    public void AnchorInsideContentRegionIsInContentEvenWithinNavigation()
    {
        var anchor = DomFactory.Anchor("/target");
        var nav = DomFactory.Element("nav", children: [anchor]);
        var document = DomFactory.Document(DomFactory.Element("main", children: [nav]));

        var result = _classifier.Classify(document, anchor);

        Assert.Equal(DomZone.MainContent, result.Zone);
        Assert.Contains("content region", result.Evidence);
    }

    [Fact]
    public void ArticleLandmarkIsAContentRegion()
    {
        var anchor = DomFactory.Anchor("/target");
        var document = DomFactory.Document(DomFactory.Element("article", children: [anchor]));

        var result = _classifier.Classify(document, anchor);

        Assert.Equal(DomZone.MainContent, result.Zone);
    }

    [Theory]
    [InlineData("#content")]
    [InlineData(".article-body")]
    [InlineData("[role=main]")]
    public void CustomContentRegionSelectorsAreHonored(string selector)
    {
        var attributes = selector.StartsWith('#')
            ? new Dictionary<string, string> { ["id"] = selector[1..] }
            : selector.StartsWith('.')
                ? new Dictionary<string, string> { ["class"] = selector[1..] }
                : new Dictionary<string, string> { ["role"] = "main" };
        var anchor = DomFactory.Anchor("/target");
        var region = DomFactory.Element("div", attributes, [anchor]);
        var nav = DomFactory.Element("nav", children: [region]);
        var document = DomFactory.Document(nav);
        var classifier = new LinkZoneClassifier(new LinkZoneClassifierOptions
        {
            ContentRegionSelectors = [selector]
        });

        var result = classifier.Classify(document, anchor);

        Assert.Equal(DomZone.MainContent, result.Zone);
    }

    [Fact]
    public void AnchorOutsideBodyClassifiesAsResidualOther()
    {
        var anchor = DomFactory.Anchor("/target");
        var document = new DomDocument(
            "https://example.com/page",
            DomFactory.Element("html", children:
            [
                DomFactory.Element("head", children: [anchor]),
                DomFactory.Element("body")
            ]),
            []);

        var result = _classifier.Classify(document, anchor);

        Assert.Equal(DomZone.Other, result.Zone);
        Assert.Contains("outside document body", result.Evidence);
    }

    [Fact]
    public void DetachedAnchorFallsBackToDefaultWithoutThrowing()
    {
        var document = DomFactory.Document(DomFactory.Element("p"));
        var detached = DomFactory.Anchor("/target");

        var result = _classifier.Classify(document, detached);

        Assert.Equal(DomZone.MainContent, result.Zone);
        Assert.Contains("detached", result.Evidence);
    }

    [Fact]
    public void DeeplyNestedDocumentIsClassifiedWithoutStackOverflow()
    {
        // 5000 nested divs: iterative index build and chain walk must be stack-safe.
        var anchor = DomFactory.Anchor("/target");
        DomNode current = anchor;
        for (var i = 0; i < 5000; i++)
        {
            current = DomFactory.Element("div", children: [current]);
        }

        var document = DomFactory.Document(DomFactory.Element("nav", children: [current]));

        var result = _classifier.Classify(document, anchor);

        Assert.Equal(DomZone.Navigation, result.Zone);
    }

    [Fact]
    public void RepeatedClassificationsShareTheCachedParentIndex()
    {
        var first = DomFactory.Anchor("/first");
        var second = DomFactory.Anchor("/second");
        var document = DomFactory.Document(
            DomFactory.Element("nav", children: [first]),
            DomFactory.Element("footer", children: [second]));

        var firstResult = _classifier.Classify(document, first);
        var secondResult = _classifier.Classify(document, second);

        Assert.Equal(DomZone.Navigation, firstResult.Zone);
        Assert.Equal(DomZone.Footer, secondResult.Zone);
    }

    [Fact]
    public void EqualButDistinctAnchorInstancesDoNotCollideInTheIndex()
    {
        // Records use value equality; the index must key on reference identity.
        var first = DomFactory.Anchor("/same");
        var second = DomFactory.Anchor("/same");
        var document = DomFactory.Document(
            DomFactory.Element("nav", children: [first]),
            DomFactory.Element("footer", children: [second]));

        var firstResult = _classifier.Classify(document, first);
        var secondResult = _classifier.Classify(document, second);

        Assert.Equal(DomZone.Navigation, firstResult.Zone);
        Assert.Equal(DomZone.Footer, secondResult.Zone);
    }

    [Fact]
    public void EvidenceIdentifiesTheWinningMarkerAndDepth()
    {
        var anchor = DomFactory.Anchor("/target");
        var document = DomFactory.Document(
            DomFactory.Element("div", children:
            [
                DomFactory.Element("aside", children: [anchor])
            ]));

        var result = _classifier.Classify(document, anchor);

        Assert.Equal(DomZone.Sidebar, result.Zone);
        Assert.Contains("aside", result.Evidence);
        Assert.Contains("depth 1", result.Evidence);
    }
}
