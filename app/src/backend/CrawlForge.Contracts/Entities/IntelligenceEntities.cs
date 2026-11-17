using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Entities;

/// <summary>
/// Additive table (FS-CRAWL-002 §4.4): reusable project-level include/exclude
/// content scope.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="ProjectId">Owning project reference.</param>
/// <param name="Name">Profile name.</param>
/// <param name="IncludeSelectorsJson">Include selectors payload.</param>
/// <param name="ExcludeSelectorsJson">Exclude selectors payload.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="UpdatedAt">Last-update timestamp (UTC).</param>
public sealed record ContentScopeProfileDto(
    long Id,
    long ProjectId,
    string Name,
    string IncludeSelectorsJson,
    string ExcludeSelectorsJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Additive table (FS-CRAWL-002 §5.6): per-URL, per-zone, per-order n-gram
/// counts and term frequency. Unique per (audit, URL, zone, order, term).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="UrlId">Owning URL reference.</param>
/// <param name="Zone">Text zone the n-gram was observed in.</param>
/// <param name="Order">N-gram order (1, 2, 3).</param>
/// <param name="Term">N-gram term.</param>
/// <param name="Count">Occurrences on the URL.</param>
/// <param name="TermFrequency">Term frequency within the zone.</param>
public sealed record NgramFrequencyDto(
    long Id,
    long AuditId,
    long UrlId,
    TextZone Zone,
    int Order,
    string Term,
    long Count,
    double TermFrequency);

/// <summary>
/// Additive aggregate view / materialized read model (FS-CRAWL-002 §5.6):
/// site-wide document frequency, total frequency, mean TF, and primary-term
/// counts per (audit, zone, order, term).
/// </summary>
/// <param name="Id">Surrogate key of the read-model row.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="Zone">Text zone.</param>
/// <param name="Order">N-gram order (1, 2, 3).</param>
/// <param name="Term">N-gram term.</param>
/// <param name="DocumentFrequency">Number of URLs containing the term.</param>
/// <param name="TotalFrequency">Total occurrences across the site.</param>
/// <param name="MeanTermFrequency">Mean term frequency across URLs.</param>
/// <param name="PrimaryTermCount">URLs where the term is a primary term.</param>
public sealed record NgramSiteSummaryDto(
    long Id,
    long AuditId,
    TextZone Zone,
    int Order,
    string Term,
    long DocumentFrequency,
    long TotalFrequency,
    double MeanTermFrequency,
    long PrimaryTermCount);

/// <summary>
/// Additive table (FS-CRAWL-002 §5.6): per-language user stop-word additions.
/// Bundled stop-word lists are packaged assets (exclusion E2).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="Language">Language tag.</param>
/// <param name="Word">User stop word.</param>
/// <param name="AddedAt">Addition timestamp (UTC).</param>
public sealed record UserStopwordDto(
    long Id,
    string Language,
    string Word,
    DateTimeOffset AddedAt);

/// <summary>
/// Additive table (FS-CRAWL-002 §6.7): local plugin registry with grants and
/// integrity hashes. Plugins are local user-authored artifacts only
/// (exclusion E1); grants are read-only data + UI contribution (exclusion E5);
/// no licensing, accounts, or feature gating exists (exclusion E6).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="Name">Plugin name.</param>
/// <param name="DirectoryPath">Local plugin directory path.</param>
/// <param name="Version">Plugin version.</param>
/// <param name="IsEnabled">Whether the plugin is enabled.</param>
/// <param name="Grants">Granted permissions (read-only by construction).</param>
/// <param name="IntegrityHash">Integrity hash of the plugin artifact.</param>
/// <param name="LoadState">Current load state, when loaded.</param>
/// <param name="RegisteredAt">Registration timestamp (UTC).</param>
public sealed record UiPluginDto(
    long Id,
    string Name,
    string DirectoryPath,
    string Version,
    bool IsEnabled,
    PluginPermission Grants,
    string IntegrityHash,
    string? LoadState,
    DateTimeOffset RegisteredAt);

/// <summary>
/// Additive table (FS-CRAWL-002 §10.6): migration matrix row
/// (Source_URL, Expected_Target_URL) per audit. Unique per (audit, source URL).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="SourceUrl">Source URL of the migration row.</param>
/// <param name="ExpectedTargetUrl">Expected target URL after migration.</param>
/// <param name="Ordinal">Row ordinal within the matrix.</param>
public sealed record RedirectMappingDto(
    long Id,
    long AuditId,
    string SourceUrl,
    string ExpectedTargetUrl,
    int Ordinal);

/// <summary>
/// Additive table (FS-CRAWL-002 §11.6): log import record with format, window,
/// counters, verification mode, and status. Ingestion reads local disk only
/// (exclusion E8).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="ProjectId">Owning project reference.</param>
/// <param name="Format">Log file format.</param>
/// <param name="WindowStart">Log window start (UTC).</param>
/// <param name="WindowEnd">Log window end (UTC).</param>
/// <param name="CountersJson">Import counters payload.</param>
/// <param name="VerificationMode">Bot verification mode of the import.</param>
/// <param name="Status">Import status.</param>
/// <param name="ImportedAt">Import timestamp (UTC).</param>
/// <param name="SourceFilePath">Local source file path.</param>
public sealed record LogImportDto(
    long Id,
    long ProjectId,
    LogFileFormat Format,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    string CountersJson,
    BotVerificationMode VerificationMode,
    LogImportStatus Status,
    DateTimeOffset ImportedAt,
    string? SourceFilePath);

/// <summary>
/// Additive table (FS-CRAWL-002 §11.6): per-(import, canonical URL) hit
/// aggregates by bot family and status. Unique per (import, canonical URL).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="LogImportId">Owning log import reference.</param>
/// <param name="CanonicalUrl">Canonical URL of the aggregated hits.</param>
/// <param name="Host">Host of the URL.</param>
/// <param name="TotalHits">Total hits in the window.</param>
/// <param name="SearchBotHits">Hits attributed to search bots.</param>
/// <param name="BotFamilyBreakdownJson">Per-bot-family hit breakdown payload.</param>
/// <param name="StatusBreakdownJson">Per-status hit breakdown payload.</param>
public sealed record LogUrlAggregateDto(
    long Id,
    long LogImportId,
    string CanonicalUrl,
    string Host,
    long TotalHits,
    long SearchBotHits,
    string? BotFamilyBreakdownJson,
    string? StatusBreakdownJson);

/// <summary>
/// Additive table (FS-CRAWL-002 §12.7): bounded top-K semantic link
/// recommendation with anchor candidates. Unique per (audit, source, target).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="SourceUrlId">Source URL reference.</param>
/// <param name="TargetUrlId">Recommended target URL reference.</param>
/// <param name="Similarity">Cosine similarity (0–1).</param>
/// <param name="AnchorCandidatesJson">Anchor text candidates payload.</param>
public sealed record SemanticLinkOpportunityDto(
    long Id,
    long AuditId,
    long SourceUrlId,
    long TargetUrlId,
    double Similarity,
    string? AnchorCandidatesJson);

/// <summary>
/// Additive table (FS-CRAWL-002 §12.7): semantic cannibalization cluster
/// membership and centroid similarity.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="ClusterId">Cluster identifier within the audit.</param>
/// <param name="UrlId">Member URL reference.</param>
/// <param name="CentroidSimilarity">Similarity to the cluster centroid (0–1).</param>
public sealed record SemanticClusterDto(
    long Id,
    long AuditId,
    long ClusterId,
    long UrlId,
    double CentroidSimilarity);

/// <summary>
/// Additive table (FS-CRAWL-002 §13.4): agent × scope × purpose allow/deny
/// verdict with evidence.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="AgentName">Agent name from the bundled profile catalogue.</param>
/// <param name="ScopeValue">Robots scope value the verdict applies to.</param>
/// <param name="Purpose">Agent purpose dimension.</param>
/// <param name="Verdict">Allow/deny verdict.</param>
/// <param name="EvidenceJson">Structured evidence payload.</param>
public sealed record GovernanceMatrixRowDto(
    long Id,
    long AuditId,
    string AgentName,
    string ScopeValue,
    AiAgentPurpose Purpose,
    GovernanceVerdict Verdict,
    string EvidenceJson);

/// <summary>
/// Additive table (FS-CRAWL-002 §14.4): per-URL tag/container detection with
/// vendor, identifier, source, and placement.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="UrlId">Owning URL reference.</param>
/// <param name="Vendor">Vendor label.</param>
/// <param name="ContainerId">Container identifier observed, when present.</param>
/// <param name="DetectionSource">Detection source (dom, proxy).</param>
/// <param name="Placement">Placement detail, when established.</param>
public sealed record AnalyticsDetectionDto(
    long Id,
    long AuditId,
    long UrlId,
    string Vendor,
    string? ContainerId,
    string DetectionSource,
    string? Placement);
