using System.Security.Cryptography;
using System.Text;

namespace CrawlForge.Crawler.Canonicalization;

/// <summary>
/// Persistent seen-set keyed by canonical URL (FS-CRAWL-001 §5.2): each URL is
/// fetched at most once per audit. Durability is an append-only local file (one
/// canonical URL per line); the in-memory index stores fixed-size 128-bit content
/// hashes, so memory stays bounded at 500k+ URLs (precedence rule 7). Crash
/// recovery rebuilds the index by streaming the file — the file path is the
/// checkpoint's seen-set reference (FS-CRAWL-001 §5.7).
/// </summary>
public sealed class PersistentSeenSet : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _hashes = [];
    private string _filePath;
    private StreamWriter? _writer;

    /// <summary>Opens or creates the seen-set at a local file path.</summary>
    /// <param name="filePath">Local append-only seen-set file.</param>
    public PersistentSeenSet(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        RebuildIndex(_filePath);
        _writer = new StreamWriter(File.Open(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = false
        };
    }

    /// <summary>The checkpoint reference for this seen-set (its local file path).</summary>
    public string Reference
    {
        get
        {
            lock (_gate)
            {
                return _filePath;
            }
        }
    }

    /// <summary>Number of distinct canonical URLs recorded.</summary>
    public long Count
    {
        get
        {
            lock (_gate)
            {
                return _hashes.Count;
            }
        }
    }

    /// <summary>
    /// Records a canonical URL. Returns false when it was already seen (dedup).
    /// </summary>
    /// <param name="canonicalUrl">Canonical URL key.</param>
    /// <returns>True when newly recorded; false when already present.</returns>
    public bool TryAdd(string canonicalUrl)
    {
        lock (_gate)
        {
            if (!_hashes.Add(Hash128(canonicalUrl)))
            {
                return false;
            }

            _writer!.WriteLine(canonicalUrl);
            return true;
        }
    }

    /// <summary>Checks membership without recording.</summary>
    /// <param name="canonicalUrl">Canonical URL key.</param>
    /// <returns>True when already seen.</returns>
    public bool Contains(string canonicalUrl)
    {
        lock (_gate)
        {
            return _hashes.Contains(Hash128(canonicalUrl));
        }
    }

    /// <summary>Flushes buffered entries to disk (called before checkpointing).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            _writer?.Flush();
        }
    }

    /// <summary>
    /// Rehydrates the seen-set from a checkpoint reference during resume
    /// (FS-CRAWL-001 §5.7): streams the referenced file, rebuilds the hash index,
    /// and continues appending to it.
    /// </summary>
    /// <param name="reference">Seen-set file path from the checkpoint.</param>
    public void RebuildFrom(string reference)
    {
        var path = Path.GetFullPath(reference);
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _hashes.Clear();
            RebuildIndex(path);
            _filePath = path;
            _writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = false
            };
        }
    }

    /// <summary>
    /// Fixed-size 128-bit key derived from SHA-256 of the canonical URL. Collision
    /// probability at 500k URLs is negligible (~1e-28), keeping the index bounded.
    /// </summary>
    /// <param name="canonicalUrl">Canonical URL key.</param>
    /// <returns>The 128-bit hash as a GUID.</returns>
    public static Guid Hash128(string canonicalUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalUrl));
        return new Guid(hash.AsSpan(0, 16));
    }

    private void RebuildIndex(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var reader = new StreamReader(File.OpenRead(path));
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                _hashes.Add(Hash128(line));
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
