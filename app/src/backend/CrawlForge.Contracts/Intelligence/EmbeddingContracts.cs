using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Intelligence;

/// <summary>
/// Local embedding provider (FS-CRAWL-002 §12, exclusion E7): embeddings come
/// only from the bundled offline ONNX model or a user-configured loopback-only
/// daemon. Non-loopback endpoints are rejected at configuration; no remote
/// embedding/vector/LLM API is ever consulted.
/// </summary>
public interface ILocalEmbeddingProvider
{
    /// <summary>Model tag recorded with every stored embedding.</summary>
    string ModelTag { get; }

    /// <summary>Dimensionality of produced vectors.</summary>
    int Dimensions { get; }

    /// <summary>Embeds text into a local vector.</summary>
    /// <param name="text">Input text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The embedding vector.</returns>
    Task<EmbeddingVector> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Tests provider reachability and model availability (loopback only).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection/model test verdict.</returns>
    Task<EmbeddingProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One embedding vector with its model tag.
/// </summary>
/// <param name="Values">Vector components.</param>
/// <param name="ModelTag">Model that produced the vector.</param>
public sealed record EmbeddingVector(ReadOnlyMemory<float> Values, string ModelTag);

/// <summary>
/// Embedding provider connection test result
/// (REST: POST /api/settings/embedding/test).
/// </summary>
/// <param name="IsReachable">Whether the provider endpoint is reachable.</param>
/// <param name="ModelAvailable">Whether the configured model is available.</param>
/// <param name="Message">Human-readable verdict detail.</param>
public sealed record EmbeddingProviderTestResult(bool IsReachable, bool ModelAvailable, string? Message);

/// <summary>
/// Embedding provider configuration (additive application-settings field).
/// The loopback endpoint is validated to reject any non-loopback address
/// (exclusion E7).
/// </summary>
/// <param name="Kind">Provider kind.</param>
/// <param name="LoopbackEndpoint">Loopback daemon endpoint; null for the bundled model.</param>
/// <param name="ModelTag">Model tag override; null uses the provider default.</param>
public sealed record EmbeddingProviderSettings(
    EmbeddingProviderKind Kind,
    string? LoopbackEndpoint,
    string? ModelTag);

/// <summary>
/// Semantic internal-linking analyzer (FS-CRAWL-002 §12): activates the
/// dormant url_embeddings table and produces bounded top-K link
/// recommendations and cannibalization clusters.
/// </summary>
public interface ISemanticLinkAnalyzer
{
    /// <summary>Runs the local embedding job for an audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="options">Similarity threshold and top-K bounds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Opportunities and clusters.</returns>
    Task<SemanticAnalysisResult> RunAsync(long auditId, SemanticAnalysisOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Semantic analysis options.
/// </summary>
/// <param name="SimilarityThreshold">Minimum cosine similarity to report (0–1).</param>
/// <param name="TopK">Bound on recommendations per source URL.</param>
public sealed record SemanticAnalysisOptions(double SimilarityThreshold, int TopK);

/// <summary>
/// Semantic analysis result.
/// </summary>
/// <param name="Opportunities">Bounded top-K semantic link recommendations.</param>
/// <param name="Clusters">Semantic cannibalization clusters.</param>
public sealed record SemanticAnalysisResult(
    IReadOnlyList<SemanticLinkOpportunityDto> Opportunities,
    IReadOnlyList<SemanticClusterDto> Clusters);
