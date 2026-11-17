namespace CrawlForge.Audit.Indexability;

/// <summary>
/// Soft-404 detection configuration (FS-CRAWL-001 §7.12): a configurable
/// error-like phrase list plus structural heuristics. Defaults are bundled
/// locally; user configuration is local-only (no remote catalogue, E2).
/// </summary>
public sealed record Soft404Options
{
    /// <summary>
    /// Error-like phrases matched case-insensitively against the title and
    /// visible text. Defaults cover common error-page vocabulary.
    /// </summary>
    public IReadOnlyList<string> ErrorPhrases { get; init; } = new[]
    {
        "page not found",
        "404 not found",
        "error 404",
        "404 error",
        "not found",
        "page doesn't exist",
        "page does not exist",
        "no longer available",
        "no longer exists",
        "we couldn't find",
        "we could not find",
        "sorry, we can't find",
        "content not found",
        "page unavailable",
    };

    /// <summary>
    /// Minimum confidence (0–1) for a positive soft-404 classification.
    /// Defaults to 0.6.
    /// </summary>
    public double ConfidenceThreshold { get; init; } = 0.6;

    /// <summary>
    /// Visible-text word count at or below which a page is considered thin
    /// for the structural heuristic. Defaults to 40 words.
    /// </summary>
    public int ThinContentWordThreshold { get; init; } = 40;

    /// <summary>Validates the option bounds.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is invalid.</exception>
    public void Validate()
    {
        if (ConfidenceThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ConfidenceThreshold), ConfidenceThreshold, "Confidence threshold must be within [0, 1].");
        }

        if (ThinContentWordThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ThinContentWordThreshold), ThinContentWordThreshold, "The thin-content threshold cannot be negative.");
        }
    }
}