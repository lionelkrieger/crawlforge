using CrawlForge.Intelligence.NGrams;
using Xunit;

namespace CrawlForge.Intelligence.Tests.NGrams;

/// <summary>Zone extraction tests (FS-CRAWL-002 §5.3–§5.4): title never scoped,
/// headings/H1 scoping containment, visible text, canonical and language.</summary>
public sealed class ZoneTextExtractorTests
{
    private const string Url = "https://example.com/page";

    /// <summary>The title zone extracts the full document title.</summary>
    [Fact]
    public void ExtractTitle_Returns_Full_Title()
    {
        var document = DomBuilder.Page(Url, "  Best   Running Shoes  ", "Running Shoes", "Buy shoes today.");

        Assert.Equal("Best Running Shoes", ZoneTextExtractor.ExtractTitle(document));
    }

    /// <summary>Headings extract H1–H6 in document order; H1 extraction is H1-only.</summary>
    [Fact]
    public void ExtractHeadings_Covers_H1_To_H6_And_H1_Only()
    {
        var document = DomBuilder.Page(
            Url, "Title", "Main Heading", "Body text.",
            extraHeadings: [DomBuilder.Element("h2", "Sub Heading"), DomBuilder.Element("h3", "Deep Heading")]);

        var headings = ZoneTextExtractor.ExtractHeadings(document, null);
        Assert.Equal("Main Heading\nSub Heading\nDeep Heading", headings);
        Assert.Equal("Main Heading", ZoneTextExtractor.ExtractH1(document, null));
    }

    /// <summary>Scoped headings use containment in the resolved scoped text.</summary>
    [Fact]
    public void ExtractHeadings_Applies_Scoped_Containment()
    {
        var document = DomBuilder.Page(
            Url, "Title", "Included Heading", "Body text.",
            extraHeadings: [DomBuilder.Element("h2", "Excluded Heading")]);

        var headings = ZoneTextExtractor.ExtractHeadings(document, "Included Heading and body content");
        Assert.Equal("Included Heading", headings);
        Assert.Equal("Included Heading", ZoneTextExtractor.ExtractH1(document, "Included Heading and body content"));
        Assert.Equal(string.Empty, ZoneTextExtractor.ExtractH1(document, "nothing matches here"));
    }

    /// <summary>Visible text skips script/style and collapses whitespace.</summary>
    [Fact]
    public void ExtractVisibleText_Skips_NonContent_Tags()
    {
        var document = DomBuilder.Document(Url,
        [
            DomBuilder.Element("head", children: [DomBuilder.Element("title", "T"), DomBuilder.Element("style", "css{}")]),
            DomBuilder.Element("body", children:
            [
                DomBuilder.Element("script", "var x = 1;"),
                DomBuilder.Element("p", "Hello   world"),
                DomBuilder.Element("noscript", "fallback"),
            ]),
        ]);

        Assert.Equal("Hello world", ZoneTextExtractor.ExtractVisibleText(document));
    }

    /// <summary>A single canonical declaration resolves to an absolute target.</summary>
    [Fact]
    public void ExtractCanonicalTarget_Resolves_Single_Declaration()
    {
        var document = DomBuilder.Page(Url, "T", null, "Body", canonicalHref: "/canonical-page");

        Assert.Equal("https://example.com/canonical-page", ZoneTextExtractor.ExtractCanonicalTarget(document, Url));
    }

    /// <summary>Multiple or absent canonical declarations yield null.</summary>
    [Fact]
    public void ExtractCanonicalTarget_Null_When_Multiple_Or_Absent()
    {
        var none = DomBuilder.Page(Url, "T", null, "Body");
        Assert.Null(ZoneTextExtractor.ExtractCanonicalTarget(none, Url));

        var multiple = DomBuilder.Document(Url,
        [
            DomBuilder.Element("head", children:
            [
                DomBuilder.Element("link", attributes: new Dictionary<string, string> { ["rel"] = "canonical", ["href"] = "/a" }),
                DomBuilder.Element("link", attributes: new Dictionary<string, string> { ["rel"] = "canonical", ["href"] = "/b" }),
            ]),
        ]);
        Assert.Null(ZoneTextExtractor.ExtractCanonicalTarget(multiple, Url));
    }

    /// <summary>The declared language comes from the html lang attribute.</summary>
    [Fact]
    public void ExtractLanguage_Reads_Html_Lang()
    {
        var document = DomBuilder.Page(Url, "T", null, "Body", lang: "en-GB");

        Assert.Equal("en-GB", ZoneTextExtractor.ExtractLanguage(document));
        Assert.Null(ZoneTextExtractor.ExtractLanguage(DomBuilder.Page(Url, "T", null, "Body", lang: null)));
    }
}
