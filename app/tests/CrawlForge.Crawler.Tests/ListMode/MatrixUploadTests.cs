using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// FS-CRAWL-002 §10.3 upload evidence over the real SQLite stack: two-column
/// matrices persist to redirect_mappings (unique per audit + source URL,
/// ordinal preserved) and auto-seed a list-only audit from the Source column.
/// </summary>
public class MatrixUploadTests
{
    private static (ListModeIngestor Ingestor, RecordingFrontier Frontier) CreateIngestor(DatabaseFixture fixture)
    {
        var frontier = new RecordingFrontier();
        var ingestor = new ListModeIngestor(fixture.Provider, new TestCanonicalizer(), new TestRewriteEngine(), frontier);
        return (ingestor, frontier);
    }

    /// <summary>Uploaded rows persist with their ordinals and both URL columns.</summary>
    [Fact]
    public async Task Upload_Persists_Rows_With_Ordinals()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var (ingestor, _) = CreateIngestor(fixture);
        var uploader = new RedirectMatrixUploader(fixture.Provider, ingestor);
        var parsed = MatrixInputParser.Parse(
            "Source_URL,Expected_Target_URL\nhttps://old.example.com/a,https://new.example.com/a\nhttps://old.example.com/b,https://new.example.com/b\n");

        var result = await uploader.UploadAsync(auditId, parsed);

        Assert.Equal(2, result.PersistedCount);
        Assert.Equal(0, result.DuplicateCount);
        var mappings = await uploader.ReadMappingsAsync(auditId);
        Assert.Equal(2, mappings.Count);
        Assert.Equal(0, mappings[0].Ordinal);
        Assert.Equal(1, mappings[1].Ordinal);
        Assert.Equal("https://old.example.com/a", mappings[0].SourceUrl);
        Assert.Equal("https://new.example.com/b", mappings[1].ExpectedTargetUrl);
        Assert.All(mappings, m => Assert.Equal(auditId, m.AuditId));
    }

    /// <summary>Re-uploading the same source URLs counts duplicates, never re-inserts.</summary>
    [Fact]
    public async Task Upload_Deduplicates_By_Audit_And_Source()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var (ingestor, _) = CreateIngestor(fixture);
        var uploader = new RedirectMatrixUploader(fixture.Provider, ingestor);
        var parsed = MatrixInputParser.Parse("https://old.example.com/a,https://new.example.com/a\n");

        await uploader.UploadAsync(auditId, parsed);
        var second = await uploader.UploadAsync(auditId, parsed);

        Assert.Equal(0, second.PersistedCount);
        Assert.Equal(1, second.DuplicateCount);
        Assert.Single(await uploader.ReadMappingsAsync(auditId));
    }

    /// <summary>The upload auto-seeds a list-only audit from the Source column.</summary>
    [Fact]
    public async Task Upload_Auto_Seeds_List_Only_Audit()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var (ingestor, frontier) = CreateIngestor(fixture);
        var uploader = new RedirectMatrixUploader(fixture.Provider, ingestor);
        var parsed = MatrixInputParser.Parse(
            "https://old.example.com/a,https://new.example.com/a\nhttps://old.example.com/b,https://new.example.com/b\n");

        var result = await uploader.UploadAsync(auditId, parsed, seedListMode: true);

        Assert.NotNull(result.Seeding);
        Assert.Equal(2, result.Seeding!.IngestedCount);
        Assert.All(frontier.Enqueued, entry =>
        {
            Assert.Equal(0, entry.Depth);
            Assert.Equal(DiscoverySource.List, entry.DiscoverySource);
        });

        await using var connection = await fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM `url_records` WHERE `audit_id` = @auditId AND `discovery_source` = 4 AND `depth` = 0";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    /// <summary>Seeding can be declined when attaching to an existing list-mode audit.</summary>
    [Fact]
    public async Task Upload_Without_Seeding_Leaves_Frontier_Empty()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var (ingestor, frontier) = CreateIngestor(fixture);
        var uploader = new RedirectMatrixUploader(fixture.Provider, ingestor);
        var parsed = MatrixInputParser.Parse("https://old.example.com/a,https://new.example.com/a\n");

        var result = await uploader.UploadAsync(auditId, parsed, seedListMode: false);

        Assert.Null(result.Seeding);
        Assert.Empty(frontier.Enqueued);
        Assert.Single(await uploader.ReadMappingsAsync(auditId));
    }

    /// <summary>Invalid rows are counted from the parse diagnostics, valid rows persist.</summary>
    [Fact]
    public async Task Upload_Carries_Invalid_Row_Count()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var (ingestor, _) = CreateIngestor(fixture);
        var uploader = new RedirectMatrixUploader(fixture.Provider, ingestor);
        var parsed = MatrixInputParser.Parse(
            "https://old.example.com/a,https://new.example.com/a\nbad-row\n");

        var result = await uploader.UploadAsync(auditId, parsed);

        Assert.Equal(1, result.PersistedCount);
        Assert.Equal(1, result.InvalidCount);
    }
}
