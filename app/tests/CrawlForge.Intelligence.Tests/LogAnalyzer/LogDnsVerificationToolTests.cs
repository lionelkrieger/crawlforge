using System.Text.Json;
using CrawlForge.Crawler.Canonicalization;
using CrawlForge.Intelligence.LogAnalyzer;
using CrawlForge.Intelligence.Tests.LogAnalyzer.Support;
using Xunit;

namespace CrawlForge.Intelligence.Tests.LogAnalyzer;

/// <summary>
/// Consented DNS verification tool (FS-CRAWL-002 §11.3, criterion #44):
/// consent is mandatory, samples are bounded, results log locally, and the
/// suites run fully offline through the scripted resolver.
/// </summary>
public sealed class LogDnsVerificationToolTests
{
    private static async Task<(LogAnalyzerTestDatabase Database, long ImportId, FakeDnsResolver Resolver, LogDnsVerificationTool Tool)> SetupAsync()
    {
        var database = await LogAnalyzerTestDatabase.CreateAsync();
        await database.SeedProjectAndAuditAsync(1, 1);
        var catalogue = BotCatalogue.Load(TestAssets.BotCataloguePath());
        var service = new LogImportService(
            database.Provider,
            new LogFileParser(),
            new BotVerifier(catalogue),
            new UrlCanonicalizer(),
            new RecordingHub(),
            catalogue);
        var path = database.WriteLogFile("access.log", LogSamples.CombinedSample());
        var outcome = await service.ImportAsync(new LogImportRequest(1, path, "https://example.com"));

        var resolver = new FakeDnsResolver();
        resolver.Reverse["66.249.66.1"] = "crawl-66-249-66-1.googlebot.com";
        resolver.Forward["crawl-66-249-66-1.googlebot.com"] = ["66.249.66.1"];
        var tool = new LogDnsVerificationTool(database.Provider, new BotVerifier(catalogue, resolver));
        return (database, outcome.ImportId, resolver, tool);
    }

    [Fact]
    public async Task WithoutConsentTheToolRefusesAndNeverResolves()
    {
        var (database, importId, resolver, tool) = await SetupAsync();
        await using var db = database;

        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.VerifySampleAsync(importId, consent: false));

        Assert.Equal(0, resolver.ReverseCalls);
        Assert.Equal(0, resolver.ForwardCalls);
    }

    [Fact]
    public async Task ConsentedVerificationUsesTheBoundedImportSample()
    {
        var (database, importId, resolver, tool) = await SetupAsync();
        await using var db = database;

        var results = await tool.VerifySampleAsync(importId, consent: true, sampleSize: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, resolver.ReverseCalls);
        var verified = results.Single(r => r.ClientIp == "66.249.66.1");
        Assert.True(verified.Verified);
        Assert.Equal("crawl-66-249-66-1.googlebot.com", verified.Hostname);
    }

    [Fact]
    public async Task SampleSizeIsClampedToTheHardBound()
    {
        var (database, importId, resolver, tool) = await SetupAsync();
        await using var db = database;

        var results = await tool.VerifySampleAsync(importId, consent: true, sampleSize: 10_000);

        // Only 3 bot IPs were sampled at import; the clamp never exceeds them.
        Assert.Equal(3, results.Count);
        Assert.True(resolver.ReverseCalls <= LogDnsVerificationTool.MaxSampleSize);
    }

    [Fact]
    public async Task ResultsAreLoggedLocallyIntoCountersJson()
    {
        var (database, importId, _, tool) = await SetupAsync();
        await using var db = database;

        await tool.VerifySampleAsync(importId, consent: true, sampleSize: 1);

        await using var connection = await database.Provider.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT counters_json FROM log_imports WHERE id = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = importId;
        command.Parameters.Add(parameter);
        var json = (string)(await command.ExecuteScalarAsync())!;
        using var document = JsonDocument.Parse(json);
        var section = document.RootElement.GetProperty("dnsVerification");
        Assert.True(section.TryGetProperty("verifiedAt", out _));
        Assert.Equal(1, section.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task UnknownImportThrows()
    {
        var (database, _, _, tool) = await SetupAsync();
        await using var db = database;

        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.VerifySampleAsync(9999, consent: true));
    }

    [Fact]
    public async Task NonPositiveSampleSizeThrows()
    {
        var (database, importId, _, tool) = await SetupAsync();
        await using var db = database;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => tool.VerifySampleAsync(importId, consent: true, sampleSize: 0));
    }
}
