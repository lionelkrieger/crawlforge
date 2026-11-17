namespace CrawlForge.Contracts.Enums;

/// <summary>
/// Server-log file formats supported by the offline log analyzer
/// (FS-CRAWL-002 §11; format names are publicly documented technology vocabulary).
/// </summary>
public enum LogFileFormat
{
    /// <summary>Apache / Nginx Combined log format.</summary>
    ApacheNginxCombined = 0,

    /// <summary>IIS W3C extended log format.</summary>
    IisW3c = 1,

    /// <summary>Cloudflare log export format.</summary>
    CloudflareExport = 2,

    /// <summary>User-described custom format.</summary>
    Custom = 3,
}

/// <summary>
/// Processing status of a log import (schema catalog: log_imports.status).
/// </summary>
public enum LogImportStatus
{
    /// <summary>Import accepted but not started.</summary>
    Pending = 0,

    /// <summary>Streaming import in progress.</summary>
    Importing = 1,

    /// <summary>Import and aggregation finished.</summary>
    Completed = 2,

    /// <summary>Import failed.</summary>
    Failed = 3,
}

/// <summary>
/// Bot verification mode of a log import (schema catalog: log_imports.verification
/// mode). CIDR ranges and agent profiles are bundled assets (exclusion E8); DNS
/// verification is the only optional network action — user-triggered, off by
/// default, per-import consent, locally logged.
/// </summary>
public enum BotVerificationMode
{
    /// <summary>Bundled CIDR + agent-profile classification only; zero outbound traffic.</summary>
    CidrOnly = 0,

    /// <summary>Bundled CIDR classification plus user-consented DNS verification of bot hits.</summary>
    CidrAndDns = 1,
}

/// <summary>
/// Purpose dimension of the AI-agent governance matrix
/// (FS-CRAWL-002 §13: search vs RAG vs training).
/// </summary>
public enum AiAgentPurpose
{
    /// <summary>Agent fetches content for search-index inclusion.</summary>
    Search = 0,

    /// <summary>Agent fetches content for retrieval-augmented generation.</summary>
    Rag = 1,

    /// <summary>Agent fetches content for model training.</summary>
    Training = 2,
}

/// <summary>
/// Allow/deny verdict of a governance-matrix cell
/// (schema catalog: governance_matrix).
/// </summary>
public enum GovernanceVerdict
{
    /// <summary>No explicit directive observed for this agent/scope/purpose.</summary>
    Unspecified = 0,

    /// <summary>Access is allowed for this agent/scope/purpose.</summary>
    Allow = 1,

    /// <summary>Access is denied for this agent/scope/purpose.</summary>
    Deny = 2,
}

/// <summary>
/// Local embedding provider kinds (exclusion E7): the bundled offline ONNX model
/// or a user-configured loopback-only daemon. Non-loopback endpoints are rejected
/// at configuration; remote embedding/vector/LLM APIs are forbidden.
/// </summary>
public enum EmbeddingProviderKind
{
    /// <summary>Bundled offline ONNX model shipped with the product.</summary>
    BundledOnnx = 0,

    /// <summary>User-configured loopback-only embedding daemon.</summary>
    LoopbackDaemon = 1,
}
