using CrawlForge.Audit.LinkGraph;
using CrawlForge.Audit.Tests.LinkGraph.Support;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.LinkGraph;

/// <summary>
/// Zone-aware link inventory: IElementInventoryExtractor consumption (test double),
/// document-order anchor extraction, zone evidence, and rel parsing.
/// </summary>
public sealed class LinkZoneInventoryServiceTests
{
    [Fact]
    public async Task ExtractsAnchorsInDocumentOrderWithZonesAndEvidence()
    {
        var navAnchor = DomFactory.Anchor("/nav-target", "Nav link");
        var contentAnchor = DomFactory.Anchor("/content-target", "Content link");
        var footerAnchor = DomFactory.Anchor("/footer-target", "Footer link");
        var document = DomFactory.Document(
            DomFactory.Element("nav", children: [navAnchor]),
            DomFactory.Element("article", children: [contentAnchor]),
            DomFactory.Element("footer", children: [footerAnchor]));
        var extractor = new TestElementInventoryExtractor
        {
            Items = [new ElementInventoryItem("links", "{\"count\":3}", 0)]
        };
        var service = new LinkZoneInventoryService(new LinkZoneClassifier(), extractor);

        var result = await service.ExtractZonedLinksAsync(document);

        Assert.Equal(3, result.Links.Count);
        Assert.Equal(["/nav-target", "/content-target", "/footer-target"], result.Links.Select(l => l.Href).ToArray());
        Assert.Equal([DomZone.Navigation, DomZone.MainContent, DomZone.Footer], result.Links.Select(l => l.Zone).ToArray());
        Assert.All(result.Links, link => Assert.False(string.IsNullOrWhiteSpace(link.ZoneEvidence)));
        Assert.Equal("Nav link", result.Links[0].AnchorText);
    }

    [Fact]
    public async Task ConsumesTheElementInventoryExtractorExactlyOnceAndPassesItemsThrough()
    {
        var document = DomFactory.Document(DomFactory.Anchor("/target"));
        var items = new[]
        {
            new ElementInventoryItem("links", "{\"href\":\"/target\"}", 0),
            new ElementInventoryItem("headings", "{\"level\":1}", 1)
        };
        var extractor = new TestElementInventoryExtractor { Items = items };
        var service = new LinkZoneInventoryService(new LinkZoneClassifier(), extractor);

        var result = await service.ExtractZonedLinksAsync(document);

        Assert.Equal(1, extractor.CallCount);
        Assert.Same(document, extractor.LastDocument);
        Assert.Equal(items, result.Inventory);
    }

    [Fact]
    public async Task ParsesRelFlagsOnExtractedLinks()
    {
        var document = DomFactory.Document(
            DomFactory.Anchor("/sponsored", "Ad", "nofollow sponsored"),
            DomFactory.Anchor("/plain", "Plain"));
        var service = new LinkZoneInventoryService(new LinkZoneClassifier(), new TestElementInventoryExtractor());

        var result = await service.ExtractZonedLinksAsync(document);

        Assert.Equal(LinkRelationFlags.Nofollow | LinkRelationFlags.Sponsored, result.Links[0].RelationFlags);
        Assert.Equal(LinkRelationFlags.None, result.Links[1].RelationFlags);
    }

    [Fact]
    public async Task AnchorsWithoutHrefAreIgnored()
    {
        var document = DomFactory.Document(
            DomFactory.Element("a", text: "named anchor without href"),
            DomFactory.Anchor("/real"));
        var service = new LinkZoneInventoryService(new LinkZoneClassifier(), new TestElementInventoryExtractor());

        var result = await service.ExtractZonedLinksAsync(document);

        Assert.Single(result.Links);
        Assert.Equal("/real", result.Links[0].Href);
    }

    [Theory]
    [InlineData(null, LinkRelationFlags.None)]
    [InlineData("", LinkRelationFlags.None)]
    [InlineData("nofollow", LinkRelationFlags.Nofollow)]
    [InlineData("NOFOLLOW NoOpener", LinkRelationFlags.Nofollow | LinkRelationFlags.Noopener)]
    [InlineData("noreferrer ugc sponsored", LinkRelationFlags.Noreferrer | LinkRelationFlags.Ugc | LinkRelationFlags.Sponsored)]
    [InlineData("alternate canonical", LinkRelationFlags.Alternate | LinkRelationFlags.Canonical)]
    [InlineData("unknown-token", LinkRelationFlags.None)]
    [InlineData("nofollow unknown", LinkRelationFlags.Nofollow)]
    public void ParseRelationFlagsHandlesTokensCaseAndUnknowns(string? rel, LinkRelationFlags expected)
    {
        Assert.Equal(expected, LinkZoneInventoryService.ParseRelationFlags(rel));
    }
}
