using CrawlForge.Audit.ContentQuality;
using Xunit;

namespace CrawlForge.Audit.Tests.ContentQuality;

/// <summary>
/// Baseline visible-text extraction evidence (FS-CRAWL-001 §7.3, FS-CRAWL-002
/// §4.2 precedence rule 4): non-visible subtrees excluded, block boundaries
/// preserved, head facts extracted.
/// </summary>
public class VisibleTextExtractorTests
{
    /// <summary>Script, style, noscript, and head content never leak into visible text.</summary>
    [Fact]
    public void Non_Visible_Subtrees_Are_Excluded()
    {
        var root = DomBuilder.Element("html",
        [
            DomBuilder.Element("head", [DomBuilder.Element("title", text: "Hidden Title")]),
            DomBuilder.Element("body",
            [
                DomBuilder.Element("script", text: "var secret = 1;"),
                DomBuilder.Element("style", text: ".x { color: red; }"),
                DomBuilder.Element("noscript", text: "Enable JS"),
                DomBuilder.Element("p", text: "Visible text"),
            ]),
        ]);

        var text = VisibleTextExtractor.ExtractVisibleText(DomBuilder.Document("https://example.com", root));

        Assert.Equal("Visible text", text);
    }

    /// <summary>Block elements introduce whitespace boundaries; inline flow does not split words.</summary>
    [Fact]
    public void Block_Boundaries_Separate_Text()
    {
        var root = DomBuilder.Element("body",
        [
            DomBuilder.Element("p", [DomBuilder.Element("span", text: "Hello ")], text: ""),
            DomBuilder.Element("p", text: "World"),
        ]);

        var text = VisibleTextExtractor.ExtractVisibleText(DomBuilder.Document("https://example.com", root));

        Assert.Equal("Hello World", text);
    }

    /// <summary>Whitespace collapses to single spaces with no leading/trailing space.</summary>
    [Fact]
    public void Whitespace_Collapses()
    {
        var root = DomBuilder.Element("body", [DomBuilder.Element("p", text: "  a \n\t b   c  ")]);

        var text = VisibleTextExtractor.ExtractVisibleText(DomBuilder.Document("https://example.com", root));

        Assert.Equal("a b c", text);
    }

    /// <summary>Title, meta description, and declared language are extracted from the head.</summary>
    [Fact]
    public void Head_Facts_Are_Extracted()
    {
        var document = DomBuilder.Page("My Title", "My description", "Body", lang: "en-GB");

        Assert.Equal("My Title", VisibleTextExtractor.ExtractTitle(document));
        Assert.Equal("My description", VisibleTextExtractor.ExtractMetaDescription(document));
        Assert.Equal("en-GB", VisibleTextExtractor.ExtractLanguage(document));
    }

    /// <summary>Absent head facts yield null.</summary>
    [Fact]
    public void Absent_Head_Facts_Yield_Null()
    {
        var document = DomBuilder.Page(title: null, description: null, "Body", lang: null);

        Assert.Null(VisibleTextExtractor.ExtractTitle(document));
        Assert.Null(VisibleTextExtractor.ExtractMetaDescription(document));
        Assert.Null(VisibleTextExtractor.ExtractLanguage(document));
    }

    /// <summary>Word counting splits on whitespace runs.</summary>
    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("one", 1)]
    [InlineData("one two  three\tfour\nfive", 5)]
    public void Word_Count_Splits_On_Whitespace(string text, int expected)
    {
        Assert.Equal(expected, VisibleTextExtractor.CountWords(text));
    }
}
