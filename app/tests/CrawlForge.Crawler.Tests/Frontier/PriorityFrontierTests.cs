using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Frontier;
using CrawlForge.Crawler.Tests.Frontier.Support;
using Xunit;

namespace CrawlForge.Crawler.Tests.Frontier;

/// <summary>
/// FS-CRAWL-001 §5.3 priority frontier: seeds first, priority ordering with depth
/// tie-break, per-host sub-queues, politeness delegation through the locked
/// controller contract, and snapshot/restore fidelity.
/// </summary>
public class PriorityFrontierTests
{
    private static FrontierEntry Entry(string url, string host, int depth = 0, double priority = 0, DiscoverySource source = DiscoverySource.Link) =>
        new(url, host, depth, priority, source);

    [Fact]
    public async Task SeedsDequeueBeforeDiscoveredLinksRegardlessOfPriority()
    {
        var frontier = new PriorityFrontier(new TestPolitenessController());
        await frontier.EnqueueAsync(Entry("https://example.com/link", "example.com", priority: 100));
        await frontier.EnqueueAsync(Entry("https://example.com/seed", "example.com", priority: 0, source: DiscoverySource.Seed));
        await frontier.EnqueueAsync(Entry("https://example.com/list", "example.com", priority: 0, source: DiscoverySource.List));

        Assert.Equal("https://example.com/seed", (await frontier.DequeueAsync())!.Url);
        Assert.Equal("https://example.com/list", (await frontier.DequeueAsync())!.Url);
        Assert.Equal("https://example.com/link", (await frontier.DequeueAsync())!.Url);
    }

    [Fact]
    public async Task HigherPriorityDequeuesFirstWithinSameClass()
    {
        var frontier = new PriorityFrontier(new TestPolitenessController());
        await frontier.EnqueueAsync(Entry("https://example.com/low", "example.com", priority: 1));
        await frontier.EnqueueAsync(Entry("https://example.com/high", "example.com", priority: 9));
        await frontier.EnqueueAsync(Entry("https://example.com/mid", "example.com", priority: 5));

        Assert.Equal("https://example.com/high", (await frontier.DequeueAsync())!.Url);
        Assert.Equal("https://example.com/mid", (await frontier.DequeueAsync())!.Url);
        Assert.Equal("https://example.com/low", (await frontier.DequeueAsync())!.Url);
    }

    [Fact]
    public async Task DepthIsTheBreadthFirstTieBreaker()
    {
        var frontier = new PriorityFrontier(new TestPolitenessController());
        await frontier.EnqueueAsync(Entry("https://example.com/deep", "example.com", depth: 3, priority: 5));
        await frontier.EnqueueAsync(Entry("https://example.com/shallow", "example.com", depth: 1, priority: 5));
        await frontier.EnqueueAsync(Entry("https://example.com/mid", "example.com", depth: 2, priority: 5));

        Assert.Equal("https://example.com/shallow", (await frontier.DequeueAsync())!.Url);
        Assert.Equal("https://example.com/mid", (await frontier.DequeueAsync())!.Url);
        Assert.Equal("https://example.com/deep", (await frontier.DequeueAsync())!.Url);
    }

    [Fact]
    public async Task EqualKeysDequeueInFifoOrder()
    {
        var frontier = new PriorityFrontier(new TestPolitenessController());
        await frontier.EnqueueAsync(Entry("https://example.com/1", "example.com"));
        await frontier.EnqueueAsync(Entry("https://example.com/2", "example.com"));
        await frontier.EnqueueAsync(Entry("https://example.com/3", "example.com"));

        Assert.Equal("https://example.com/1", (await frontier.DequeueAsync())!.Url);
        Assert.Equal("https://example.com/2", (await frontier.DequeueAsync())!.Url);
        Assert.Equal("https://example.com/3", (await frontier.DequeueAsync())!.Url);
    }

    [Fact]
    public async Task DequeueDelegatesRateDecisionToPolitenessController()
    {
        var politeness = new TestPolitenessController();
        var frontier = new PriorityFrontier(politeness);
        await frontier.EnqueueAsync(Entry("https://a.example.com/1", "a.example.com", priority: 2));
        await frontier.EnqueueAsync(Entry("https://b.example.com/1", "b.example.com", priority: 1));

        Assert.Equal("https://a.example.com/1", (await frontier.DequeueAsync())!.Url);
        Assert.Equal("https://b.example.com/1", (await frontier.DequeueAsync())!.Url);
        Assert.Equal(["a.example.com", "b.example.com"], politeness.WaitedHosts);
    }

    [Fact]
    public async Task EmptyFrontierReturnsNull()
    {
        var frontier = new PriorityFrontier(new TestPolitenessController());
        Assert.Null(await frontier.DequeueAsync());
        Assert.Equal(0, frontier.Count);
    }

    [Fact]
    public async Task CountTracksQueuedEntriesAcrossHosts()
    {
        var frontier = new PriorityFrontier(new TestPolitenessController());
        await frontier.EnqueueAsync(Entry("https://a.example.com/1", "a.example.com"));
        await frontier.EnqueueAsync(Entry("https://b.example.com/1", "b.example.com"));
        await frontier.EnqueueAsync(Entry("https://a.example.com/2", "a.example.com"));
        Assert.Equal(3, frontier.Count);

        await frontier.DequeueAsync();
        Assert.Equal(2, frontier.Count);
    }

    [Fact]
    public async Task SnapshotRestoreReproducesExactDequeueOrder()
    {
        var politeness = new TestPolitenessController();
        var frontier = new PriorityFrontier(politeness);
        await frontier.EnqueueAsync(Entry("https://a.example.com/deep", "a.example.com", depth: 2, priority: 3));
        await frontier.EnqueueAsync(Entry("https://b.example.com/seed", "b.example.com", source: DiscoverySource.Seed));
        await frontier.EnqueueAsync(Entry("https://a.example.com/shallow", "a.example.com", depth: 0, priority: 3));
        await frontier.EnqueueAsync(Entry("https://c.example.com/high", "c.example.com", priority: 9));

        var snapshot = frontier.CreateSnapshot();
        Assert.Equal(4, snapshot.Entries.Count);

        // Simulated crash: brand-new frontier rehydrated from the snapshot.
        var restored = new PriorityFrontier(new TestPolitenessController());
        restored.Restore(snapshot);
        Assert.Equal(4, restored.Count);

        var originalOrder = new List<string>();
        while (await frontier.DequeueAsync() is { } entry)
        {
            originalOrder.Add(entry.Url);
        }

        var restoredOrder = new List<string>();
        while (await restored.DequeueAsync() is { } entry)
        {
            restoredOrder.Add(entry.Url);
        }

        Assert.Equal(originalOrder, restoredOrder);
        Assert.Equal(
            ["https://b.example.com/seed", "https://c.example.com/high", "https://a.example.com/shallow", "https://a.example.com/deep"],
            restoredOrder);
    }

    [Fact]
    public async Task EnqueueHonorsCancellation()
    {
        var frontier = new PriorityFrontier(new TestPolitenessController());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            frontier.EnqueueAsync(Entry("https://example.com/", "example.com"), cts.Token).AsTask());
    }

    [Fact]
    public async Task CancellationDuringPolitenessWaitPreservesTheEntry()
    {
        var politeness = new TestPolitenessController { WaitDelay = TimeSpan.FromSeconds(30) };
        var frontier = new PriorityFrontier(politeness);
        await frontier.EnqueueAsync(Entry("https://example.com/", "example.com"));

        using var cts = new CancellationTokenSource();
        var dequeue = frontier.DequeueAsync(cts.Token).AsTask();
        await Task.Delay(100); // let the politeness wait begin
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dequeue);
        Assert.Equal(1, frontier.Count); // entry returned to the queue, not lost
    }
}
