using CrawlForge.Audit.Indexability;
using Xunit;

namespace CrawlForge.Audit.Tests.Indexability;

/// <summary>
/// Header directive parsing (FS-CRAWL-002 §15.4): X-Robots-Tag agent scopes,
/// directive parameters, and Link header entries with quoted parameters and
/// comma-bearing URIs.
/// </summary>
public sealed class HeaderDirectiveParserTests
{
    [Fact]
    public void Parse_NullHeaders_EmptyResult()
    {
        ParsedHeaderDirectives parsed = HeaderDirectiveParser.Parse(null);
        Assert.Empty(parsed.Robots);
        Assert.Empty(parsed.Links);
    }

    [Fact]
    public void RobotsTag_GenericScope()
    {
        IReadOnlyList<RobotsHeaderDirective> scopes = HeaderDirectiveParser.ParseRobotsTag("noindex, nofollow");
        RobotsHeaderDirective scope = Assert.Single(scopes);
        Assert.Equal("*", scope.Agent);
        Assert.Equal(new[] { "noindex", "nofollow" }, scope.Directives);
        Assert.Equal("noindex, nofollow", scope.RawValue);
    }

    [Fact]
    public void RobotsTag_AgentScope()
    {
        RobotsHeaderDirective scope = Assert.Single(HeaderDirectiveParser.ParseRobotsTag("googlebot: noindex"));
        Assert.Equal("googlebot", scope.Agent);
        Assert.Equal(new[] { "noindex" }, scope.Directives);
    }

    [Fact]
    public void RobotsTag_DirectiveWithColonParameter_NotAnAgent()
    {
        RobotsHeaderDirective scope = Assert.Single(
            HeaderDirectiveParser.ParseRobotsTag("unavailable_after: 25-Aug-2025 15:00:00 EST"));
        Assert.Equal("*", scope.Agent);
        Assert.StartsWith("unavailable_after:", scope.Directives[0], StringComparison.Ordinal);
    }

    [Fact]
    public void RobotsTag_MaxSnippetParameter_KeptAttached()
    {
        RobotsHeaderDirective scope = Assert.Single(HeaderDirectiveParser.ParseRobotsTag("max-snippet: 50, noimageindex"));
        Assert.Equal("*", scope.Agent);
        Assert.Contains("max-snippet: 50", scope.Directives);
        Assert.Contains("noimageindex", scope.Directives);
    }

    [Fact]
    public void RobotsTag_Empty_NoScopes()
    {
        Assert.Empty(HeaderDirectiveParser.ParseRobotsTag("   "));
    }

    [Fact]
    public void LinkHeader_CanonicalEntry()
    {
        LinkHeaderEntry entry = Assert.Single(
            HeaderDirectiveParser.ParseLinkHeader("<https://example.com/page>; rel=\"canonical\""));
        Assert.Equal("canonical", entry.Relation);
        Assert.Equal("https://example.com/page", entry.Target);
        Assert.Null(entry.Hreflang);
    }

    [Fact]
    public void LinkHeader_AlternateWithHreflang()
    {
        LinkHeaderEntry entry = Assert.Single(
            HeaderDirectiveParser.ParseLinkHeader("<https://example.com/de>; rel=\"alternate\"; hreflang=\"de\""));
        Assert.Equal("alternate", entry.Relation);
        Assert.Equal("de", entry.Hreflang);
    }

    [Fact]
    public void LinkHeader_MultipleEntries_WithCommaInsideUri()
    {
        IReadOnlyList<LinkHeaderEntry> entries = HeaderDirectiveParser.ParseLinkHeader(
            "<https://example.com/a,b>; rel=\"canonical\", <https://example.com/en>; rel=\"alternate\"; hreflang=\"en\"");
        Assert.Equal(2, entries.Count);
        Assert.Equal("https://example.com/a,b", entries[0].Target);
        Assert.Equal("en", entries[1].Hreflang);
    }

    [Fact]
    public void LinkHeader_UnquotedRel_Accepted()
    {
        LinkHeaderEntry entry = Assert.Single(HeaderDirectiveParser.ParseLinkHeader("</page>; rel=canonical"));
        Assert.Equal("canonical", entry.Relation);
        Assert.Equal("/page", entry.Target);
    }

    [Fact]
    public void LinkHeader_MalformedEntries_Skipped()
    {
        IReadOnlyList<LinkHeaderEntry> entries = HeaderDirectiveParser.ParseLinkHeader("garbage, <>; rel=\"canonical\", </ok>; rel=\"canonical\"");
        LinkHeaderEntry entry = Assert.Single(entries);
        Assert.Equal("/ok", entry.Target);
    }

    [Fact]
    public void Parse_CaseInsensitiveHeaderNames()
    {
        ParsedHeaderDirectives parsed = HeaderDirectiveParser.Parse(new Dictionary<string, string>
        {
            ["x-robots-tag"] = "noindex",
            ["link"] = "</page>; rel=\"canonical\"",
        });
        Assert.Single(parsed.Robots);
        Assert.Single(parsed.Links);
    }

    [Fact]
    public void Parse_IgnoresUnrelatedHeaders()
    {
        ParsedHeaderDirectives parsed = HeaderDirectiveParser.Parse(new Dictionary<string, string>
        {
            ["Content-Type"] = "text/html",
            ["Server"] = "test",
        });
        Assert.Empty(parsed.Robots);
        Assert.Empty(parsed.Links);
    }
}