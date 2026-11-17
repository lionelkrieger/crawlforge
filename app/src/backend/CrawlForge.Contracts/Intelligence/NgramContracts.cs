using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Intelligence;

/// <summary>
/// Site-wide n-gram and topical-density analyzer (FS-CRAWL-002 §5):
/// per-language tokenization and stop-word filtering, four-zone 1/2/3-gram
/// TF/DF analysis, cannibalization and topical-gap detection. Enabled by
/// default within mandatory bounds; stop-word lists are bundled assets
/// (exclusion E2).
/// </summary>
public interface INgramAnalyzer
{
    /// <summary>Analyzes one URL's zone texts into per-zone n-gram frequencies.</summary>
    /// <param name="input">URL text input per zone.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>N-gram frequency rows.</returns>
    Task<IReadOnlyList<NgramFrequencyDto>> AnalyzeUrlAsync(NgramUrlInput input, CancellationToken cancellationToken = default);

    /// <summary>Aggregates site-wide n-gram summaries for an audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Site summary rows (materialized read model).</returns>
    Task<IReadOnlyList<NgramSiteSummaryDto>> SummarizeSiteAsync(long auditId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-URL n-gram analysis input.
/// </summary>
/// <param name="AuditId">Audit identifier.</param>
/// <param name="UrlId">URL record identifier.</param>
/// <param name="Language">Language tag selecting tokenization and stop-words.</param>
/// <param name="ZoneTexts">Visible text per zone.</param>
/// <param name="Orders">N-gram orders to compute (1, 2, 3).</param>
public sealed record NgramUrlInput(
    long AuditId,
    long UrlId,
    string Language,
    IReadOnlyDictionary<TextZone, string> ZoneTexts,
    IReadOnlyList<int> Orders);

/// <summary>
/// Stop-word filter (FS-CRAWL-002 §5.2): removes stop words using bundled
/// per-language lists plus local user additions (schema catalog: user_stopwords).
/// </summary>
public interface IStopWordFilter
{
    /// <summary>Removes stop words from a token sequence.</summary>
    /// <param name="tokens">Tokenized terms.</param>
    /// <param name="language">Language tag selecting the stop-word list.</param>
    /// <returns>Tokens with stop words removed, order preserved.</returns>
    IReadOnlyList<string> RemoveStopWords(IReadOnlyList<string> tokens, string language);
}
