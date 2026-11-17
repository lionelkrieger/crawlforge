using CrawlForge.Crawler.Canonicalization;
using Xunit;

namespace CrawlForge.Crawler.Tests.Frontier;

/// <summary>
/// FS-CRAWL-001 §5.2 persistent seen-set: dedup, crash-durable append-only file,
/// streaming rebuild, and bounded-memory behavior at scale.
/// </summary>
public class PersistentSeenSetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cf-seenset-tests", Guid.NewGuid().ToString("N"));

    public PersistentSeenSetTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    [Fact]
    public void TryAddDeduplicatesCanonicalUrls()
    {
        using var seen = new PersistentSeenSet(PathFor("seen.log"));
        Assert.True(seen.TryAdd("https://example.com/a"));
        Assert.False(seen.TryAdd("https://example.com/a"));
        Assert.True(seen.TryAdd("https://example.com/b"));
        Assert.Equal(2, seen.Count);
        Assert.True(seen.Contains("https://example.com/a"));
        Assert.False(seen.Contains("https://example.com/missing"));
    }

    [Fact]
    public void SurvivesReopenAfterSimulatedProcessLoss()
    {
        var path = PathFor("seen.log");
        using (var seen = new PersistentSeenSet(path))
        {
            seen.TryAdd("https://example.com/a");
            seen.TryAdd("https://example.com/b");
            seen.Flush();
        }

        // Fresh instance over the same file: state rehydrated by streaming.
        using var reopened = new PersistentSeenSet(path);
        Assert.Equal(2, reopened.Count);
        Assert.False(reopened.TryAdd("https://example.com/a"));
        Assert.True(reopened.TryAdd("https://example.com/c"));
        Assert.Equal(3, reopened.Count);
    }

    [Fact]
    public void RebuildFromSwitchesToCheckpointReference()
    {
        var originalPath = PathFor("original.log");
        using (var original = new PersistentSeenSet(originalPath))
        {
            original.TryAdd("https://example.com/one");
            original.TryAdd("https://example.com/two");
            original.Flush();
        }

        using (var resumed = new PersistentSeenSet(PathFor("other.log")))
        {
            Assert.Equal(0, resumed.Count);
            resumed.RebuildFrom(originalPath);
            Assert.Equal(originalPath, resumed.Reference);
            Assert.Equal(2, resumed.Count);
            Assert.True(resumed.Contains("https://example.com/one"));

            // New entries append to the referenced file.
            resumed.TryAdd("https://example.com/three");
            resumed.Flush();
        }

        // The writer handle is released before another reader opens the file.
        using var verify = new PersistentSeenSet(originalPath);
        Assert.Equal(3, verify.Count);
    }

    [Fact]
    public void HandlesHundredThousandUrlsWithBoundedIndex()
    {
        var path = PathFor("large.log");
        using (var seen = new PersistentSeenSet(path))
        {
            for (var i = 0; i < 100_000; i++)
            {
                Assert.True(seen.TryAdd($"https://example.com/page/{i}"));
            }

            Assert.Equal(100_000, seen.Count);
            seen.Flush();
        }

        using var reopened = new PersistentSeenSet(path);
        Assert.Equal(100_000, reopened.Count);
        Assert.False(reopened.TryAdd("https://example.com/page/42"));
        Assert.True(reopened.TryAdd("https://example.com/page/100000"));
    }

    [Fact]
    public void HashIsDeterministicAndUrlSensitive()
    {
        Assert.Equal(PersistentSeenSet.Hash128("https://example.com/a"), PersistentSeenSet.Hash128("https://example.com/a"));
        Assert.NotEqual(PersistentSeenSet.Hash128("https://example.com/a"), PersistentSeenSet.Hash128("https://example.com/b"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
