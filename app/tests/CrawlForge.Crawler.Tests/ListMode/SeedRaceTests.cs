using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// G-MATRIX-SEED-RACE seeder-side proof (PHASE-08-REPORT §4.2): the matrix
/// auto-seed and the crawl's own list seeding insert the same canonical URLs
/// concurrently. The ingestor must be idempotent and race-safe — duplicates
/// are counted, never surfaced as UNIQUE(url_records.audit_id, canonical_url)
/// failures, and only persisted rows reach the frontier.
/// </summary>
public class SeedRaceTests
{
    private static ListModeIngestor NewIngestor(DatabaseFixture fixture, RecordingFrontier frontier) =>
        new(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), frontier);

    private static async Task<long> CountUrlRecordsAsync(DatabaseFixture fixture, long auditId)
    {
        await using var connection = await fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM `url_records` WHERE `audit_id` = @auditId";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ConcurrentIngest_OverlappingLists_NoUniqueViolation()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var urls = Enumerable.Range(0, 40).Select(i => $"https://race.example/page/{i}").ToList();
        var frontierA = new RecordingFrontier();
        var frontierB = new RecordingFrontier();

        // Two independent ingestors race the same canonical URL set.
        var results = await Task.WhenAll(
            NewIngestor(fixture, frontierA).IngestAsync(auditId, urls, allowDiscovery: false),
            NewIngestor(fixture, frontierB).IngestAsync(auditId, urls, allowDiscovery: false));

        Assert.Equal(40, await CountUrlRecordsAsync(fixture, auditId));
        Assert.Equal(40, results.Sum(r => r.IngestedCount));
        Assert.Equal(40, results.Sum(r => r.DuplicateCount));
        Assert.All(results, r => Assert.Equal(0, r.InvalidCount));

        // Every persisted row was enqueued exactly once across both frontiers.
        var enqueued = frontierA.Enqueued.Concat(frontierB.Enqueued).Select(e => e.Url).ToList();
        Assert.Equal(40, enqueued.Count);
        Assert.Equal(40, enqueued.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task SequentialReingest_CountsDuplicates_EnqueuesNothing()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var frontier = new RecordingFrontier();
        var ingestor = NewIngestor(fixture, frontier);

        var first = await ingestor.IngestAsync(auditId, ["https://race.example/a", "https://race.example/b"], allowDiscovery: false);
        var second = await ingestor.IngestAsync(auditId, ["https://race.example/a", "https://race.example/b"], allowDiscovery: false);

        Assert.Equal(2, first.IngestedCount);
        Assert.Equal(0, second.IngestedCount);
        Assert.Equal(2, second.DuplicateCount);
        Assert.Equal(2, frontier.Enqueued.Count);
        Assert.Equal(2, await CountUrlRecordsAsync(fixture, auditId));
    }

    [Fact]
    public async Task MatrixAutoSeed_RacingListIngest_NoUniqueViolation()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var sources = Enumerable.Range(0, 25).Select(i => $"https://old.example/migrated/{i}").ToList();
        var matrixText = "Source_URL,Expected_Target_URL\n" +
                         string.Join("\n", sources.Select(s => $"{s},https://new.example/migrated"));
        var parsed = MatrixInputParser.Parse(matrixText);
        Assert.Equal(25, parsed.Rows.Count);

        var uploaderFrontier = new RecordingFrontier();
        var crawlFrontier = new RecordingFrontier();
        var uploader = new RedirectMatrixUploader(fixture.Provider, NewIngestor(fixture, uploaderFrontier));

        // The §4.2 race: upload auto-seeding and the crawl's own list seeding
        // run concurrently over the same source URLs.
        var uploadTask = uploader.UploadAsync(auditId, parsed, seedListMode: true);
        var ingestTask = NewIngestor(fixture, crawlFrontier).IngestAsync(auditId, sources, allowDiscovery: false);
        await Task.WhenAll(uploadTask, ingestTask);
        var ingestResult = await ingestTask;

        Assert.Equal(25, await CountUrlRecordsAsync(fixture, auditId));
        var mappings = await uploader.ReadMappingsAsync(auditId);
        Assert.Equal(25, mappings.Count);

        var totalEnqueued = uploaderFrontier.Enqueued.Count + crawlFrontier.Enqueued.Count;
        Assert.Equal(25, totalEnqueued);
        Assert.Equal(25, ingestResult.IngestedCount + ingestResult.DuplicateCount);
    }

    [Fact]
    public async Task ConcurrentIngest_DisjointLists_AllPersisted()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var listA = Enumerable.Range(0, 20).Select(i => $"https://race.example/a/{i}").ToList();
        var listB = Enumerable.Range(0, 20).Select(i => $"https://race.example/b/{i}").ToList();

        var results = await Task.WhenAll(
            NewIngestor(fixture, new RecordingFrontier()).IngestAsync(auditId, listA, allowDiscovery: false),
            NewIngestor(fixture, new RecordingFrontier()).IngestAsync(auditId, listB, allowDiscovery: false));

        Assert.Equal(40, await CountUrlRecordsAsync(fixture, auditId));
        Assert.Equal(40, results.Sum(r => r.IngestedCount));
        Assert.Equal(0, results.Sum(r => r.DuplicateCount));
    }
}
