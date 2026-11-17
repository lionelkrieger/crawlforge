using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Indexability;

/// <summary>
/// Soft-404 detector (FS-CRAWL-001 §7.12, criterion #28): pages returning a
/// success status whose content matches error-like patterns — a configurable
/// phrase list plus structural heuristics — are flagged distinctly from hard
/// HTTP errors. WAF challenge pages are excluded (they carry the Blocked
/// status class and their own classification, FS-CRAWL-002 §2.6).
/// </summary>
/// <remarks>
/// Pure function of the stored page context: no network, no database reads.
/// The verdict re-runs identically from stored artifacts on re-audit. Text
/// metrics use the scoped visible text when scoping ran (§7.12), falling back
/// to full visible text (baseline-preserving default).
/// </remarks>
public sealed class Soft404Detector : ISoft404Detector
{
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web);

    private readonly Soft404Options _options;

    /// <summary>Creates a detector with local configuration.</summary>
    /// <param name="options">Detection options; null uses bundled defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option bound is invalid.</exception>
    public Soft404Detector(Soft404Options? options = null)
    {
        _options = options ?? new Soft404Options();
        _options.Validate();
    }

    /// <inheritdoc />
    public Task<Soft404Verdict> DetectAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // Hard errors and non-success classes are never soft 404s; WAF
        // challenge pages carry their own distinct classification.
        if (context.Url.StatusClass != CrawlStatusClass.Success
            || context.Url.ChallengeClass is not null
            || context.Url.StatusClass == CrawlStatusClass.Blocked)
        {
            return Task.FromResult(new Soft404Verdict(
                false,
                0,
                SerializeEvidence(new EvidencePayload(
                    context.Url.StatusClass.ToString(),
                    context.Url.FinalStatusCode,
                    null,
                    0,
                    Array.Empty<PhraseMatch>(),
                    0,
                    _options.ConfidenceThreshold,
                    "not-a-success-page"))));
        }

        string title = DomTraversal.Title(context.Document) ?? string.Empty;
        string text = context.ScopedText?.Text ?? DomTraversal.VisibleText(context.Document);
        int wordCount = context.ScopedText?.WordCount ?? CountWords(text);

        var matches = new List<PhraseMatch>();
        double confidence = 0;

        foreach (string phrase in _options.ErrorPhrases)
        {
            if (string.IsNullOrWhiteSpace(phrase))
            {
                continue;
            }

            if (title.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new PhraseMatch(phrase, "title"));
                confidence = Math.Max(confidence, 0.9);
            }
            else if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new PhraseMatch(phrase, "body"));
                confidence = Math.Max(confidence, 0.7);
            }
        }

        // Structural heuristics: error-token titles/bodies on thin pages.
        bool thin = wordCount <= _options.ThinContentWordThreshold;
        bool titleHasErrorToken = ContainsErrorToken(title);
        if (thin && titleHasErrorToken)
        {
            confidence = Math.Max(confidence, 0.65);
        }

        if (thin && ContainsErrorToken(text))
        {
            confidence = Math.Max(confidence, 0.5);
        }

        bool isSoft404 = confidence >= _options.ConfidenceThreshold;
        string reason = isSoft404
            ? (matches.Count > 0 ? "error-phrase-match" : "structural-heuristic")
            : "no-error-signal";

        return Task.FromResult(new Soft404Verdict(
            isSoft404,
            confidence,
            SerializeEvidence(new EvidencePayload(
                context.Url.StatusClass.ToString(),
                context.Url.FinalStatusCode,
                title,
                wordCount,
                matches,
                confidence,
                _options.ConfidenceThreshold,
                reason))));
    }

    private static bool ContainsErrorToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = token.Trim('.', ':', '-', '!', '?', '(', ')');
            if (string.Equals(trimmed, "404", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "error", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string SerializeEvidence(EvidencePayload payload) =>
        JsonSerializer.Serialize(payload, EvidenceOptions);

    private sealed record EvidencePayload(
        string StatusClass,
        int? FinalStatusCode,
        string? Title,
        int WordCount,
        IReadOnlyList<PhraseMatch> MatchedPhrases,
        double Confidence,
        double Threshold,
        string Reason);

    private sealed record PhraseMatch(string Phrase, string Location);
}