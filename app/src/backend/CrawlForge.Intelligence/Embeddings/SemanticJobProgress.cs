namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Local bounds and thresholds for the semantic linking pipeline
/// (FS-CRAWL-002 §12.5–12.6 defaults; §7.9 bound philosophy — excess is
/// discarded and counted, never fatal). These are local configuration, never
/// remote input.
/// </summary>
public sealed record SemanticPipelineOptions
{
    /// <summary>Cosine similarity at/above which URLs form cannibalization clusters (default 0.88).</summary>
    public double CannibalizationThreshold { get; init; } = 0.88;

    /// <summary>Similarity below which an existing internal link is low-relevance (default 0.35).</summary>
    public double RelevanceFloor { get; init; } = 0.35;

    /// <summary>Maximum indexable URLs analyzed per job; excess discarded and counted (default 5000).</summary>
    public int MaxAnalyzedUrls { get; init; } = 5000;

    /// <summary>Bounded concurrent embedding inferences (default 2).</summary>
    public int MaxConcurrency { get; init; } = 2;

    /// <summary>url_embeddings persistence batch size (default 100).</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>Maximum chunk length in characters.</summary>
    public int MaxChunkChars { get; init; } = TextChunker.DefaultMaxChunkChars;

    /// <summary>Chunk overlap in characters.</summary>
    public int OverlapChars { get; init; } = TextChunker.DefaultOverlapChars;

    /// <summary>Maximum anchor candidates per opportunity (from target n-grams).</summary>
    public int MaxAnchorCandidates { get; init; } = 5;

    /// <summary>Maximum low-relevance link findings per job (default 1000).</summary>
    public int MaxLowRelevanceFindings { get; init; } = 1000;

    /// <summary>Maximum retained per-URL failure diagnostics (default 100).</summary>
    public int MaxDiagnostics { get; init; } = 100;
}

/// <summary>
/// Semantic job progress (FS-CRAWL-002 §12.9): the Host relays this to the
/// hub as embedding-progress (at most once per second) and
/// embedding-completed on finalization. Local diagnostics only — no identity
/// dimensions, no transmission beyond the loopback hub.
/// </summary>
/// <param name="AuditId">Audit reference.</param>
/// <param name="EmbeddedCount">URLs embedded so far (reused vectors count).</param>
/// <param name="TotalEligible">Total eligible indexable URLs.</param>
/// <param name="Provider">Active provider model tag.</param>
/// <param name="Phase">Job phase label (embedding, similarity, persistence, completed, unavailable).</param>
/// <param name="IsFinal">True on the final report.</param>
public sealed record SemanticJobProgress(
    long AuditId,
    int EmbeddedCount,
    int TotalEligible,
    string Provider,
    string Phase,
    bool IsFinal = false);
