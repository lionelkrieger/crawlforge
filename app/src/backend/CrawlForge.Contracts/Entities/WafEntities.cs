using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Entities;

/// <summary>
/// Additive table (FS-CRAWL-002 §2.6): per-URL WAF challenge detection with
/// provider, class, and evidence. Unique per (audit, URL).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="UrlId">Owning URL reference.</param>
/// <param name="Provider">Attributed provider label.</param>
/// <param name="ChallengeClass">Challenge page class.</param>
/// <param name="EvidenceJson">Structured evidence payload.</param>
/// <param name="SignatureKey">Matched catalogue signature key, when any.</param>
/// <param name="ResolvedByRendering">Whether rendering resolved the challenge.</param>
/// <param name="DetectedAt">Detection timestamp (UTC).</param>
public sealed record WafDetectionDto(
    long Id,
    long AuditId,
    long UrlId,
    WafProvider Provider,
    WafChallengeClass ChallengeClass,
    string EvidenceJson,
    string? SignatureKey,
    bool ResolvedByRendering,
    DateTimeOffset DetectedAt);

/// <summary>
/// Additive table (FS-CRAWL-002 §2.6): user-local signature catalogue entry.
/// Bundled entries are packaged assets; user additions are local only
/// (exclusion E2).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="Key">Stable signature key.</param>
/// <param name="Provider">Provider label the signature attributes to.</param>
/// <param name="ChallengeClass">Challenge class the signature indicates.</param>
/// <param name="HeaderMarkersJson">Header marker patterns payload.</param>
/// <param name="BodyMarkersJson">Body marker patterns payload.</param>
/// <param name="IsEnabled">Whether the user entry is enabled.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="UpdatedAt">Last-update timestamp (UTC).</param>
public sealed record WafUserSignatureDto(
    long Id,
    string Key,
    WafProvider Provider,
    WafChallengeClass ChallengeClass,
    string HeaderMarkersJson,
    string BodyMarkersJson,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Additive table (FS-CRAWL-002 §3.6): bounded AIMD adjustment history per
/// (audit, host).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="Host">Host the adjustment applies to.</param>
/// <param name="PreviousRate">Rate before the adjustment.</param>
/// <param name="NewRate">Rate after the adjustment.</param>
/// <param name="Reason">Adjustment reason.</param>
/// <param name="Timestamp">Adjustment timestamp (UTC).</param>
public sealed record HostThrottleEventDto(
    long Id,
    long AuditId,
    string Host,
    double PreviousRate,
    double NewRate,
    string Reason,
    DateTimeOffset Timestamp);
