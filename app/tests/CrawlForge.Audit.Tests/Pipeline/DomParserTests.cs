using CrawlForge.Audit.Pipeline;
using Xunit;

namespace CrawlForge.Audit.Tests.Pipeline;

/// <summary>
/// Tolerant DOM parsing (FS-CRAWL-001 §7.2 step 2): malformed markup always
/// yields a document tree; parse problems surface as warnings.
/// </summary>
public class DomParserTests
{
    private readonly AngleSharpDomParser _parser = new();

    [Fact]
    public void WellFormedHtmlMapsOntoTheContractTree()
    {
        var document = _parser.Parse(
            "<!DOCTYPE html><html lang=\"en\"><head><title>T</title></head><body><p id=\"x\">Hello <b>world</b></p></body></html>",
            "https://example.test/");

        Assert.Equal("https://example.test/", document.Url);
        Assert.Equal("html", document.Root.TagName);
        Assert.Equal("en", document.Root.Attributes["lang"]);
        Assert.Empty(document.ParseWarnings);

        var body = document.Root.Children.Single(c => c.TagName == "body");
        var p = body.Children.Single(c => c.TagName == "p");
        Assert.Equal("x", p.Attributes["id"]);
        Assert.Equal("Hello ", p.TextContent);
        var b = p.Children.Single(c => c.TagName == "b");
        Assert.Equal("world", b.TextContent);
    }

    [Fact]
    public void MalformedMarkupStillProducesATreeWithWarnings()
    {
        var document = _parser.Parse(
            "<html><body><p>Unclosed <b>bold <i>italic</p><div>Next</div></body>",
            "https://example.test/broken");

        Assert.Equal("html", document.Root.TagName);
        Assert.NotEmpty(document.ParseWarnings);
        var body = document.Root.Children.Single(c => c.TagName == "body");
        Assert.Contains(body.Children, c => c.TagName == "p");
        Assert.Contains(body.Children, c => c.TagName == "div");
    }

    [Fact]
    public void EmptyInputProducesAnEmptyDocument()
    {
        var document = _parser.Parse(string.Empty, "https://example.test/empty");
        Assert.Equal("html", document.Root.TagName);
        Assert.NotNull(document.Root.Children);
    }

    [Fact]
    public void AttributesAreCaseInsensitiveAndTagsLowercase()
    {
        var document = _parser.Parse(
            "<HTML><BODY><DIV ID=\"Main\" CLASS=\"content\">Text</DIV></BODY></HTML>",
            "https://example.test/case");

        var body = document.Root.Children.Single(c => c.TagName == "body");
        var div = body.Children.Single(c => c.TagName == "div");
        Assert.Equal("Main", div.Attributes["id"]);
        Assert.Equal("content", div.Attributes["CLASS"]);
    }

    [Fact]
    public void ScriptAndStyleTextIsPreservedAsDirectText()
    {
        var document = _parser.Parse(
            "<html><head><style>.a{color:red}</style></head><body><script>var x = 1;</script></body></html>",
            "https://example.test/scripts");

        var head = document.Root.Children.Single(c => c.TagName == "head");
        var style = head.Children.Single(c => c.TagName == "style");
        Assert.Contains("color:red", style.TextContent, StringComparison.Ordinal);
        var body = document.Root.Children.Single(c => c.TagName == "body");
        var script = body.Children.Single(c => c.TagName == "script");
        Assert.Contains("var x = 1;", script.TextContent, StringComparison.Ordinal);
    }
}
