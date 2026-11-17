using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// Criterion #43 ingestion fixtures: .txt (one URL per line), .csv
/// (configurable URL column, automatic header detection), and clipboard paste,
/// with bounded per-line diagnostics that never fail the audit (§10.2).
/// </summary>
public class ListInputParserTests
{
    /// <summary>Txt ingestion accepts one URL per line and skips blank lines.</summary>
    [Fact]
    public void Txt_Accepts_One_Url_Per_Line()
    {
        var text = "https://example.com/a\n\nhttps://example.com/b\nhttp://example.com/c\n";

        var result = ListInputParser.ParseText(text);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(0, result.InvalidCount);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Entries[0].LineNumber);
        Assert.Equal("https://example.com/a", result.Entries[0].Url);
        Assert.Equal(3, result.Entries[1].LineNumber);
        Assert.Equal(4, result.Entries[2].LineNumber);
    }

    /// <summary>Malformed entries are rejected into per-line diagnostics, never thrown.</summary>
    [Theory]
    [InlineData("not a url", "not an absolute URL")]
    [InlineData("/relative/path", "not an absolute URL")]
    [InlineData("ftp://example.com/file", "scheme 'ftp' is not http(s)")]
    [InlineData("javascript:alert(1)", "scheme 'javascript' is not http(s)")]
    public void Txt_Rejects_Invalid_Entries_With_Diagnostics(string raw, string expectedReason)
    {
        var result = ListInputParser.ParseText($"https://example.com/ok\n{raw}\n");

        Assert.Single(result.Entries);
        Assert.Equal(1, result.InvalidCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(2, diagnostic.LineNumber);
        Assert.Equal(raw, diagnostic.RawText);
        Assert.Equal(expectedReason, diagnostic.Reason);
    }

    /// <summary>Diagnostics are bounded; the invalid count keeps the full total.</summary>
    [Fact]
    public void Txt_Diagnostics_Are_Bounded()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 150).Select(i => $"bad-line-{i}"));

        var result = ListInputParser.ParseText(lines);

        Assert.Empty(result.Entries);
        Assert.Equal(150, result.InvalidCount);
        Assert.Equal(ListInputParser.MaxDiagnostics, result.Diagnostics.Count);
    }

    /// <summary>CSV header detection skips the header row and selects the URL column.</summary>
    [Fact]
    public void Csv_Detects_Header_And_Url_Column()
    {
        var text = "id,url,note\n1,https://example.com/a,first\n2,https://example.com/b,second\n";

        var result = ListInputParser.ParseCsv(text);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("https://example.com/a", result.Entries[0].Url);
        Assert.Equal(2, result.Entries[0].LineNumber);
        Assert.Equal(0, result.InvalidCount);
    }

    /// <summary>The URL column is configurable by header name (case-insensitive).</summary>
    [Fact]
    public void Csv_Column_Selectable_By_Name()
    {
        var text = "note,URL\nfirst,https://example.com/a\nsecond,https://example.com/b\n";

        var result = ListInputParser.ParseCsv(text, urlColumn: "url");

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("https://example.com/b", result.Entries[1].Url);
    }

    /// <summary>The URL column is configurable by zero-based index without a header.</summary>
    [Fact]
    public void Csv_Column_Selectable_By_Index()
    {
        var text = "first,https://example.com/a\nsecond,https://example.com/b\n";

        var result = ListInputParser.ParseCsv(text, urlColumn: "1");

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("https://example.com/a", result.Entries[0].Url);
    }

    /// <summary>Without a header, the column holding URLs is auto-detected.</summary>
    [Fact]
    public void Csv_Auto_Detects_Url_Column()
    {
        var text = "alpha,https://example.com/a\nbeta,https://example.com/b\n";

        var result = ListInputParser.ParseCsv(text);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("https://example.com/a", result.Entries[0].Url);
    }

    /// <summary>An unknown column name is a configuration error, not a silent pass.</summary>
    [Fact]
    public void Csv_Unknown_Column_Name_Throws()
    {
        Assert.Throws<ArgumentException>(() => ListInputParser.ParseCsv("url\nhttps://example.com/a\n", urlColumn: "missing"));
    }

    /// <summary>Tab-delimited CSV is supported.</summary>
    [Fact]
    public void Csv_Tab_Delimiter_Supported()
    {
        var text = "url\tnote\nhttps://example.com/a\tfirst\n";

        var result = ListInputParser.ParseCsv(text);

        Assert.Single(result.Entries);
        Assert.Equal("https://example.com/a", result.Entries[0].Url);
    }

    /// <summary>Clipboard paste of plain lines behaves like txt ingestion.</summary>
    [Fact]
    public void Clipboard_Plain_Lines_Behave_Like_Txt()
    {
        var result = ListInputParser.ParseClipboard("https://example.com/a\nhttps://example.com/b");

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(0, result.InvalidCount);
    }

    /// <summary>Clipboard paste of multi-cell lines behaves like CSV ingestion.</summary>
    [Fact]
    public void Clipboard_Csv_Lines_Behave_Like_Csv()
    {
        var result = ListInputParser.ParseClipboard("url,note\nhttps://example.com/a,first\nhttps://example.com/b,second");

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("https://example.com/b", result.Entries[1].Url);
    }

    /// <summary>Empty input yields an empty result, never an error.</summary>
    [Fact]
    public void Empty_Input_Yields_Empty_Result()
    {
        Assert.Empty(ListInputParser.ParseText(string.Empty).Entries);
        Assert.Empty(ListInputParser.ParseCsv(string.Empty).Entries);
        Assert.Empty(ListInputParser.ParseClipboard(string.Empty).Entries);
    }
}
