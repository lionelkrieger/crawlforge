namespace CrawlForge.Data.Repositories;

/// <summary>
/// File-level access to the compressed artifact store, used by the project archive
/// service so artifact bytes can be exported/imported inside the archive's own
/// transaction boundary (index rows are written by the archive, files by the store).
/// Implemented by the content store; storage keys are content-hash addresses.
/// </summary>
public interface IArtifactFileStore
{
    /// <summary>Checks whether a compressed artifact file exists.</summary>
    /// <param name="storageKey">Content-hash storage key.</param>
    /// <returns>True when the file exists.</returns>
    bool Exists(string storageKey);

    /// <summary>Opens the raw compressed file for reading.</summary>
    /// <param name="storageKey">Content-hash storage key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A readable compressed stream, or null when absent. The caller owns the stream.</returns>
    Task<Stream?> OpenCompressedAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a raw compressed file under its content-hash key (deduplicated: an
    /// existing file is kept untouched).
    /// </summary>
    /// <param name="storageKey">Content-hash storage key.</param>
    /// <param name="compressedContent">The compressed bytes to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a new file was created; false when the key already existed.</returns>
    Task<bool> SaveCompressedAsync(string storageKey, Stream compressedContent, CancellationToken cancellationToken = default);

    /// <summary>Deletes a compressed artifact file.</summary>
    /// <param name="storageKey">Content-hash storage key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the file was deleted.</returns>
    Task<bool> DeleteCompressedAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every storage key present on disk (used by retention to reclaim
    /// orphaned files, including leftovers from interrupted operations).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All storage keys currently stored.</returns>
    Task<IReadOnlyList<string>> ListStorageKeysAsync(CancellationToken cancellationToken = default);
}
