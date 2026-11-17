using System.Security.Cryptography;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.ContentStore;
using CrawlForge.Data.Repositories;
using CrawlForge.Data.Repositories.Tests.Harness;
using Xunit;

namespace CrawlForge.Data.Repositories.Tests;

/// <summary>
/// Compressed content store (criterion #15): streaming put/get for all artifact
/// kinds, content-hash dedup across audits, offline re-audit retrieval, upsert
/// semantics with orphan cleanup, and path-traversal-safe storage keys.
/// </summary>
public sealed class ContentStoreTests
{
    [Theory]
    [InlineData(ContentArtifactKind.Body)]
    [InlineData(ContentArtifactKind.RenderedDom)]
    [InlineData(ContentArtifactKind.Screenshot)]
    public async Task PutAndOpenRead_RoundTrips_AllArtifactKinds(ContentArtifactKind kind)
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var payload = Enumerable.Repeat((byte)'x', 50_000).ToArray();

        var artifact = await store.PutAsync(1, 1, kind, new MemoryStream(payload));

        Assert.True(artifact.Id > 0);
        Assert.Equal(kind, artifact.Kind);
        Assert.Equal(SHA256.HashData(payload).Select(b => b.ToString("x2")).Aggregate((a, b) => a + b), artifact.ContentHash);
        Assert.True(artifact.SizeBytes > 0);
        Assert.True(artifact.SizeBytes < payload.Length, "Compressed size must be smaller for compressible content.");

        await using var read = await store.OpenReadAsync(artifact.StorageKey);
        Assert.NotNull(read);
        using var buffer = new MemoryStream();
        await read!.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());

        var indexed = await store.GetForUrlAsync(1, 1);
        Assert.Single(indexed);
        Assert.Equal(artifact.StorageKey, indexed[0].StorageKey);
    }

    [Fact]
    public async Task Put_IdenticalContentAcrossAudits_StoresOneFile()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var secondAudit = await CreateSecondAuditAsync(database);
        var secondUrl = await CreateUrlAsync(database, secondAudit, "https://example.test/shared");
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        var first = await store.PutAsync(1, 1, ContentArtifactKind.Body, new MemoryStream(payload));
        var second = await store.PutAsync(secondAudit, secondUrl, ContentArtifactKind.Body, new MemoryStream(payload));

        Assert.Equal(first.StorageKey, second.StorageKey);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Single(await store.ListStorageKeysAsync());
        Assert.Equal(2, await database.CountAsync("content_artifacts"));
    }

    [Fact]
    public async Task Put_ReplacingContent_ReclaimsUnreferencedFile()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);

        var first = await store.PutAsync(1, 1, ContentArtifactKind.Body, new MemoryStream(new byte[] { 9, 9, 9 }));
        var replaced = await store.PutAsync(1, 1, ContentArtifactKind.Body, new MemoryStream(new byte[] { 1, 2, 3 }));

        Assert.NotEqual(first.StorageKey, replaced.StorageKey);
        Assert.False(store.Exists(first.StorageKey));
        Assert.True(store.Exists(replaced.StorageKey));
        Assert.Single(await store.GetForUrlAsync(1, 1));
        Assert.Single(await store.ListStorageKeysAsync());
    }

    [Fact]
    public async Task Put_SharedContentReplacement_KeepsFileWhileReferenced()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var secondAudit = await CreateSecondAuditAsync(database);
        var secondUrl = await CreateUrlAsync(database, secondAudit, "https://example.test/shared");
        var shared = new byte[] { 4, 4, 4, 4 };

        var first = await store.PutAsync(1, 1, ContentArtifactKind.Body, new MemoryStream(shared));
        await store.PutAsync(secondAudit, secondUrl, ContentArtifactKind.Body, new MemoryStream(shared));

        await store.PutAsync(1, 1, ContentArtifactKind.Body, new MemoryStream(new byte[] { 7 }));

        Assert.True(store.Exists(first.StorageKey), "File still referenced by the second audit must survive.");
    }

    [Fact]
    public async Task StreamForProjectUrl_RetrievesPriorAuditContent_Offline()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var secondAudit = await CreateSecondAuditAsync(database);
        var secondUrl = await CreateUrlAsync(database, secondAudit, "https://example.test/shared");
        var olderPayload = new byte[] { 10, 20, 30 };
        var newerPayload = new byte[] { 40, 50, 60 };

        // Url 1 of the base chain is re-pointed at the shared canonical URL.
        var urls = new SqlRepository<UrlRecordDto>(database.Provider);
        var baseUrl = await urls.GetByIdAsync(1);
        await urls.UpdateAsync(baseUrl! with { CanonicalUrl = "https://example.test/shared" });

        var older = await store.PutAsync(1, 1, ContentArtifactKind.Body, new MemoryStream(olderPayload));
        var newer = await store.PutAsync(secondAudit, secondUrl, ContentArtifactKind.Body, new MemoryStream(newerPayload));

        var rows = new List<ContentArtifactDto>();
        await foreach (var row in store.StreamForProjectUrlAsync(1, "https://example.test/shared", ContentArtifactKind.Body))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(older.StorageKey, rows[0].StorageKey);
        Assert.Equal(newer.StorageKey, rows[1].StorageKey);

        // Re-audit without re-fetching: the older body is still readable locally.
        await using var read = await store.OpenReadAsync(rows[0].StorageKey);
        using var buffer = new MemoryStream();
        await read!.CopyToAsync(buffer);
        Assert.Equal(olderPayload, buffer.ToArray());
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("sha256/../../etc/passwd")]
    [InlineData("sha256/zz/not-a-hash")]
    [InlineData("sha256/ab/ABC")]
    [InlineData("")]
    public async Task StorageKeys_AreValidated_RejectingTraversal(string badKey)
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);

        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync(badKey));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(badKey));
        Assert.Throws<ArgumentException>(() => store.Exists(badKey));
    }

    [Fact]
    public async Task OpenRead_MissingKey_ReturnsNull_AndDeleteReturnsFalse()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var missingKey = "sha256/ab/" + new string('0', 64);

        Assert.Null(await store.OpenReadAsync(missingKey));
        Assert.False(await store.DeleteAsync(missingKey));
    }

    [Fact]
    public async Task Put_LargePayload_StreamsBoundedMemory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        const long length = 24 * 1024 * 1024;

        var source = new PatternStream(length);
        var artifact = await store.PutAsync(1, 1, ContentArtifactKind.Body, source);

        Assert.Equal(length, source.BytesRead);
        Assert.True(source.PeakHeapDelta < 64_000_000,
            $"PutAsync heap delta peaked at {source.PeakHeapDelta:N0} bytes — expected streaming.");
        Assert.True(artifact.SizeBytes < length);

        // Verify integrity by streaming the stored content back through SHA-256.
        await using var read = await store.OpenReadAsync(artifact.StorageKey);
        Assert.NotNull(read);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long total = 0;
        int got;
        while ((got = await read!.ReadAsync(buffer)) > 0)
        {
            hasher.AppendData(buffer, 0, got);
            total += got;
        }

        Assert.Equal(length, total);
        var expectedHash = Convert.ToHexString(PatternStream.ExpectedHash(length)).ToLowerInvariant();
        Assert.Equal(expectedHash, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
        Assert.Equal(expectedHash, artifact.ContentHash);
    }

    [Fact]
    public async Task ArtifactFileStore_SaveCompressed_Deduplicates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);
        var key = "sha256/cd/" + new string('a', 64);

        Assert.True(await store.SaveCompressedAsync(key, new MemoryStream(new byte[] { 1 })));
        Assert.False(await store.SaveCompressedAsync(key, new MemoryStream(new byte[] { 2 })));
        Assert.True(store.Exists(key));
        Assert.Contains(key, await store.ListStorageKeysAsync());
        Assert.True(await store.DeleteCompressedAsync(key));
        Assert.False(store.Exists(key));
    }

    [Fact]
    public async Task GetTotalStoredBytes_ReportsCompressedSize()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new CompressedContentStore(database.ContentStoreRoot, database.Provider);

        var artifact = await store.PutAsync(1, 1, ContentArtifactKind.Body, new MemoryStream(Enumerable.Repeat((byte)'z', 10_000).ToArray()));

        Assert.Equal(artifact.SizeBytes, await store.GetTotalStoredBytesAsync());
    }

    private static async Task<long> CreateSecondAuditAsync(TestDatabase database)
    {
        var audits = new SqlRepository<AuditDto>(database.Provider);
        var created = await audits.CreateAsync(SampleFactory.Create<AuditDto>());
        return created.Id;
    }

    private static async Task<long> CreateUrlAsync(TestDatabase database, long auditId, string canonicalUrl)
    {
        var urls = new SqlRepository<UrlRecordDto>(database.Provider);
        var created = await urls.CreateAsync(SampleFactory.Create<UrlRecordDto>() with
        {
            AuditId = auditId,
            CanonicalUrl = canonicalUrl,
            Path = new Uri(canonicalUrl).AbsolutePath
        });
        return created.Id;
    }

    /// <summary>
    /// Deterministic byte-pattern stream that never holds the full payload in
    /// memory; samples the managed heap while the store consumes it.
    /// </summary>
    private sealed class PatternStream : Stream
    {
        private readonly long _length;
        private long _position;
        private long _heapBaseline;

        public PatternStream(long length)
        {
            _length = length;
            GC.Collect();
            _heapBaseline = GC.GetTotalMemory(forceFullCollection: true);
        }

        public long BytesRead { get; private set; }

        public long PeakHeapDelta { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public static byte[] ExpectedHash(long length)
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunk = new byte[81_920];
            var remaining = length;
            long offset = 0;
            while (remaining > 0)
            {
                var take = (int)Math.Min(chunk.Length, remaining);
                Fill(chunk, take, offset);
                hasher.AppendData(chunk, 0, take);
                remaining -= take;
                offset += take;
            }

            return hasher.GetHashAndReset();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var take = (int)Math.Min(count, remaining);
            Fill(buffer.AsSpan(offset, take), take, _position);
            _position += take;
            BytesRead += take;
            if (BytesRead % (4 * 1024 * 1024) < take)
            {
                PeakHeapDelta = Math.Max(PeakHeapDelta, GC.GetTotalMemory(forceFullCollection: false) - _heapBaseline);
            }

            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var array = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                var read = Read(array, 0, buffer.Length);
                array.AsMemory(0, read).CopyTo(buffer);
                return ValueTask.FromResult(read);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(array);
            }
        }

        private static void Fill(byte[] buffer, int count, long position) => Fill(buffer.AsSpan(0, count), count, position);

        private static void Fill(Span<byte> buffer, int count, long position)
        {
            for (var i = 0; i < count; i++)
            {
                buffer[i] = (byte)((position + i) % 251);
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
