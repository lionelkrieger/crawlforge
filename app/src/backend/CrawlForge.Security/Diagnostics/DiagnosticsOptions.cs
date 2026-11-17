namespace CrawlForge.Security.Diagnostics;

/// <summary>
/// Local diagnostics storage options (FS-CRAWL-001 §10.5): artifacts live only
/// on the local filesystem, are user-viewable and purgeable, and are bounded in
/// count and total size with oldest-first reclamation. Nothing is ever
/// transmitted automatically (exclusion row 6).
/// </summary>
public sealed class DiagnosticsOptions
{
    /// <summary>Default maximum number of retained diagnostic artifacts.</summary>
    public const int DefaultMaxArtifactCount = 20;

    /// <summary>Default maximum total artifact size in bytes (128 MiB).</summary>
    public const long DefaultMaxTotalBytes = 128L * 1024 * 1024;

    /// <summary>Creates diagnostics options rooted at a local directory.</summary>
    /// <param name="rootDirectory">
    /// Local diagnostics root. When null or whitespace, defaults to the
    /// <c>diagnostics</c> folder inside the per-user CrawlForge data directory.
    /// </param>
    /// <param name="maxArtifactCount">Maximum retained artifact count (at least 1).</param>
    /// <param name="maxTotalBytes">Maximum retained total bytes (at least 1).</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is less than 1.</exception>
    public DiagnosticsOptions(
        string? rootDirectory = null,
        int maxArtifactCount = DefaultMaxArtifactCount,
        long maxTotalBytes = DefaultMaxTotalBytes)
    {
        if (maxArtifactCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxArtifactCount), maxArtifactCount, "At least one artifact must be retainable.");
        }

        if (maxTotalBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), maxTotalBytes, "The total-size bound must be positive.");
        }

        RootDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(CrawlForgePaths.GetPerUserDataDirectory(), "diagnostics")
            : rootDirectory);
        MaxArtifactCount = maxArtifactCount;
        MaxTotalBytes = maxTotalBytes;
    }

    /// <summary>Absolute local diagnostics root directory.</summary>
    public string RootDirectory { get; }

    /// <summary>Local directory holding structured log files.</summary>
    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>Local directory holding native minidump files.</summary>
    public string MinidumpDirectory => Path.Combine(RootDirectory, "minidumps");

    /// <summary>Maximum number of retained artifacts; oldest are reclaimed first.</summary>
    public int MaxArtifactCount { get; }

    /// <summary>Maximum total retained bytes; oldest artifacts are reclaimed first.</summary>
    public long MaxTotalBytes { get; }
}