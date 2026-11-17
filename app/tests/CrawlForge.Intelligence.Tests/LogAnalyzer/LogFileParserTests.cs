using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Intelligence.LogAnalyzer;
using CrawlForge.Intelligence.Tests.LogAnalyzer.Support;
using Xunit;

namespace CrawlForge.Intelligence.Tests.LogAnalyzer;

/// <summary>
/// Streaming parser behavior (FS-CRAWL-002 §11.2): all four formats, gzip,
/// malformed-line discard counters with bounded samples, UTC normalization,
/// window filtering, auto-detection, and custom field mapping.
/// </summary>
public sealed class LogFileParserTests
{
    private static async Task<List<LogEntry>> ParseStringAsync(string content, LogFileFormat format, LogParserOptions? options = null)
    {
        var parser = new LogFileParser(options);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var entries = new List<LogEntry>();
        await foreach (var entry in parser.ParseAsync(stream, format))
        {
            entries.Add(entry);
        }

        return entries;
    }

    [Fact]
    public async Task CombinedFormatParsesAllFields()
    {
        var entries = await ParseStringAsync(LogSamples.CombinedSample(), LogFileFormat.ApacheNginxCombined);

        Assert.Equal(5, entries.Count);
        var first = entries[0];
        Assert.Equal("66.249.66.1", first.ClientIp);
        Assert.Equal("GET", first.Method);
        Assert.Equal("/page-a", first.Path);
        Assert.Equal(200, first.StatusCode);
        Assert.Equal(1024, first.ResponseBytes);
        Assert.Equal(LogSamples.GooglebotDesktopUa, first.UserAgent);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 13, 55, 36, TimeSpan.Zero), first.Timestamp);
    }

    [Fact]
    public async Task TimestampsNormalizeToUtc()
    {
        var line = LogSamples.CombinedLine("66.249.66.1", "10/Oct/2026:15:55:36 +0200", "GET", "/x", 200, "10", "bot");

        var entries = await ParseStringAsync(line, LogFileFormat.ApacheNginxCombined);

        Assert.Equal(new DateTimeOffset(2026, 10, 10, 13, 55, 36, TimeSpan.Zero), entries[0].Timestamp);
        Assert.Equal(TimeSpan.Zero, entries[0].Timestamp.Offset);
    }

    [Fact]
    public async Task CommonFormatWithoutRefererAndUserAgentIsTolerated()
    {
        const string common = "66.249.66.1 - - [10/Oct/2026:13:55:36 +0000] \"GET /page-a HTTP/1.1\" 200 1024";

        var entries = await ParseStringAsync(common, LogFileFormat.ApacheNginxCombined);

        Assert.Single(entries);
        Assert.Null(entries[0].UserAgent);
        Assert.Equal(1024, entries[0].ResponseBytes);
    }

    [Fact]
    public async Task MissingBytesBecomeNull()
    {
        var line = LogSamples.CombinedLine("66.249.66.1", "10/Oct/2026:13:55:36 +0000", "GET", "/x", 304, "-", "bot");

        var entries = await ParseStringAsync(line, LogFileFormat.ApacheNginxCombined);

        Assert.Null(entries[0].ResponseBytes);
    }

    [Fact]
    public async Task MalformedLinesAreCountedWithBoundedSamples()
    {
        var lines = Enumerable.Range(0, 30).Select(i => $"garbage line {i}");
        var content = string.Join('\n', lines);
        var parser = new LogFileParser(new LogParserOptions(MaxErrorSamples: 5));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

        var entries = new List<LogEntry>();
        await foreach (var entry in parser.ParseAsync(stream, LogFileFormat.ApacheNginxCombined))
        {
            entries.Add(entry);
        }

        Assert.Empty(entries);
        Assert.Equal(30, parser.Counters.LinesParsed);
        Assert.Equal(30, parser.Counters.LinesRejected);
        Assert.Equal(0, parser.Counters.LinesAccepted);
        Assert.Equal(5, parser.Counters.ErrorSamples.Count);
    }

    [Fact]
    public async Task WindowFilteringCountsOutOfWindowSeparately()
    {
        var options = new LogParserOptions(
            WindowStart: new DateTimeOffset(2026, 10, 10, 13, 56, 0, TimeSpan.Zero),
            WindowEnd: new DateTimeOffset(2026, 10, 10, 13, 58, 0, TimeSpan.Zero));
        var parser = new LogFileParser(options);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(LogSamples.CombinedSample()));

        var entries = new List<LogEntry>();
        await foreach (var entry in parser.ParseAsync(stream, LogFileFormat.ApacheNginxCombined))
        {
            entries.Add(entry);
        }

        // In window: 13:56 (page-b) and 13:57 (page-c). Malformed line still rejected.
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, parser.Counters.LinesAccepted);
        Assert.Equal(3, parser.Counters.LinesOutOfWindow);
        Assert.Equal(1, parser.Counters.LinesRejected);
    }

    [Fact]
    public async Task W3cHeaderDrivenMappingParsesFieldsAndQueries()
    {
        var entries = await ParseStringAsync(LogSamples.W3cSample(), LogFileFormat.IisW3c);

        Assert.Equal(3, entries.Count);
        Assert.Equal("/page-a", entries[0].Path);
        Assert.Equal("/page-b?session=abc", entries[1].Path);
        Assert.Equal("66.249.66.1", entries[0].ClientIp);
        Assert.Equal(200, entries[0].StatusCode);
        Assert.Equal(1024, entries[0].ResponseBytes);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 13, 55, 36, TimeSpan.Zero), entries[0].Timestamp);
    }

    [Fact]
    public async Task W3cQuotedUserAgentWithSpacesIsPreserved()
    {
        const string content = "#Fields: date time c-ip cs-method cs-uri-stem sc-status cs(User-Agent)\n" +
            "2026-10-10 13:55:36 66.249.66.1 GET /x 200 \"Mozilla/5.0 (compatible; Googlebot/2.1)\"";

        var entries = await ParseStringAsync(content, LogFileFormat.IisW3c);

        Assert.Equal("Mozilla/5.0 (compatible; Googlebot/2.1)", entries[0].UserAgent);
    }

    [Fact]
    public async Task CloudflareJsonExportParses()
    {
        var entries = await ParseStringAsync(LogSamples.CloudflareJsonSample(), LogFileFormat.CloudflareExport);

        Assert.Equal(2, entries.Count);
        Assert.Equal("66.249.66.1", entries[0].ClientIp);
        Assert.Equal("/page-a", entries[0].Path);
        Assert.Equal(200, entries[0].StatusCode);
        Assert.Equal(1024, entries[0].ResponseBytes);
        Assert.Equal(LogSamples.GooglebotDesktopUa, entries[0].UserAgent);
        Assert.Equal("20.15.240.5", entries[1].ClientIp);
    }

    [Fact]
    public async Task CloudflareCsvExportParses()
    {
        var entries = await ParseStringAsync(LogSamples.CloudflareCsvSample(), LogFileFormat.CloudflareExport);

        Assert.Equal(2, entries.Count);
        Assert.Equal("/page-a", entries[0].Path);
        Assert.Equal(LogSamples.GooglebotDesktopUa, entries[0].UserAgent);
        Assert.Equal("157.55.39.10", entries[1].ClientIp);
        Assert.Equal(2048, entries[1].ResponseBytes);
    }

    [Fact]
    public async Task GzipContentIsDetectedAndDecompressed()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        var path = database.WriteGzipLogFile("access.log.gz", LogSamples.CombinedSample());
        var parser = new LogFileParser();
        await using var stream = File.OpenRead(path);

        var entries = new List<LogEntry>();
        await foreach (var entry in parser.ParseAsync(stream, LogFileFormat.ApacheNginxCombined))
        {
            entries.Add(entry);
        }

        Assert.Equal(5, entries.Count);
        Assert.Equal(1, parser.Counters.LinesRejected);
    }

    [Theory]
    [InlineData(LogFileFormat.ApacheNginxCombined)]
    [InlineData(LogFileFormat.IisW3c)]
    [InlineData(LogFileFormat.CloudflareExport)]
    public async Task FormatAutoDetectionSniffsTheFirstLine(LogFileFormat expected)
    {
        var content = expected switch
        {
            LogFileFormat.IisW3c => LogSamples.W3cSample(),
            LogFileFormat.CloudflareExport => LogSamples.CloudflareJsonSample(),
            _ => LogSamples.CombinedSample()
        };
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

        var detected = await LogFormatDetector.DetectAsync(stream);

        Assert.Equal(expected, detected);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task CloudflareCsvIsAutoDetected()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(LogSamples.CloudflareCsvSample()));

        var detected = await LogFormatDetector.DetectAsync(stream);

        Assert.Equal(LogFileFormat.CloudflareExport, detected);
    }

    [Fact]
    public async Task CustomFormatUsesConfiguredFieldList()
    {
        const string content = "2026-10-10 13:55:36 66.249.66.1 GET /custom 200";
        var options = new LogParserOptions(CustomFields: ["date", "time", "c-ip", "cs-method", "cs-uri-stem", "sc-status"]);

        var entries = await ParseStringAsync(content, LogFileFormat.Custom, options);

        Assert.Single(entries);
        Assert.Equal("/custom", entries[0].Path);
        Assert.Equal("66.249.66.1", entries[0].ClientIp);
    }

    [Fact]
    public async Task EmptyStreamProducesNoEntriesAndZeroCounters()
    {
        var parser = new LogFileParser();
        using var stream = new MemoryStream([]);

        var entries = new List<LogEntry>();
        await foreach (var entry in parser.ParseAsync(stream, LogFileFormat.ApacheNginxCombined))
        {
            entries.Add(entry);
        }

        Assert.Empty(entries);
        Assert.Equal(0, parser.Counters.LinesParsed);
    }

    [Fact]
    public async Task ConfigureAppliesPerImportWindow()
    {
        var parser = new LogFileParser();
        parser.Configure(new LogParserOptions(
            WindowStart: new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero)));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(LogSamples.CombinedSample()));

        var entries = new List<LogEntry>();
        await foreach (var entry in parser.ParseAsync(stream, LogFileFormat.ApacheNginxCombined))
        {
            entries.Add(entry);
        }

        Assert.Single(entries);
        Assert.Equal("/orphan-page", entries[0].Path);
    }
}
