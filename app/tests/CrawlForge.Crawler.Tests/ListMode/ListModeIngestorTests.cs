using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// Criterion #43 ingestion evidence over the real SQLite stack: list seeds
/// carry discovery source <c>list</c> at depth 0; list-only crawls disable
/// discovery; duplicates and invalid entries are counted, never thrown.
/// </summary>
public class ListModeIngestorTests
{
    /// <summary>Ingestion counters split accepted, duplicate, and invalid entries.</summary>
    [Fact]
    public async Task Ingestion_Counters_Are_Exact()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var frontier = new RecordingFrontier();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), frontier);

        var result = await ingestor.IngestAsync(auditId, [
            "https://example.com/a",
            "https://example.com/b",
            "https://example.com/a",
            "not-a-url",
        ], allowDiscovery: false);

        Assert.Equal(new ListIngestionResult(2, 1, 1), result);
        Assert.Equal(2, frontier.Enqueued.Count);
    }

    /// <summary>List seeds persist at depth 0 with discovery source list.</summary>
    [Fact]
    public async Task Seeds_Persist_With_List_Source_At_Depth_Zero()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), new RecordingFrontier());

        await ingestor.IngestAsync(auditId, ["https://example.com/a"], allowDiscovery: false);

        await using var connection = await fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT `depth`, `discovery_source`, `status_class`, `host`, `path` FROM `url_records` WHERE `audit_id` = @auditId";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal((int)DiscoverySource.List, reader.GetInt32(1));
        Assert.Equal((int)CrawlStatusClass.NotCrawled, reader.GetInt32(2));
        Assert.Equal("example.com", reader.GetString(3));
        Assert.Equal("/a", reader.GetString(4));
        Assert.False(await reader.ReadAsync());
    }

    /// <summary>Frontier entries carry depth 0 and the list discovery source.</summary>
    [Fact]
    public async Task Frontier_Entries_Are_Depth_Zero_List_Seeds()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var frontier = new RecordingFrontier();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), frontier);

        await ingestor.IngestAsync(auditId, ["https://example.com/a", "https://example.com/b"], allowDiscovery: false);

        Assert.All(frontier.Enqueued, entry =>
        {
            Assert.Equal(0, entry.Depth);
            Assert.Equal(DiscoverySource.List, entry.DiscoverySource);
            Assert.Equal("example.com", entry.Host);
        });
    }

    /// <summary>Re-ingestion deduplicates against already-persisted URL records.</summary>
    [Fact]
    public async Task Reingestion_Deduplicates_Against_Persisted_Records()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), new RecordingFrontier());

        await ingestor.IngestAsync(auditId, ["https://example.com/a"], allowDiscovery: false);
        var second = await ingestor.IngestAsync(auditId, ["https://example.com/a", "https://example.com/c"], allowDiscovery: false);

        Assert.Equal(new ListIngestionResult(1, 1, 0), second);
    }

    /// <summary>Rewrite rules apply before seen-set keying (§10.4): two raw URLs
    /// rewritten to the same canonical URL ingest once.</summary>
    [Fact]
    public async Task Rewriting_Applies_Before_Dedup()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var options = new ListModeOptions(RewriteRules: [new UrlRewriteRule("/legacy/", "/modern/")]);
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), new RecordingFrontier(), options);

        var result = await ingestor.IngestAsync(auditId, [
            "https://example.com/legacy/a",
            "https://example.com/modern/a",
        ], allowDiscovery: false);

        Assert.Equal(new ListIngestionResult(1, 1, 0), result);
    }

    /// <summary>Canonicalization deduplicates case/trailing-slash variants.</summary>
    [Fact]
    public async Task Canonicalization_Deduplicates_Variants()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), new RecordingFrontier());

        var result = await ingestor.IngestAsync(auditId, [
            "https://EXAMPLE.com/a/",
            "https://example.com/a",
        ], allowDiscovery: false);

        Assert.Equal(new ListIngestionResult(1, 1, 0), result);
    }

    /// <summary>List-only policy forces depth ceiling 0 and disables discovery.</summary>
    [Fact]
    public async Task ListOnly_Policy_Forces_Depth_Zero_And_Disables_Discovery()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), new RecordingFrontier());
        await ingestor.IngestAsync(auditId, ["https://example.com/a"], allowDiscovery: false);

        var policy = await ingestor.ResolvePolicyAsync(auditId, allowDiscovery: false);

        Assert.Equal(ListDiscoveryMode.ListOnly, policy.Mode);
        Assert.Equal(0, policy.DepthCeiling);
        Assert.False(policy.DiscoveryEnabled);
        Assert.Contains("https://example.com/a", policy.AllowedCanonicalUrls);
    }

    /// <summary>List-bounded policy enables discovery restricted to the list set.</summary>
    [Fact]
    public async Task ListBounded_Policy_Carries_The_List_Set()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), new RecordingFrontier());
        await ingestor.IngestAsync(auditId, ["https://example.com/a", "https://example.com/b"], allowDiscovery: true);

        var policy = await ingestor.ResolvePolicyAsync(auditId, allowDiscovery: true);

        Assert.Equal(ListDiscoveryMode.ListBounded, policy.Mode);
        Assert.True(policy.DiscoveryEnabled);
        Assert.Equal(2, policy.AllowedCanonicalUrls.Count);
    }

    /// <summary>An entirely invalid list ingests nothing and never throws.</summary>
    [Fact]
    public async Task All_Invalid_List_Ingests_Nothing()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var frontier = new RecordingFrontier();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), frontier);

        var result = await ingestor.IngestAsync(auditId, ["garbage", "/relative", "ftp://example.com/x"], allowDiscovery: false);

        Assert.Equal(new ListIngestionResult(0, 0, 3), result);
        Assert.Empty(frontier.Enqueued);
    }
}
