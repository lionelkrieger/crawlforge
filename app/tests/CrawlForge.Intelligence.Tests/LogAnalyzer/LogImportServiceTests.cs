using System.Text.Json;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Crawler.Canonicalization;
using CrawlForge.Intelligence.LogAnalyzer;
using CrawlForge.Intelligence.Tests.LogAnalyzer.Support;
using Xunit;

namespace CrawlForge.Intelligence.Tests.LogAnalyzer;

/// <summary>
/// Import-service integration on the real persistence stack (FS-CRAWL-002
/// §11.2/§11.4/§11.8): streaming aggregation, counters, window facets, hub
/// emission, listing, and transactional purge. Fully offline.
/// </summary>
public sealed class LogImportServiceTests
{
    private static (LogImportService Service, RecordingHub Hub, BotCatalogue Catalogue) CreateService(LogAnalyzerTestDatabase database)
    {
        var catalogue = BotCatalogue.Load(TestAssets.BotCataloguePath());
        var verifier = new BotVerifier(catalogue);
        var canonicalizer = new UrlCanonicalizer();
        var parser = new LogFileParser();
        var hub = new RecordingHub();
        var service = new LogImportService(database.Provider, parser, verifier, canonicalizer, hub, catalogue);
        return (service, hub, catalogue);
    }

    private static LogImportRequest Request(LogAnalyzerTestDatabase database, string path, DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new(1, path, "https://example.com", WindowStart: start, WindowEnd: end);

    [Fact]
    public async Task CombinedImportPersistsAggregatesCountersAndWindowFacets()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var (service, hub, _) = CreateService(database);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());

        var outcome = await service.ImportAsync(Request(database, path));

        Assert.Equal(LogImportStatus.Completed, outcome.Status);
        Assert.Equal(6, outcome.Counters.LinesParsed);
        Assert.Equal(5, outcome.Counters.LinesAccepted);
        Assert.Equal(1, outcome.Counters.LinesRejected);
        Assert.Equal(4, outcome.Counters.UniqueUrls);
        Assert.Equal(8704, outcome.Counters.BytesServed);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 13, 55, 36, TimeSpan.Zero), outcome.Counters.FirstSeen);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero), outcome.Counters.LastSeen);
        Assert.Equal("2026.1", outcome.Counters.CatalogueVersion);

        await using var connection = await database.Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT canonical_url, host, total_hits, search_bot_hits, bot_family_breakdown_json, status_breakdown_json " +
            "FROM log_url_aggregates WHERE log_import_id = @id ORDER BY canonical_url";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = outcome.ImportId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string Url, string Host, long Total, long Search, string Families, string Statuses)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5)));
        }

        Assert.Equal(4, rows.Count);
        var pageA = rows.Single(r => r.Url == "https://example.com/page-a");
        Assert.Equal("example.com", pageA.Host);
        Assert.Equal(2, pageA.Total);
        Assert.Equal(2, pageA.Search);
        Assert.Contains("Googlebot Desktop", pageA.Families);
        Assert.Contains("Bingbot", pageA.Families);
        Assert.Contains("2xx", pageA.Statuses);
        var pageC = rows.Single(r => r.Url == "https://example.com/page-c");
        Assert.Equal(0, pageC.Search);
        Assert.Contains("human", pageC.Families);
        Assert.Contains("4xx", pageC.Statuses);
        var orphan = rows.Single(r => r.Url == "https://example.com/orphan-page");
        Assert.Equal(1, orphan.Search);

        // Hub: completion event once, progress events throttled at 1s.
        var completed = hub.Events.Where(e => e.Name == LogHubEventNames.LogImportCompleted).ToList();
        Assert.Single(completed);
        Assert.Contains("\"status\":\"completed\"", completed[0].PayloadJson);
        foreach (var progress in hub.Events.Where(e => e.Name == LogHubEventNames.LogImportProgress))
        {
            Assert.Equal($"log-import-{outcome.ImportId}", progress.ThrottleKey);
            Assert.Equal(TimeSpan.FromSeconds(1), progress.ThrottleInterval);
        }

        // Bot IP samples exclude human traffic.
        Assert.Equal(3, outcome.Counters.BotIpSamples.Count);
        Assert.DoesNotContain(outcome.Counters.BotIpSamples, s => s.Ip == "203.0.113.5");
        Assert.All(outcome.Counters.BotIpSamples, s => Assert.Contains(s.Tier, new[] { "claimed", "cidr-verified" }));
    }

    [Fact]
    public async Task WindowSelectionFiltersAndPersistsTheRequestedWindow()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var (service, _, _) = CreateService(database);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());
        var start = new DateTimeOffset(2026, 10, 10, 13, 56, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 10, 10, 13, 58, 0, TimeSpan.Zero);

        var outcome = await service.ImportAsync(Request(database, path, start, end));

        Assert.Equal(2, outcome.Counters.LinesAccepted);
        Assert.Equal(3, outcome.Counters.LinesOutOfWindow);
        Assert.Equal(1, outcome.Counters.LinesRejected);
        var records = await service.ListImportsAsync(1);
        Assert.Equal(start, records[0].Import.WindowStart);
        Assert.Equal(end, records[0].Import.WindowEnd);
    }

    [Fact]
    public async Task GzipImportWorksThroughTheService()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var (service, _, _) = CreateService(database);
        var path = database.WriteGzipLogFile("access.log.gz", LogSamples.CombinedSample());

        var outcome = await service.ImportAsync(Request(database, path));

        Assert.Equal(LogImportStatus.Completed, outcome.Status);
        Assert.Equal(5, outcome.Counters.LinesAccepted);
    }

    [Fact]
    public async Task FormatAutoDetectionImportsW3cWithoutOverride()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var (service, _, _) = CreateService(database);
        var path = database.WriteLogFile("iis.log", LogSamples.W3cSample());
        var request = new LogImportRequest(1, path, "https://example.com");

        var outcome = await service.ImportAsync(request);

        Assert.Equal(LogImportStatus.Completed, outcome.Status);
        Assert.Equal(3, outcome.Counters.LinesAccepted);
        Assert.Equal(1, outcome.Counters.LinesRejected);
        var records = await service.ListImportsAsync(1);
        Assert.Equal(LogFileFormat.IisW3c, records[0].Import.Format);
    }

    [Fact]
    public async Task AbsoluteUrlPathsAreHonoredAndCanonicalized()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var (service, _, _) = CreateService(database);
        var line = LogSamples.CombinedLine("66.249.66.1", "10/Oct/2026:13:55:36 +0000", "GET", "HTTPS://OTHER.EXAMPLE/Page-A?b=2&a=1", 200, "10", LogSamples.GooglebotDesktopUa);
        var path = database.WriteLogFile("absolute.log", line);

        var outcome = await service.ImportAsync(Request(database, path));

        Assert.Equal(1, outcome.Counters.UniqueUrls);
        await using var connection = await database.Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT canonical_url, host FROM log_url_aggregates";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("https://other.example/Page-A?a=1&b=2", reader.GetString(0));
        Assert.Equal("other.example", reader.GetString(1));
    }

    [Fact]
    public async Task QueryVariantTotalsAreTrackedWithBoundedSets()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var (service, _, _) = CreateService(database);
        // Session-tracker variants canonicalize to ONE URL (trackers stripped),
        // while raw query-variant tracking still observes all 300 variants.
        var lines = Enumerable.Range(0, 300).Select(i =>
            LogSamples.CombinedLine("66.249.66.1", "10/Oct/2026:13:55:36 +0000", "GET", $"/trap?utm_source={i}", 200, "10", LogSamples.GooglebotDesktopUa));
        var path = database.WriteLogFile("trap.log", string.Join('\n', lines));

        var outcome = await service.ImportAsync(Request(database, path));

        Assert.Equal(1, outcome.Counters.UniqueUrls);
        Assert.Equal(300, outcome.Counters.QueryVariantTotal);
        Assert.Equal(300, outcome.Counters.LinesAccepted);
    }

    [Fact]
    public async Task MissingFileThrowsBeforeAnyImportRow()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var (service, _, _) = CreateService(database);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ImportAsync(Request(database, Path.Combine(database.DirectoryPath, "missing.log"))));

        Assert.Empty(await service.ListImportsAsync(1));
    }

    [Fact]
    public async Task ParserFailureMarksImportFailedAndEmitsCompletion()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var catalogue = BotCatalogue.Load(TestAssets.BotCataloguePath());
        var hub = new RecordingHub();
        var service = new LogImportService(
            database.Provider,
            new ThrowingParser(),
            new BotVerifier(catalogue),
            new UrlCanonicalizer(),
            hub,
            catalogue);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());

        var outcome = await service.ImportAsync(Request(database, path));

        Assert.Equal(LogImportStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.Error);
        var completed = hub.Events.Where(e => e.Name == LogHubEventNames.LogImportCompleted).ToList();
        Assert.Single(completed);
        Assert.Contains("\"status\":\"failed\"", completed[0].PayloadJson);
        var records = await service.ListImportsAsync(1);
        Assert.Equal(LogImportStatus.Failed, records[0].Import.Status);
    }

    [Fact]
    public async Task ListImportsReturnsNewestFirstWithCounters()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var (service, _, _) = CreateService(database);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());

        var first = await service.ImportAsync(Request(database, path));
        var second = await service.ImportAsync(Request(database, path));

        var records = await service.ListImportsAsync(1);
        Assert.Equal(2, records.Count);
        Assert.Equal(second.ImportId, records[0].Import.Id);
        Assert.Equal(first.ImportId, records[1].Import.Id);
        Assert.Equal(5, records[0].Counters.LinesAccepted);
        Assert.Equal("r1", records[0].Counters.CatalogueRevision);
    }

    [Fact]
    public async Task PurgeRemovesImportAndAggregatesTransactionally()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var (service, _, _) = CreateService(database);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());
        var outcome = await service.ImportAsync(Request(database, path));

        await service.PurgeImportAsync(outcome.ImportId);

        Assert.Empty(await service.ListImportsAsync(1));
        await using var connection = await database.Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM log_url_aggregates";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ProgressPayloadMatchesLockedContract()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var (service, hub, _) = CreateService(database);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());

        var outcome = await service.ImportAsync(Request(database, path));

        // Locked shape: { importId, counters: { linesParsed, linesAccepted, linesRejected }, phase }.
        var progressEvents = hub.Events.Where(e => e.Name == LogHubEventNames.LogImportProgress).ToList();
        Assert.NotEmpty(progressEvents);
        foreach (var progress in progressEvents)
        {
            using var document = JsonDocument.Parse(progress.PayloadJson);
            var root = document.RootElement;
            Assert.Equal(outcome.ImportId, root.GetProperty("importId").GetInt64());
            Assert.True(root.TryGetProperty("phase", out var phase));
            Assert.Equal(JsonValueKind.String, phase.ValueKind);
            // Counters are NESTED, never flat.
            Assert.False(root.TryGetProperty("linesParsed", out _));
            Assert.False(root.TryGetProperty("linesAccepted", out _));
            Assert.False(root.TryGetProperty("linesRejected", out _));
            var counters = root.GetProperty("counters");
            Assert.Equal(JsonValueKind.Object, counters.ValueKind);
            Assert.Equal(JsonValueKind.Number, counters.GetProperty("linesParsed").ValueKind);
            Assert.Equal(JsonValueKind.Number, counters.GetProperty("linesAccepted").ValueKind);
            Assert.Equal(JsonValueKind.Number, counters.GetProperty("linesRejected").ValueKind);
        }

        // The final (persisting-phase) progress carries the final counters.
        using var last = JsonDocument.Parse(progressEvents[^1].PayloadJson);
        Assert.Equal("persisting", last.RootElement.GetProperty("phase").GetString());
        Assert.Equal(6, last.RootElement.GetProperty("counters").GetProperty("linesParsed").GetInt64());
        Assert.Equal(5, last.RootElement.GetProperty("counters").GetProperty("linesAccepted").GetInt64());
        Assert.Equal(1, last.RootElement.GetProperty("counters").GetProperty("linesRejected").GetInt64());
    }

    [Fact]
    public async Task CompletedPayloadMatchesLockedContract_WithCompletedAudit()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1, auditStatus: 3); // Completed
        await database.InsertUrlAsync(1, "https://example.com/page-a", depth: 1);
        var (service, hub, _) = CreateService(database);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());

        var outcome = await service.ImportAsync(Request(database, path));

        var completed = hub.Events.Single(e => e.Name == LogHubEventNames.LogImportCompleted);
        using var document = JsonDocument.Parse(completed.PayloadJson);
        var root = document.RootElement;

        // Locked fields with exact names/shapes.
        Assert.Equal(outcome.ImportId, root.GetProperty("importId").GetInt64());
        var finalCounters = root.GetProperty("finalCounters");
        Assert.Equal(6, finalCounters.GetProperty("linesParsed").GetInt64());
        Assert.Equal(5, finalCounters.GetProperty("linesAccepted").GetInt64());
        Assert.Equal(1, finalCounters.GetProperty("linesRejected").GetInt64());

        // Correlation against the completed audit: page-b + orphan-page are
        // search-bot-hit URLs absent from the crawl; page-a is crawled and hit.
        var correlation = root.GetProperty("correlation");
        Assert.Equal(2, correlation.GetProperty("orphanCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("budgetWasteCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("uncrawledIndexableCount").GetInt64());

        // Self-consistency with the accepted correlation engine.
        var engine = new LogCorrelationEngine(database.Provider);
        var engineResult = await engine.CorrelateAsync(1, outcome.ImportId);
        Assert.Equal(engineResult.TrueOrphans.Count, correlation.GetProperty("orphanCount").GetInt32());
        Assert.Equal(engineResult.BudgetWaste.Count, correlation.GetProperty("budgetWasteCount").GetInt32());
        Assert.Equal(engineResult.UncrawledIndexable.Count, correlation.GetProperty("uncrawledIndexableCount").GetInt32());
    }

    [Fact]
    public async Task CompletedPayloadUsesLatestCompletedAudit()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1, auditStatus: 3);
        await database.InsertUrlAsync(1, "https://example.com/page-a", depth: 1);
        // A newer completed audit whose crawl covers every search-bot-hit URL.
        await database.SeedProjectAndAuditAsync(1, 2, auditStatus: 3);
        await database.InsertUrlAsync(2, "https://example.com/page-a", depth: 1);
        await database.InsertUrlAsync(2, "https://example.com/page-b", depth: 1);
        await database.InsertUrlAsync(2, "https://example.com/orphan-page", depth: 1);
        var (service, hub, _) = CreateService(database);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());

        var outcome = await service.ImportAsync(Request(database, path));

        var completed = hub.Events.Single(e => e.Name == LogHubEventNames.LogImportCompleted);
        using var document = JsonDocument.Parse(completed.PayloadJson);
        var correlation = document.RootElement.GetProperty("correlation");
        // Audit 2 (latest) covers all hit URLs: zero orphans, not audit 1's two.
        Assert.Equal(0, correlation.GetProperty("orphanCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("budgetWasteCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("uncrawledIndexableCount").GetInt64());
        Assert.Equal(LogImportStatus.Completed, outcome.Status);
    }

    [Fact]
    public async Task CompletedPayloadCorrelationZerosWhenProjectHasNoAudit()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAsync(1); // project only, no audit
        var (service, hub, _) = CreateService(database);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());

        var outcome = await service.ImportAsync(Request(database, path));

        Assert.Equal(LogImportStatus.Completed, outcome.Status);
        var completed = hub.Events.Single(e => e.Name == LogHubEventNames.LogImportCompleted);
        using var document = JsonDocument.Parse(completed.PayloadJson);
        var root = document.RootElement;
        Assert.Equal(6, root.GetProperty("finalCounters").GetProperty("linesParsed").GetInt64());
        var correlation = root.GetProperty("correlation");
        Assert.Equal(0, correlation.GetProperty("orphanCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("budgetWasteCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("uncrawledIndexableCount").GetInt64());
    }

    [Fact]
    public async Task CompletedPayloadCorrelationZerosWhenNoAuditIsCompleted()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1, auditStatus: 1); // Running, not Completed
        await database.InsertUrlAsync(1, "https://example.com/page-a", depth: 1);
        var (service, hub, _) = CreateService(database);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());

        await service.ImportAsync(Request(database, path));

        var completed = hub.Events.Single(e => e.Name == LogHubEventNames.LogImportCompleted);
        using var document = JsonDocument.Parse(completed.PayloadJson);
        var correlation = document.RootElement.GetProperty("correlation");
        Assert.Equal(0, correlation.GetProperty("orphanCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("budgetWasteCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("uncrawledIndexableCount").GetInt64());
    }

    [Fact]
    public async Task FailedImportEmitsLockedShapeWithZeroCorrelation()
    {
        await using var database = await LogAnalyzerTestDatabase.CreateAsync();
        // A completed audit with an indexable URL that WOULD correlate if the
        // failed import's partial data were used — zeros prove it is not.
        await database.SeedProjectAndAuditAsync(1, 1, auditStatus: 3);
        await database.InsertUrlAsync(1, "https://example.com/never-hit", depth: 1);
        var catalogue = BotCatalogue.Load(TestAssets.BotCataloguePath());
        var hub = new RecordingHub();
        var service = new LogImportService(
            database.Provider,
            new ThrowingParser(),
            new BotVerifier(catalogue),
            new UrlCanonicalizer(),
            hub,
            catalogue);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());

        var outcome = await service.ImportAsync(Request(database, path));

        Assert.Equal(LogImportStatus.Failed, outcome.Status);
        var completed = hub.Events.Single(e => e.Name == LogHubEventNames.LogImportCompleted);
        using var document = JsonDocument.Parse(completed.PayloadJson);
        var root = document.RootElement;
        Assert.Equal(outcome.ImportId, root.GetProperty("importId").GetInt64());
        var finalCounters = root.GetProperty("finalCounters");
        Assert.Equal(JsonValueKind.Number, finalCounters.GetProperty("linesParsed").ValueKind);
        Assert.Equal(JsonValueKind.Number, finalCounters.GetProperty("linesAccepted").ValueKind);
        Assert.Equal(JsonValueKind.Number, finalCounters.GetProperty("linesRejected").ValueKind);
        var correlation = root.GetProperty("correlation");
        Assert.Equal(0, correlation.GetProperty("orphanCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("budgetWasteCount").GetInt64());
        Assert.Equal(0, correlation.GetProperty("uncrawledIndexableCount").GetInt64());
    }

    /// <summary>
    /// Parser double that fails after the first real parsed entry. Counters
    /// advance through the real parsing path (their setters are internal to
    /// the production assembly and are not assigned here).
    /// </summary>
    private sealed class ThrowingParser : IStreamingLogFileParser
    {
        private readonly LogFileParser _inner = new();

        public LogImportCounters Counters => _inner.Counters;

        public void Configure(LogParserOptions options) => _inner.Configure(options);

        public async IAsyncEnumerable<LogEntry> ParseAsync(Stream stream, LogFileFormat format, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var entry in _inner.ParseAsync(stream, format, cancellationToken))
            {
                yield return entry;
                throw new IOException("simulated stream failure");
            }
        }
    }
}
