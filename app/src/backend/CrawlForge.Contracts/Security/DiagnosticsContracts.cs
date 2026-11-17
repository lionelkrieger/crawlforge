using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Security;

/// <summary>
/// Local diagnostic artifact handler (FS-CRAWL-001 §10.5/§12): local structured
/// logs and locally generated native minidumps. Artifacts are produced and
/// stored only on the local filesystem, never transmitted automatically, and
/// are user-viewable and purgeable (exclusion row 6). No identity dimension is
/// ever attached (privacy invariant).
/// </summary>
public interface ICrashDumpHandler
{
    /// <summary>Captures a native minidump of the current process locally.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Artifact metadata, or null when capture was not possible.</returns>
    Task<DiagnosticArtifactDto?> CaptureMinidumpAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists local diagnostic artifacts, optionally filtered by kind.</summary>
    /// <param name="kind">Optional kind filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Artifact metadata rows.</returns>
    Task<IReadOnlyList<DiagnosticArtifactDto>> ListArtifactsAsync(DiagnosticArtifactKind? kind, CancellationToken cancellationToken = default);

    /// <summary>Purges local diagnostic artifacts, optionally filtered by kind.</summary>
    /// <param name="kind">Optional kind filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of artifacts purged.</returns>
    Task<int> PurgeArtifactsAsync(DiagnosticArtifactKind? kind, CancellationToken cancellationToken = default);
}

/// <summary>
/// Metadata of one local diagnostic artifact
/// (REST: GET /api/diagnostics/artifacts).
/// </summary>
/// <param name="Id">Artifact identifier.</param>
/// <param name="Kind">Artifact kind.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="SizeBytes">Artifact size in bytes.</param>
/// <param name="FilePath">Local file path.</param>
public sealed record DiagnosticArtifactDto(
    long Id,
    DiagnosticArtifactKind Kind,
    DateTimeOffset CreatedAt,
    long SizeBytes,
    string FilePath);
