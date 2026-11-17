using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// Two-column migration-matrix parsing fixtures (FS-CRAWL-002 §10.3):
/// Source_URL / Expected_Target_URL from .csv, .txt, or clipboard with
/// automatic header detection, positional fallback, and bounded diagnostics.
/// </summary>
public class MatrixInputParserTests
{
    /// <summary>CSV with the canonical header parses both columns in order.</summary>
    [Fact]
    public void Csv_With_Header_Parses_Rows()
    {
        var text = "Source_URL,Expected_Target_URL\nhttps://old.example.com/a,https://new.example.com/a\nhttps://old.example.com/b,https://new.example.com/b\n";

        var result = MatrixInputParser.Parse(text);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Rows[0].Ordinal);
        Assert.Equal(1, result.Rows[1].Ordinal);
        Assert.Equal("https://old.example.com/a", result.Rows[0].SourceUrl);
        Assert.Equal("https://new.example.com/a", result.Rows[0].ExpectedTargetUrl);
        Assert.Equal(0, result.InvalidCount);
    }

    /// <summary>Header detection is case-insensitive.</summary>
    [Fact]
    public void Csv_Header_Detection_Is_Case_Insensitive()
    {
        var text = "source_url,expected_target_url\nhttps://old.example.com/a,https://new.example.com/a\n";

        var result = MatrixInputParser.Parse(text);

        Assert.Single(result.Rows);
    }

    /// <summary>Headerless CSV uses positional columns (source, target).</summary>
    [Fact]
    public void Csv_Without_Header_Uses_Positional_Columns()
    {
        var text = "https://old.example.com/a,https://new.example.com/a\n";

        var result = MatrixInputParser.Parse(text);

        Assert.Single(result.Rows);
        Assert.Equal("https://old.example.com/a", result.Rows[0].SourceUrl);
        Assert.Equal("https://new.example.com/a", result.Rows[0].ExpectedTargetUrl);
    }

    /// <summary>Reordered headers are resolved by name, not position.</summary>
    [Fact]
    public void Csv_Reordered_Headers_Resolved_By_Name()
    {
        var text = "Expected_Target_URL,Source_URL\nhttps://new.example.com/a,https://old.example.com/a\n";

        var result = MatrixInputParser.Parse(text);

        Assert.Single(result.Rows);
        Assert.Equal("https://old.example.com/a", result.Rows[0].SourceUrl);
        Assert.Equal("https://new.example.com/a", result.Rows[0].ExpectedTargetUrl);
    }

    /// <summary>Whitespace-separated two-column text is supported (.txt matrix).</summary>
    [Fact]
    public void Txt_Whitespace_Separated_Columns()
    {
        var text = "https://old.example.com/a https://new.example.com/a\nhttps://old.example.com/b https://new.example.com/b\n";

        var result = MatrixInputParser.Parse(text);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("https://new.example.com/b", result.Rows[1].ExpectedTargetUrl);
    }

    /// <summary>Tab-delimited matrices are supported (clipboard spreadsheets).</summary>
    [Fact]
    public void Tab_Delimited_Matrix()
    {
        var text = "Source_URL\tExpected_Target_URL\nhttps://old.example.com/a\thttps://new.example.com/a\n";

        var result = MatrixInputParser.Parse(text);

        Assert.Single(result.Rows);
    }

    /// <summary>Rows with an invalid source or target are rejected with diagnostics.</summary>
    [Theory]
    [InlineData("not-a-url,https://new.example.com/a", "source")]
    [InlineData("https://old.example.com/a,ftp://new.example.com/a", "target")]
    public void Invalid_Rows_Are_Rejected(string row, string expectedSide)
    {
        var result = MatrixInputParser.Parse(row + "\n");

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.InvalidCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.StartsWith(expectedSide, diagnostic.Reason, StringComparison.Ordinal);
    }

    /// <summary>Ordinals stay contiguous across rejected rows.</summary>
    [Fact]
    public void Ordinals_Contiguous_Across_Rejections()
    {
        var text = "https://old.example.com/a,https://new.example.com/a\nbad,row\nhttps://old.example.com/b,https://new.example.com/b\n";

        var result = MatrixInputParser.Parse(text);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Rows[0].Ordinal);
        Assert.Equal(1, result.Rows[1].Ordinal);
        Assert.Equal(1, result.InvalidCount);
    }

    /// <summary>Column overrides by index work without a header.</summary>
    [Fact]
    public void Column_Override_By_Index()
    {
        var text = "https://new.example.com/a,https://old.example.com/a\n";

        var result = MatrixInputParser.Parse(text, sourceColumn: "1", targetColumn: "0");

        Assert.Single(result.Rows);
        Assert.Equal("https://old.example.com/a", result.Rows[0].SourceUrl);
        Assert.Equal("https://new.example.com/a", result.Rows[0].ExpectedTargetUrl);
    }

    /// <summary>Empty input yields an empty matrix.</summary>
    [Fact]
    public void Empty_Input_Yields_Empty_Matrix()
    {
        var result = MatrixInputParser.Parse(string.Empty);

        Assert.Empty(result.Rows);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.InvalidCount);
    }
}
