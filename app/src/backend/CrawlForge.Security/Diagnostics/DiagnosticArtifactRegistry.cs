using System.Text.Json;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Security;

namespace CrawlForge.Security.Diagnostics;

/// <summary>
/// Local registry of diagnostic artifacts (FS-CRAWL-001 §10.5): structured log
/// files and native minidumps stored only on the local filesystem. Provides the
/// user-viewable listing and purge capability consumed by the settings surface
/// and the diagnostic-artifacts endpoint (<c>GET /api/diagnostics/artifacts</c>,
/// <see cref="ICrashDumpHandler"/>). Artifacts are never transmitted
/// automatically (exclusion row 6); no identity dimension is recorded (privacy
/// invariant).
/// </summary>
/// <remarks>
/// <para>
/// Metadata persists in <c>artifacts.json</c> under the diagnostics root with
/// monotonically increasing surrogate identifiers. Retention is bounded by
/// <see cref="DiagnosticsOptions.MaxArtifactCount"/> and
/// <see cref="DiagnosticsOptions.MaxTotalBytes"/>; the oldest artifacts are
/// reclaimed first (spec §10.5). A corrupt registry file recovers to an empty
/// registry; listing self-heals by dropping entries whose files vanished and
/// refreshing sizes from disk.
/// </para>
/// <para>
/// Intended as a process-wide singleton shared by the crash-dump handler and
/// the structured log sink. All operations are serialized and thread-safe.
/// </para>
/// </remarks>
public sealed class DiagnosticArtifactRegistry
{
    private const string RegistryFileName = "artifacts.json";

    private static readonly JsonSerializerOptions RegistryOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DiagnosticsOptions _options;
    private readonly string _registryFilePath;
    private RegistryFile _registry;

    /// <summary>Creates (or reopens) the registry under the given options.</summary>
    /// <param name="options">Diagnostics storage options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public DiagnosticArtifactRegistry(DiagnosticsOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _registryFilePath = Path.Combine(options.RootDirectory, RegistryFileName);

        Directory.CreateDirectory(options.RootDirectory);
        Directory.CreateDirectory(options.LogDirectory);
        Directory.CreateDirectory(options.MinidumpDirectory);

        _registry = LoadRegistry();
    }

    /// <summary>Absolute local diagnostics root directory.</summary>
    public string RootDirectory => _options.RootDirectory;

    /// <summary>Local directory holding structured log files.</summary>
    public string LogDirectory => _options.LogDirectory;

    /// <summary>Local directory holding native minidump files.</summary>
    public string MinidumpDirectory => _options.MinidumpDirectory;

    /// <summary>
    /// Registers an existing local file as a diagnostic artifact and enforces
    /// retention bounds (oldest-first reclamation).
    /// </summary>
    /// <param name="filePath">Local artifact file; must exist.</param>
    /// <param name="kind">Artifact kind.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Metadata of the registered artifact.</returns>
    /// <exception cref="ArgumentException">The path is null, empty, or whitespace.</exception>
    /// <exception cref="FileNotFoundException">The artifact file does not exist.</exception>
    public async Task<DiagnosticArtifactDto> RegisterAsync(
        string filePath,
        DiagnosticArtifactKind kind,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Artifact path must be a non-empty local path.", nameof(filePath));
        }

        string fullPath = Path.GetFullPath(filePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Diagnostic artifact file does not exist.", fullPath);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = new RegistryEntry
            {
                Id = _registry.NextId++,
                Kind = kind,
                CreatedAtUtc = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                SizeBytes = info.Length,
                FilePath = fullPath,
            };
            _registry.Entries.Add(entry);
            EnforceRetention();
            Persist();
            return ToDto(entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Lists local diagnostic artifacts, newest first, optionally filtered by
    /// kind. Entries whose files vanished are dropped; sizes refresh from disk.
    /// </summary>
    /// <param name="kind">Optional kind filter; null lists every kind.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Artifact metadata rows, newest first.</returns>
    public async Task<IReadOnlyList<DiagnosticArtifactDto>> ListAsync(
        DiagnosticArtifactKind? kind,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool changed = false;
            for (int i = _registry.Entries.Count - 1; i >= 0; i--)
            {
                RegistryEntry entry = _registry.Entries[i];
                var info = new FileInfo(entry.FilePath);
                if (!info.Exists)
                {
                    _registry.Entries.RemoveAt(i);
                    changed = true;
                }
                else if (info.Length != entry.SizeBytes)
                {
                    entry.SizeBytes = info.Length;
                    changed = true;
                }
            }

            if (changed)
            {
                Persist();
            }

            return _registry.Entries
                .Where(e => kind is null || e.Kind == kind)
                .OrderByDescending(e => e.CreatedAtUtc)
                .ThenByDescending(e => e.Id)
                .Select(ToDto)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Purges local diagnostic artifacts (files and registry entries), optionally
    /// filtered by kind. This is the user-initiated purge capability exposed to
    /// the settings surface; nothing is purged or transmitted automatically.
    /// </summary>
    /// <param name="kind">Optional kind filter; null purges every kind.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of artifacts purged.</returns>
    public async Task<int> PurgeAsync(
        DiagnosticArtifactKind? kind,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<RegistryEntry> targets = _registry.Entries
                .Where(e => kind is null || e.Kind == kind)
                .ToList();

            foreach (RegistryEntry entry in targets)
            {
                TryDeleteFile(entry.FilePath);
                _registry.Entries.Remove(entry);
            }

            if (targets.Count > 0)
            {
                Persist();
            }

            return targets.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnforceRetention()
    {
        // Oldest-first reclamation (spec §10.5). A single artifact larger than the
        // total-size bound reclaims everything, including itself; callers treat a
        // vanished file as "capture not retained".
        while (_registry.Entries.Count > 0 &&
               (_registry.Entries.Count > _options.MaxArtifactCount ||
                _registry.Entries.Sum(e => e.SizeBytes) > _options.MaxTotalBytes))
        {
            RegistryEntry oldest = _registry.Entries
                .OrderBy(e => e.CreatedAtUtc)
                .ThenBy(e => e.Id)
                .First();
            TryDeleteFile(oldest.FilePath);
            _registry.Entries.Remove(oldest);
        }
    }

    private RegistryFile LoadRegistry()
    {
        try
        {
            if (File.Exists(_registryFilePath))
            {
                RegistryFile? loaded = JsonSerializer.Deserialize<RegistryFile>(
                    File.ReadAllText(_registryFilePath), RegistryOptions);
                if (loaded is not null && loaded.NextId >= 1)
                {
                    return loaded;
                }
            }
        }
        catch (JsonException)
        {
            // Corrupt registry: recover to an empty registry; artifact files remain
            // on disk, user-viewable and purgeable via the diagnostics directory.
        }
        catch (IOException)
        {
            // Unreadable registry: recover to an empty registry.
        }

        return new RegistryFile();
    }

    private void Persist()
    {
        string tempPath = _registryFilePath + "." + Path.GetRandomFileName() + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_registry, RegistryOptions));
        File.Move(tempPath, _registryFilePath, overwrite: true);
    }

    private static DiagnosticArtifactDto ToDto(RegistryEntry entry) =>
        new(entry.Id, entry.Kind, entry.CreatedAtUtc, entry.SizeBytes, entry.FilePath);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort file removal; the registry entry is still purged.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort file removal; the registry entry is still purged.
        }
    }

    private sealed class RegistryFile
    {
        public long NextId { get; set; } = 1;

        public List<RegistryEntry> Entries { get; set; } = new();
    }

    private sealed class RegistryEntry
    {
        public long Id { get; set; }

        public DiagnosticArtifactKind Kind { get; set; }

        public DateTimeOffset CreatedAtUtc { get; set; }

        public long SizeBytes { get; set; }

        public string FilePath { get; set; } = string.Empty;
    }
}