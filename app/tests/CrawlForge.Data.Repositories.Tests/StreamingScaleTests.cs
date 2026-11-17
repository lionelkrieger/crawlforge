using System.Data.Common;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Repositories.Tests.Harness;
using Xunit;

namespace CrawlForge.Data.Repositories.Tests;

/// <summary>
/// Scale discipline (precedence rule 7): streaming reads over a meaningful row
/// count must stay bounded-memory — rows are yielded one at a time and never
/// accumulated by the repository.
/// </summary>
public sealed class StreamingScaleTests
{
    private const int RowCount = 60_000;
    private const int PayloadBytes = 2048;

    [Fact]
    public async Task StreamAsync_SixtyThousandRows_StaysBoundedMemory()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedUrlRecordsAsync(database, RowCount);

        var repository = new SqlRepository<UrlRecordDto>(database.Provider);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baselineHeap = GC.GetTotalMemory(forceFullCollection: true);
        var peakHeapDelta = 0L;
        var count = 0;
        long lastId = 0;

        await foreach (var url in repository.StreamAsync(new ListQuery()))
        {
            count++;
            lastId = url.Id;
            if (count % 5_000 == 0)
            {
                var delta = GC.GetTotalMemory(forceFullCollection: false) - baselineHeap;
                peakHeapDelta = Math.Max(peakHeapDelta, delta);
            }
        }

        // The base chain contributes two additional url_records rows.
        Assert.Equal(RowCount + 2, count);
        Assert.True(lastId > RowCount);

        // Full materialization of 60k rows with 2 KB payloads would retain well
        // over 150 MB; a streaming reader must stay far below that ceiling.
        Assert.True(peakHeapDelta < 100_000_000,
            $"Streaming heap delta peaked at {peakHeapDelta:N0} bytes — expected bounded memory.");
    }

    [Fact]
    public async Task StreamAsync_EarlyTermination_DoesNotDrainRemainingRows()
    {
        const int seeded = 20_000;
        await using var database = await TestDatabase.CreateAsync();
        await SeedUrlRecordsAsync(database, seeded);
        var repository = new SqlRepository<UrlRecordDto>(database.Provider);

        var taken = 0;
        await foreach (var url in repository.StreamAsync(new ListQuery()))
        {
            taken++;
            Assert.True(url.Id > 0);
            if (taken == 10)
            {
                break;
            }
        }

        Assert.Equal(10, taken);
        Assert.Equal(seeded + 2, await database.CountAsync("url_records"));
    }

    [Fact]
    public async Task StreamWhereAsync_FilteredScan_YieldsOnlyMatchingRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedUrlRecordsAsync(database, 2_000);
        var repository = new SqlRepository<UrlRecordDto>(database.Provider);

        var count = 0;
        await foreach (var url in repository.StreamWhereAsync(
            "status_class = @status",
            new Dictionary<string, object?> { ["status"] = (long)CrawlStatusClass.Blocked }))
        {
            Assert.Equal(CrawlStatusClass.Blocked, url.StatusClass);
            count++;
        }

        Assert.Equal(200, count);
    }

    private static async Task SeedUrlRecordsAsync(TestDatabase database, int count)
    {
        var payload = new string('t', PayloadBytes);
        var map = EntityMap<UrlRecordDto>.Instance.Map;

        await using var connection = await database.Provider.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        for (var i = 0; i < count; i++)
        {
            var record = new UrlRecordDto(
                0, 1, $"https://example.test/page/{i}", "example.test", $"/page/{i}",
                Depth: i % 8, DiscoverySource.Link,
                StatusClass: i % 10 == 0 ? CrawlStatusClass.Blocked : CrawlStatusClass.Success,
                FinalStatusCode: 200, RedirectTargetUrl: null, ContentType: "text/html",
                SizeBytes: PayloadBytes, TimingJson: payload,
                RobotsState.Allowed, IndexabilityFlags.None, IsIndexable: true);
            await RepositoryOps.CreateAsync(map, database.Provider.Kind, record, connection, transaction);
        }

        await transaction.CommitAsync();
    }
}
