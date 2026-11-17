using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;

namespace CrawlForge.Contracts.Intelligence;

/// <summary>
/// AI-search (GEO) readiness analyzer (FS-CRAWL-002 §13): llms.txt /
/// llms-full.txt discovery and validation per host, plus readiness scoring.
/// All analysis is local; no remote registry or publishing exists (exclusion E9).
/// </summary>
public interface IGeoReadinessAnalyzer
{
    /// <summary>Analyzes GEO readiness of an audit's hosts.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Readiness score, per-host validations, and findings.</returns>
    Task<GeoReadinessResult> AnalyzeAsync(long auditId, CancellationToken cancellationToken = default);

    /// <summary>Validates an llms.txt body against the community convention.</summary>
    /// <param name="body">llms.txt content.</param>
    /// <param name="baseUrl">Optional base URL for link resolution.</param>
    /// <returns>Validation verdict.</returns>
    LlmsTxtValidation ValidateLlmsTxt(string body, string? baseUrl);
}

/// <summary>
/// GEO readiness result.
/// </summary>
/// <param name="ReadinessScore">Readiness score (0–100).</param>
/// <param name="HostResults">Per-host llms.txt validation results.</param>
/// <param name="Findings">GEO findings.</param>
public sealed record GeoReadinessResult(
    double ReadinessScore,
    IReadOnlyList<LlmsTxtValidation> HostResults,
    IReadOnlyList<FindingDraft> Findings);

/// <summary>
/// Validation result of one host's llms.txt.
/// </summary>
/// <param name="Host">Host name.</param>
/// <param name="IsPresent">Whether llms.txt was found.</param>
/// <param name="IsWellFormed">Whether the document follows the convention.</param>
/// <param name="BrokenLinks">Links in the document that do not resolve.</param>
/// <param name="Issues">Human-readable validation issues.</param>
public sealed record LlmsTxtValidation(
    string Host,
    bool IsPresent,
    bool IsWellFormed,
    IReadOnlyList<string> BrokenLinks,
    IReadOnlyList<string> Issues);

/// <summary>
/// AI-bot governance matrix (FS-CRAWL-002 §13): agent × scope × purpose
/// allow/deny verdicts with evidence, built from bundled agent profiles and
/// observed robots directives (schema catalog: governance_matrix).
/// </summary>
public interface IAiBotGovernanceMatrix
{
    /// <summary>Evaluates the governance matrix for an audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matrix rows with verdicts and evidence.</returns>
    Task<IReadOnlyList<GovernanceMatrixRowDto>> EvaluateAsync(long auditId, CancellationToken cancellationToken = default);
}

/// <summary>
/// llms.txt / llms-full.txt generator (FS-CRAWL-002 §13). Generation writes
/// local files only — no remote registry, auto-publishing, or AI-vendor
/// submission (exclusion E9).
/// </summary>
public interface ILlmsTxtGenerator
{
    /// <summary>Generates llms.txt files into a local directory.</summary>
    /// <param name="request">Generation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paths of the locally written files.</returns>
    Task<LlmsGenerationResult> GenerateAsync(LlmsGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// llms.txt generation request.
/// </summary>
/// <param name="AuditId">Audit whose content feeds the generation.</param>
/// <param name="OutputDirectory">Local output directory.</param>
/// <param name="IncludeFullVersion">Whether to also generate llms-full.txt.</param>
public sealed record LlmsGenerationRequest(long AuditId, string OutputDirectory, bool IncludeFullVersion = true);

/// <summary>
/// llms.txt generation result.
/// </summary>
/// <param name="WrittenFilePaths">Local file paths written by the generator.</param>
public sealed record LlmsGenerationResult(IReadOnlyList<string> WrittenFilePaths);
