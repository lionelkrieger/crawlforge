using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Redirect chain recorder (FS-CRAWL-001 §5.4): full chain per URL, loop
/// detection, chain cap, per-record isolation, and the persistence hook.
/// </summary>
public sealed class RedirectChainRecorderTests
{
    [Fact]
    public async Task RecordAndGet_ReturnsChainInHopOrder()
    {
        var recorder = new RedirectChainRecorder();

        await recorder.RecordHopAsync(1, 10, new RedirectHop(1, "http://a.example.com/b", "http://a.example.com/c", 302));
        await recorder.RecordHopAsync(1, 10, new RedirectHop(0, "http://a.example.com/a", "http://a.example.com/b", 301));

        IReadOnlyList<RedirectHop> chain = await recorder.GetChainAsync(1, 10);
        Assert.Equal(2, chain.Count);
        Assert.Equal(0, chain[0].Ordinal);
        Assert.Equal(1, chain[1].Ordinal);
        Assert.Equal("http://a.example.com/a", chain[0].FromUrl);
    }

    [Fact]
    public async Task Record_SelfRedirect_DetectedAsLoop()
    {
        var recorder = new RedirectChainRecorder();

        RedirectChainException ex = await Assert.ThrowsAsync<RedirectChainException>(
            () => recorder.RecordHopAsync(1, 10, new RedirectHop(0, "http://a.example.com/x", "http://a.example.com/x", 302)));

        Assert.Equal(RedirectChainViolation.Loop, ex.Reason);
    }

    [Fact]
    public async Task Record_TargetRevisitingEarlierSource_DetectedAsLoop()
    {
        var recorder = new RedirectChainRecorder();
        await recorder.RecordHopAsync(1, 10, new RedirectHop(0, "http://a.example.com/a", "http://a.example.com/b", 302));
        await recorder.RecordHopAsync(1, 10, new RedirectHop(1, "http://a.example.com/b", "http://a.example.com/c", 302));

        RedirectChainException ex = await Assert.ThrowsAsync<RedirectChainException>(
            () => recorder.RecordHopAsync(1, 10, new RedirectHop(2, "http://a.example.com/c", "http://a.example.com/a", 302)));

        Assert.Equal(RedirectChainViolation.Loop, ex.Reason);
        Assert.Equal(2, (await recorder.GetChainAsync(1, 10)).Count);
    }

    [Fact]
    public async Task Record_ExceedingChainCap_Throws()
    {
        var recorder = new RedirectChainRecorder(maxHops: 2);
        await recorder.RecordHopAsync(1, 10, new RedirectHop(0, "http://a.example.com/0", "http://a.example.com/1", 302));
        await recorder.RecordHopAsync(1, 10, new RedirectHop(1, "http://a.example.com/1", "http://a.example.com/2", 302));

        RedirectChainException ex = await Assert.ThrowsAsync<RedirectChainException>(
            () => recorder.RecordHopAsync(1, 10, new RedirectHop(2, "http://a.example.com/2", "http://a.example.com/3", 302)));

        Assert.Equal(RedirectChainViolation.ChainCapExceeded, ex.Reason);
    }

    [Fact]
    public async Task Chains_AreIsolatedPerAuditAndUrl()
    {
        var recorder = new RedirectChainRecorder();
        await recorder.RecordHopAsync(1, 10, new RedirectHop(0, "http://a.example.com/x", "http://a.example.com/y", 302));
        await recorder.RecordHopAsync(2, 10, new RedirectHop(0, "http://b.example.com/x", "http://b.example.com/y", 302));
        await recorder.RecordHopAsync(1, 11, new RedirectHop(0, "http://c.example.com/x", "http://c.example.com/y", 302));

        Assert.Single(await recorder.GetChainAsync(1, 10));
        Assert.Single(await recorder.GetChainAsync(2, 10));
        Assert.Single(await recorder.GetChainAsync(1, 11));
        Assert.Empty(await recorder.GetChainAsync(3, 99));
    }

    [Fact]
    public async Task PersistenceHook_EmitsFullChainAfterEachHop()
    {
        var snapshots = new List<RedirectChainSnapshot>();
        var recorder = new RedirectChainRecorder(
            maxHops: 10,
            persistAsync: (snapshot, _) =>
            {
                snapshots.Add(snapshot);
                return Task.CompletedTask;
            });

        await recorder.RecordHopAsync(5, 50, new RedirectHop(0, "http://a.example.com/a", "http://a.example.com/b", 301));
        await recorder.RecordHopAsync(5, 50, new RedirectHop(1, "http://a.example.com/b", "http://a.example.com/c", 302));

        Assert.Equal(2, snapshots.Count);
        Assert.Single(snapshots[0].Hops);
        Assert.Equal(2, snapshots[1].Hops.Count);
        Assert.Equal(5, snapshots[1].AuditId);
        Assert.Equal(50, snapshots[1].UrlId);
        Assert.Equal("http://a.example.com/c", snapshots[1].Hops[1].ToUrl);
    }

    [Fact]
    public async Task ParallelRecording_RemainsConsistent()
    {
        var recorder = new RedirectChainRecorder(maxHops: 4);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            await recorder.RecordHopAsync(1, i, new RedirectHop(0, $"http://h{i}.example.com/a", $"http://h{i}.example.com/b", 302));
            await recorder.RecordHopAsync(1, i, new RedirectHop(1, $"http://h{i}.example.com/b", $"http://h{i}.example.com/c", 302));
        }));

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(2, (await recorder.GetChainAsync(1, i)).Count);
        }
    }

    [Fact]
    public void Constructor_InvalidCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedirectChainRecorder(maxHops: 0));
    }
}