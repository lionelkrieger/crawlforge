using System.Buffers;
using System.Data.Common;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Data.Repositories;

namespace CrawlForge.Data.ContentStore;

/// <summary>
/// Compressed content-artifact store (FS-CRAWL-001 §9.6): response bodies, rendered
/// DOM snapshots, and screenshots persisted as compressed, managed files in a local
/// directory beside the database, independent of the database provider, indexed by
/// <c>content_artifacts</c>. Files are content-hash addressed (SHA-256), so identical
/// content is stored once and addressable across audits of the same project —
/// enabling re-audit without re-fetching and cross-audit comparison, fully offline.
/// All reads and writes stream with a fixed buffer; artifacts are never held wholly
/// in memory (precedence rule 7).
/// </summary>
public sealed class CompressedContentStore : IContentStore, IArtifactFileStore
{
    private const int BufferSize = 81_920;
    private const string KeyPrefix = "sha256/";

    private static readonly Regex StorageKeyPattern = new("^sha256/[0-9a-f]{2}/[0-9a-f]{64}$", RegexOptions.Compiled);

    private readonly string _rootDirectory;
    private readonly string _tempDirectory;
    private readonly IDbProvider _provider;

    /// <summary>Creates the store rooted at a local directory beside the database.</summary>
    /// <param name="rootDirectory">Local content-store root directory.</param>
    /// <param name="provider">The configured database provider (index rows).</param>
    public CompressedContentStore(string rootDirectory, IDbProvider provider)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _tempDirectory = Path.Combine(_rootDirectory, "tmp");
        _provider = provider;
        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(_tempDirectory);
    }

    /// <inheritdoc />
    public async Task<ContentArtifactDto> PutAsync(long auditId, long urlId, ContentArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
    {
        var (storageKey, compressedSize, contentHash, tempPath) = await WriteCompressedAsync(content, cancellationToken);
        var finalPath = ResolvePath(storageKey);

        if (File.Exists(finalPath))
        {
            // Content-hash dedup: identical content is already stored.
            File.Delete(tempPath);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.Move(tempPath, finalPath);
        }

        // Upsert the (audit, url, kind) index row inside one transaction.
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string? previousKey = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT storage_key FROM content_artifacts WHERE audit_id = @audit AND url_id = @url AND kind = @kind";
            AddParameter(select, "@audit", auditId);
            AddParameter(select, "@url", urlId);
            AddParameter(select, "@kind", (long)kind);
            previousKey = await select.ExecuteScalarAsync(cancellationToken) as string;
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM content_artifacts WHERE audit_id = @audit AND url_id = @url AND kind = @kind";
            AddParameter(delete, "@audit", auditId);
            AddParameter(delete, "@url", urlId);
            AddParameter(delete, "@kind", (long)kind);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        long newId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO content_artifacts (audit_id, url_id, kind, storage_key, size_bytes, content_hash) " +
                "VALUES (@audit, @url, @kind, @key, @size, @hash)";
            AddParameter(insert, "@audit", auditId);
            AddParameter(insert, "@url", urlId);
            AddParameter(insert, "@kind", (long)kind);
            AddParameter(insert, "@key", storageKey);
            AddParameter(insert, "@size", compressedSize);
            AddParameter(insert, "@hash", contentHash);
            await insert.ExecuteNonQueryAsync(cancellationToken);

            insert.CommandText = _provider.Kind == DatabaseProviderKind.Sqlite
                ? "SELECT last_insert_rowid()"
                : "SELECT LAST_INSERT_ID()";
            insert.Parameters.Clear();
            newId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);

        // Reclaim the replaced file when no other index row still references it.
        if (previousKey is not null && !string.Equals(previousKey, storageKey, StringComparison.Ordinal))
        {
            await DeleteIfUnreferencedAsync(previousKey, cancellationToken);
        }

        return new ContentArtifactDto(newId, auditId, urlId, kind, storageKey, compressedSize, contentHash);
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);
        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream fileStream = File.OpenRead(path);
        return Task.FromResult<Stream?>(new BrotliStream(fileStream, CompressionMode.Decompress));
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
        DeleteCompressedAsync(storageKey, cancellationToken);

    /// <inheritdoc />
    public bool Exists(string storageKey) => File.Exists(ResolvePath(storageKey));

    /// <inheritdoc />
    public Task<Stream?> OpenCompressedAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    /// <inheritdoc />
    public async Task<bool> SaveCompressedAsync(string storageKey, Stream compressedContent, CancellationToken cancellationToken = default)
    {
        var finalPath = ResolvePath(storageKey);
        if (File.Exists(finalPath))
        {
            return false;
        }

        var tempPath = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        await using (var tempFile = File.Create(tempPath))
        {
            await compressedContent.CopyToAsync(tempFile, BufferSize, cancellationToken);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        if (File.Exists(finalPath))
        {
            File.Delete(tempPath);
            return false;
        }

        File.Move(tempPath, finalPath);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> DeleteCompressedAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListStorageKeysAsync(CancellationToken cancellationToken = default)
    {
        var keys = new List<string>();
        var hashRoot = Path.Combine(_rootDirectory, "sha256");
        if (!Directory.Exists(hashRoot))
        {
            return Task.FromResult<IReadOnlyList<string>>(keys);
        }

        foreach (var directory in Directory.EnumerateDirectories(hashRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                var key = $"{KeyPrefix}{Path.GetFileName(directory)}/{name}";
                if (StorageKeyPattern.IsMatch(key))
                {
                    keys.Add(key);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    /// <summary>Lists the index rows for one URL of one audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="urlId">URL record identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The artifact index rows.</returns>
    public async Task<IReadOnlyList<ContentArtifactDto>> GetForUrlAsync(long auditId, long urlId, CancellationToken cancellationToken = default)
    {
        var repository = new SqlRepository<ContentArtifactDto>(_provider);
        var results = new List<ContentArtifactDto>();
        await foreach (var artifact in repository.StreamWhereAsync(
            "audit_id = @audit AND url_id = @url",
            new Dictionary<string, object?> { ["audit"] = auditId, ["url"] = urlId },
            cancellationToken: cancellationToken))
        {
            results.Add(artifact);
        }

        return results;
    }

    /// <summary>
    /// Streams artifact index rows for one canonical URL across every audit of a
    /// project — the cross-audit comparison / re-audit retrieval path (fully offline).
    /// </summary>
    /// <param name="projectId">Project identifier.</param>
    /// <param name="canonicalUrl">Canonical URL shared across audits.</param>
    /// <param name="kind">Artifact kind.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of matching index rows, oldest audit first.</returns>
    public async IAsyncEnumerable<ContentArtifactDto> StreamForProjectUrlAsync(
        long projectId, string canonicalUrl, ContentArtifactKind kind,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ca.id, ca.audit_id, ca.url_id, ca.kind, ca.storage_key, ca.size_bytes, ca.content_hash " +
            "FROM content_artifacts ca " +
            "JOIN url_records u ON ca.url_id = u.id " +
            "JOIN audits a ON ca.audit_id = a.id " +
            "WHERE a.project_id = @project AND u.canonical_url = @url AND ca.kind = @kind " +
            "ORDER BY ca.audit_id, ca.id";
        AddParameter(command, "@project", projectId);
        AddParameter(command, "@url", canonicalUrl);
        AddParameter(command, "@kind", (long)kind);
        var map = EntityMap<ContentArtifactDto>.Instance;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return map.Materialize(reader);
        }
    }

    /// <summary>Computes total stored (compressed) bytes for user-visible storage reporting.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total compressed artifact bytes on disk.</returns>
    public async Task<long> GetTotalStoredBytesAsync(CancellationToken cancellationToken = default)
    {
        long total = 0;
        foreach (var key in await ListStorageKeysAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += new FileInfo(ResolvePath(key)).Length;
        }

        return total;
    }

    private async Task<(string StorageKey, long CompressedSize, string ContentHash, string TempPath)> WriteCompressedAsync(Stream content, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        string hashHex;

        await using (var tempFile = File.Create(tempPath))
        await using (var compressor = new BrotliStream(tempFile, CompressionLevel.Optimal, leaveOpen: true))
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await compressor.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await compressor.FlushAsync(cancellationToken);
            hashHex = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        var storageKey = $"{KeyPrefix}{hashHex[..2]}/{hashHex}";
        var finalPath = ResolvePath(storageKey);
        var compressedSize = File.Exists(finalPath)
            ? new FileInfo(finalPath).Length
            : new FileInfo(tempPath).Length;
        return (storageKey, compressedSize, hashHex, tempPath);
    }

    private async Task DeleteIfUnreferencedAsync(string storageKey, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM content_artifacts WHERE storage_key = @key";
        AddParameter(command, "@key", storageKey);
        var references = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        if (references == 0)
        {
            await DeleteCompressedAsync(storageKey, cancellationToken);
        }
    }

    private string ResolvePath(string storageKey)
    {
        if (!StorageKeyPattern.IsMatch(storageKey))
        {
            throw new ArgumentException($"Invalid content-store storage key '{storageKey}'.", nameof(storageKey));
        }

        return Path.Combine(_rootDirectory, storageKey.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
