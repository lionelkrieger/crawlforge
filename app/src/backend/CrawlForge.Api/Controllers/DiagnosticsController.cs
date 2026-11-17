using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Security;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>One local diagnostic artifact on the wire.</summary>
/// <param name="Id">Artifact identifier.</param>
/// <param name="Kind">Artifact kind wire value: log or minidump.</param>
/// <param name="FileName">Local file name.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="SizeBytes">Artifact size in bytes.</param>
public sealed record DiagnosticArtifactWire(string Id, string Kind, string FileName, DateTimeOffset CreatedAt, long SizeBytes);

/// <summary>DELETE /api/diagnostics/artifacts — purge confirmation.</summary>
/// <param name="PurgedCount">Number of artifacts purged.</param>
public sealed record DiagnosticPurgeResponse(int PurgedCount);

/// <summary>
/// Local diagnostic artifacts (FS-CRAWL-001 §10.2/§12): user-viewable and
/// purgeable logs and minidumps, stored only on the local filesystem. No
/// identity dimension is ever attached; nothing is transmitted automatically.
/// </summary>
[Route("api/diagnostics/artifacts")]
public sealed class DiagnosticsController : ApiControllerBase
{
    private readonly ICrashDumpHandler _handler;

    /// <summary>Creates the controller.</summary>
    /// <param name="handler">Local diagnostic artifact handler.</param>
    public DiagnosticsController(ICrashDumpHandler handler) => _handler = handler;

    /// <summary>Lists local diagnostic artifacts with an optional kind filter.</summary>
    /// <param name="kind">Optional kind filter: log or minidump.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Artifact metadata envelope.</returns>
    [HttpGet]
    public async Task<ObjectResult> ListArtifactsAsync([FromQuery] string? kind, CancellationToken cancellationToken)
    {
        if (!TryParseKind(kind, out var parsed, out var error))
        {
            return ValidationError(error!);
        }

        var artifacts = await _handler.ListArtifactsAsync(parsed, cancellationToken);
        var wire = artifacts
            .Select(a => new DiagnosticArtifactWire(
                a.Id.ToString(),
                a.Kind == DiagnosticArtifactKind.Minidump ? "minidump" : "log",
                Path.GetFileName(a.FilePath),
                a.CreatedAt,
                a.SizeBytes))
            .ToList();
        return OkEnvelope(wire);
    }

    /// <summary>Purges local diagnostic artifacts with an optional kind filter.</summary>
    /// <param name="kind">Optional kind filter: log or minidump.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Purge confirmation envelope.</returns>
    [HttpDelete]
    public async Task<ObjectResult> PurgeArtifactsAsync([FromQuery] string? kind, CancellationToken cancellationToken)
    {
        if (!TryParseKind(kind, out var parsed, out var error))
        {
            return ValidationError(error!);
        }

        var purged = await _handler.PurgeArtifactsAsync(parsed, cancellationToken);
        return OkEnvelope(new DiagnosticPurgeResponse(purged));
    }

    private static bool TryParseKind(string? kind, out DiagnosticArtifactKind? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (kind is null)
        {
            return true;
        }

        if (ApiEnumConverter<DiagnosticArtifactKind>.TryParse(kind, out var value))
        {
            parsed = value;
            return true;
        }

        error = $"Unknown diagnostic artifact kind '{kind}'.";
        return false;
    }
}
