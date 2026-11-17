using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Canonicalization;
using CrawlForge.Intelligence.LogAnalyzer;
using CrawlForge.Intelligence.Tests.LogAnalyzer.Support;
using Xunit;

namespace CrawlForge.Intelligence.Tests.LogAnalyzer;

/// <summary>
/// Multi-GB scale proof (FS-CRAWL-002 §11.2, criterion #44): a ~1GB synthetic
/// combined log (5,000,000 lines, 50,000 distinct URLs) imports through the
/// real persistence stack with exact counters and bounded managed memory —
/// sampled DURING streaming via hub progress publications, not only after.
/// Fully offline; the fixture generates and imports once.
/// </summary>
public sealed class LogAnalyzerScaleTests : IAsyncLifetime
{
    private const int LineCount = 5_000_000;
    private const int UniqueUrlCount = 50_000;
    private const long MemoryBoundBytes = 512L * 1024 * 1024;

    private LogAnalyzerTestDatabase _database = null!;
    private RecordingHub _hub = null!;
    private LogImportOutcome _outcome = null!;
    private long _baselineMemory;
    private long _logFileBytes;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _database = await LogAnalyzerTestDatabase.CreateAsync();
        await _database.SeedProjectAndAuditAsync(1, 1);
        var path = Path.Combine(_database.DirectoryPath, "scale-access.log");
        GenerateLog(path);
        _logFileBytes = new FileInfo(path).Length;

        var catalogue = BotCatalogue.Load(TestAssets.BotCataloguePath());
        _hub = new RecordingHub();
        var service = new LogImportService(
            _database.Provider,
            new LogFileParser(),
            new BotVerifier(catalogue),
            new UrlCanonicalizer(),
            _hub,
            catalogue);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _baselineMemory = GC.GetTotalMemory(forceFullCollection: true);

        _outcome = await service.ImportAsync(new LogImportRequest(1, path, "https://example.com"));
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _database.DisposeAsync();

    private static void GenerateLog(string path)
    {
        var agents = new[]
        {
            (Ua: LogSamples.GooglebotDesktopUa, IpPrefix: "66.249.66."),
            (Ua: LogSamples.GooglebotSmartphoneUa, IpPrefix: "66.249.67."),
            (Ua: LogSamples.BingbotUa, IpPrefix: "157.55.39."),
            (Ua: LogSamples.GptBotUa, IpPrefix: "20.15.240."),
            (Ua: LogSamples.HumanUa, IpPrefix: "203.0.113.")
        };

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new StreamWriter(file, System.Text.Encoding.UTF8, 1 << 20);
        for (var i = 0; i < LineCount; i++)
        {
            var agent = agents[i % agents.Length];
            var ip = agent.IpPrefix + (i % 250 + 1);
            var status = i % 7 == 0 ? 404 : 200;
            var bytes = 100 + i % 900;
            writer.Write(ip);
            writer.Write(" - - [10/Oct/2026:13:55:36 +0000] \"GET /page-");
            writer.Write((i % UniqueUrlCount).ToString("D5"));
            writer.Write(" HTTP/1.1\" ");
            writer.Write(status);
            writer.Write(' ');
            writer.Write(bytes);
            writer.Write(" \"-\" \"");
            writer.Write(agent.Ua);
            writer.WriteLine('"');
        }
    }

    [Fact]
    public void ScaleFixtureReallyIsMultiHundredMegabytes()
    {
        Assert.True(_logFileBytes > 500_000_000, $"Expected a ~1GB fixture, got {_logFileBytes} bytes.");
    }

    [Fact]
    public void ImportCompletesWithExactCounters()
    {
        Assert.Equal(LogImportStatus.Completed, _outcome.Status);
        Assert.Equal(LineCount, _outcome.Counters.LinesParsed);
        Assert.Equal(LineCount, _outcome.Counters.LinesAccepted);
        Assert.Equal(0, _outcome.Counters.LinesRejected);
        Assert.Equal(UniqueUrlCount, _outcome.Counters.UniqueUrls);
        // The bot-IP sample is bounded (1,000 distinct bot IPs in the fixture).
        Assert.Equal(LogImportService.MaxBotIpSamples, _outcome.Counters.BotIpSamples.Count);
    }

    [Fact]
    public void ManagedMemoryStayedBoundedDuringStreaming()
    {
        // Progress publications sample memory while the stream is live.
        Assert.NotEmpty(_hub.ManagedMemorySamples);
        var peak = _hub.ManagedMemorySamples.Max();
        Assert.True(
            peak - _baselineMemory < MemoryBoundBytes,
            $"Managed memory grew by {peak - _baselineMemory:N0} bytes during import (bound {MemoryBoundBytes:N0}).");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retained = GC.GetTotalMemory(forceFullCollection: true) - _baselineMemory;
        Assert.True(retained < MemoryBoundBytes, $"Retained memory {retained:N0} exceeds the bound.");
    }

    [Fact]
    public async Task AggregatesAreCompleteAndCorrectAtScale()
    {
        await using var connection = await _database.Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), SUM(total_hits), SUM(search_bot_hits) FROM log_url_aggregates WHERE log_import_id = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = _outcome.ImportId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(UniqueUrlCount, reader.GetInt64(0));
        Assert.Equal(LineCount, reader.GetInt64(1));
        Assert.Equal(LineCount / 5 * 3, reader.GetInt64(2));

        // Every URL received exactly LineCount / UniqueUrlCount hits.
        await using var spotCommand = connection.CreateCommand();
        spotCommand.CommandText =
            "SELECT COUNT(*) FROM log_url_aggregates WHERE log_import_id = @id AND total_hits <> @expected";
        var importParameter = spotCommand.CreateParameter();
        importParameter.ParameterName = "@id";
        importParameter.Value = _outcome.ImportId;
        spotCommand.Parameters.Add(importParameter);
        var expectedParameter = spotCommand.CreateParameter();
        expectedParameter.ParameterName = "@expected";
        expectedParameter.Value = LineCount / UniqueUrlCount;
        spotCommand.Parameters.Add(expectedParameter);
        Assert.Equal(0L, (long)(await spotCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public void CompletionEventEmittedOnceWithLockedShape()
    {
        var completed = _hub.Events.Where(e => e.Name == LogHubEventNames.LogImportCompleted).ToList();
        Assert.Single(completed);
        Assert.Contains("\"status\":\"completed\"", completed[0].PayloadJson);
        Assert.Contains("\"uniqueUrls\":50000", completed[0].PayloadJson);

        // Locked payload shape at scale: nested finalCounters + correlation.
        using var document = System.Text.Json.JsonDocument.Parse(completed[0].PayloadJson);
        var root = document.RootElement;
        Assert.Equal(_outcome.ImportId, root.GetProperty("importId").GetInt64());
        var finalCounters = root.GetProperty("finalCounters");
        Assert.Equal(LineCount, finalCounters.GetProperty("linesParsed").GetInt64());
        Assert.Equal(LineCount, finalCounters.GetProperty("linesAccepted").GetInt64());
        Assert.Equal(0, finalCounters.GetProperty("linesRejected").GetInt64());
        // The fixture's audit is Running (not Completed): correlation zeros.
        var correlation = root.GetProperty("correlation");
        Assert.Equal(0, correlation.GetProperty("orphanCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("budgetWasteCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("uncrawledIndexableCount").GetInt64());

        // Progress publications during streaming also carry the nested shape.
        var progress = _hub.Events.Where(e => e.Name == LogHubEventNames.LogImportProgress).ToList();
        Assert.NotEmpty(progress);
        foreach (var progressEvent in progress)
        {
            using var progressDocument = System.Text.Json.JsonDocument.Parse(progressEvent.PayloadJson);
            var progressRoot = progressDocument.RootElement;
            Assert.False(progressRoot.TryGetProperty("linesParsed", out _));
            Assert.Equal(System.Text.Json.JsonValueKind.Object, progressRoot.GetProperty("counters").ValueKind);
            Assert.True(progressRoot.TryGetProperty("phase", out _));
        }
    }
}
