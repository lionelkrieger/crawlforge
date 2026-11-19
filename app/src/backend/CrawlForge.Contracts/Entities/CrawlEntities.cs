using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Entities;

/// <summary>
/// Per-URL crawl facts (schema catalog: url_records). Unique per
/// (audit, canonical URL). Additive block-reason/WAF fields carry the
/// waf-challenge sub-classification of the Blocked status class
/// (FS-CRAWL-002 §2.6).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="CanonicalUrl">Canonical URL (unique within the audit).</param>
/// <param name="Host">Host of the URL.</param>
/// <param name="Path">Path of the URL.</param>
/// <param name="Depth">Link depth from the seeds.</param>
/// <param name="DiscoverySource">How the URL was discovered.</param>
/// <param name="StatusClass">Crawl outcome class.</param>
/// <param name="FinalStatusCode">Final HTTP status code, when fetched.</param>
/// <param name="RedirectTargetUrl">Redirect target, when redirected.</param>
/// <param name="ContentType">Response content type, when fetched.</param>
/// <param name="SizeBytes">Response size in bytes, when fetched.</param>
/// <param name="TimingJson">Timing payload (TTFB, total duration).</param>
/// <param name="RobotsState">Robots evaluation state.</param>
/// <param name="IndexabilityFlags">Observed indexability flags.</param>
/// <param name="IsIndexable">Computed indexability verdict.</param>
public sealed record UrlRecordDto(
    long Id,
    long AuditId,
    string CanonicalUrl,
    string Host,
    string Path,
    int Depth,
    DiscoverySource DiscoverySource,
    CrawlStatusClass StatusClass,
    int? FinalStatusCode,
    string? RedirectTargetUrl,
    string? ContentType,
    long? SizeBytes,
    string? TimingJson,
    RobotsState RobotsState,
    IndexabilityFlags IndexabilityFlags,
    bool IsIndexable)
{
    /// <summary>Additive: block reason for blocked URLs (FS-CRAWL-002 §2.6).</summary>
    [AdditiveMember]
    public string? BlockReason { get; init; }

    /// <summary>Additive: attributed WAF provider (FS-CRAWL-002 §2.6).</summary>
    [AdditiveMember]
    public WafProvider? WafProvider { get; init; }

    /// <summary>Additive: challenge class (FS-CRAWL-002 §2.6).</summary>
    [AdditiveMember]
    public WafChallengeClass? ChallengeClass { get; init; }

    /// <summary>Additive (WP-10-A1, D-021): which capture produced the analysed
    /// document (FS-CRAWL-001 §5.6.1: raw, rendered, or both). Null means the
    /// baseline raw (non-JS) capture. Wire field: captureSource.</summary>
    [AdditiveMember]
    public CaptureSource? CaptureSource { get; init; }
}

/// <summary>
/// Extracted DOM inventory row (schema catalog: url_elements): one row per
/// extracted element group instance. New element groups arrive additively
/// (see Constants.ElementGroups).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="UrlId">Owning URL reference.</param>
/// <param name="ElementGroup">Element group name.</param>
/// <param name="PayloadJson">Structured payload of the group instance.</param>
/// <param name="Ordinal">Zero-based instance ordinal within the group.</param>
public sealed record UrlElementDto(
    long Id,
    long UrlId,
    string ElementGroup,
    string PayloadJson,
    int Ordinal);

/// <summary>
/// Directed link graph edge (schema catalog: link_edges). Internal edges
/// reference URL records on both ends; external targets carry a nullable
/// target reference. Supports orphan and graph analysis.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="SourceUrlId">Source URL reference.</param>
/// <param name="CanonicalTargetUrl">Canonical target URL.</param>
/// <param name="TargetUrlId">Target URL reference; null for external targets.</param>
/// <param name="AnchorText">Anchor text, when present.</param>
/// <param name="RelationFlags">Observed relation flags.</param>
/// <param name="IsInternal">Whether the target is internal to the crawl scope.</param>
public sealed record LinkEdgeDto(
    long Id,
    long AuditId,
    long SourceUrlId,
    string CanonicalTargetUrl,
    long? TargetUrlId,
    string? AnchorText,
    LinkRelationFlags RelationFlags,
    bool IsInternal)
{
    /// <summary>Additive: six-zone DOM classification (FS-CRAWL-002 §15.5).</summary>
    [AdditiveMember]
    public DomZone? DomZone { get; init; }

    /// <summary>Additive: zone classification evidence (FS-CRAWL-002 §15.5).</summary>
    [AdditiveMember]
    public string? ZoneEvidence { get; init; }

    /// <summary>Additive: weighted link equity (FS-CRAWL-002 §15).</summary>
    [AdditiveMember]
    public double? WeightedEquity { get; init; }
}

/// <summary>
/// Resume state (schema catalog: crawl_checkpoints). Written on pause and
/// periodically during runs.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="SerializedFrontier">Serialized frontier payload.</param>
/// <param name="SeenSetReference">Reference to the persisted seen-set.</param>
/// <param name="CountersJson">Counters payload at checkpoint time.</param>
/// <param name="SavedAt">Save timestamp (UTC).</param>
public sealed record CrawlCheckpointDto(
    long Id,
    long AuditId,
    string SerializedFrontier,
    string? SeenSetReference,
    string CountersJson,
    DateTimeOffset SavedAt)
{
    /// <summary>Additive: serialized per-host AIMD controller state (FS-CRAWL-002 §3.6).</summary>
    [AdditiveMember]
    public string? AimdStateJson { get; init; }
}

/// <summary>
/// Content-store index row (schema catalog: content_artifacts). Points into
/// the compressed content store.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="UrlId">Owning URL reference.</param>
/// <param name="Kind">Artifact kind.</param>
/// <param name="StorageKey">Content-store storage key.</param>
/// <param name="SizeBytes">Stored size in bytes.</param>
/// <param name="ContentHash">Content hash for integrity.</param>
public sealed record ContentArtifactDto(
    long Id,
    long AuditId,
    long UrlId,
    ContentArtifactKind Kind,
    string StorageKey,
    long SizeBytes,
    string ContentHash);

/// <summary>
/// Similarity artifact (schema catalog: url_embeddings, optional). Activated
/// and populated by the local embedding pipeline without schema change
/// (FS-CRAWL-002 §12.7).
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="AuditId">Owning audit reference.</param>
/// <param name="UrlId">Owning URL reference.</param>
/// <param name="EmbeddingPayloadJson">Serialized embedding vector payload.</param>
/// <param name="ModelTag">Model that produced the embedding.</param>
public sealed record UrlEmbeddingDto(
    long Id,
    long AuditId,
    long UrlId,
    string EmbeddingPayloadJson,
    string ModelTag);
