using CrawlForge.Contracts.Entities;
using CrawlForge.Intelligence.Embeddings;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Content-hash embedding cache (FS-CRAWL-002 §12.3): in-job dedup and stored
/// vector reuse; model-tag changes never reuse vectors.
/// </summary>
public class EmbeddingCacheTests : IClassFixture<IntelligenceSqliteStackFixture>
{
    private readonly IntelligenceSqliteStackFixture _stack;

    public EmbeddingCacheTests(IntelligenceSqliteStackFixture stack)
    {
        _stack = stack;
    }

    [Fact]
    public void MemoryCacheHitsOnHashAndTag()
    {
        var cache = new EmbeddingCache();
        cache.Store("hash-1", "model-a", [1f, 0f]);

        Assert.True(cache.TryGet("hash-1", "model-a", out var vector));
        Assert.Equal(new float[] { 1, 0 }, vector);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void MemoryCacheMissesOnChangedHashOrTag()
    {
        var cache = new EmbeddingCache();
        cache.Store("hash-1", "model-a", [1f]);

        Assert.False(cache.TryGet("hash-2", "model-a", out _));
        Assert.False(cache.TryGet("hash-1", "model-b", out _));
    }

    [Fact]
    public async Task StoredVectorIsReusedWhenHashAndTagMatch()
    {
        var (auditId, _, _) = await _stack.CreateAuditAsync();
        var urlId = await _stack.CreateUrlAsync(auditId, "/cached");
        var payload = new EmbeddingPayload("model-a", 2, "hash-current", [0.5f, 0.5f]);
        await _stack.Embeddings.CreateAsync(new UrlEmbeddingDto(0, auditId, urlId, payload.ToJson(), "model-a"));

        var reused = await EmbeddingCache.TryReuseStoredAsync(_stack.Embeddings, auditId, urlId, "hash-current", "model-a");

        Assert.NotNull(reused);
        Assert.Equal(urlId, reused.UrlId);
    }

    [Fact]
    public async Task ChangedContentHashForcesReEmbed()
    {
        var (auditId, _, _) = await _stack.CreateAuditAsync();
        var urlId = await _stack.CreateUrlAsync(auditId, "/changed");
        var payload = new EmbeddingPayload("model-a", 2, "hash-old", [0.5f, 0.5f]);
        await _stack.Embeddings.CreateAsync(new UrlEmbeddingDto(0, auditId, urlId, payload.ToJson(), "model-a"));

        var reused = await EmbeddingCache.TryReuseStoredAsync(_stack.Embeddings, auditId, urlId, "hash-new", "model-a");
        Assert.Null(reused);
    }

    [Fact]
    public async Task ChangedModelTagForcesReEmbed()
    {
        var (auditId, _, _) = await _stack.CreateAuditAsync();
        var urlId = await _stack.CreateUrlAsync(auditId, "/retag");
        var payload = new EmbeddingPayload("model-a", 2, "hash-current", [0.5f, 0.5f]);
        await _stack.Embeddings.CreateAsync(new UrlEmbeddingDto(0, auditId, urlId, payload.ToJson(), "model-a"));

        var reused = await EmbeddingCache.TryReuseStoredAsync(_stack.Embeddings, auditId, urlId, "hash-current", "model-b");
        Assert.Null(reused);
    }

    [Fact]
    public async Task MissingRowForcesEmbed()
    {
        var (auditId, _, _) = await _stack.CreateAuditAsync();
        var urlId = await _stack.CreateUrlAsync(auditId, "/missing");
        Assert.Null(await EmbeddingCache.TryReuseStoredAsync(_stack.Embeddings, auditId, urlId, "hash", "model-a"));
    }
}
