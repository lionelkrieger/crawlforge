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

        var (matches, confidence) = EvaluateErrorSignals(title, text, wordCount);

        bool isSoft404 = confidence >= _options.ConfidenceThreshold;
        string reason = isSoft404
            ? (matches.Count > 0 ? "error-phrase-match" : "structural-heuristic")
            : "no-error-signal";
        string source = "raw";
        string? rawTitle = null;

        // WP-10-04 (phase-10 done_when): client-route soft 404. A SPA shell
        // can serve a clean 200 document whose RENDERED tree shows the error
        // page ("Page not found" for unknown routes). When the raw document
        // carries no error signal but a rendered document exists, evaluate
        // the SAME heuristics over the rendered title/text. The pipeline's
        // dedicated detector step stays the sole emitter (WP-09-05 dedup):
        // one indexability.soft-404 per URL, evidence names the source.
        if (!isSoft404 && context.RenderedDocument is not null)
        {
            string renderedTitle = DomTraversal.Title(context.RenderedDocument) ?? string.Empty;
            string renderedText = DomTraversal.VisibleText(context.RenderedDocument);
            int renderedWordCount = CountWords(renderedText);
            var (renderedMatches, renderedConfidence) = EvaluateErrorSignals(renderedTitle, renderedText, renderedWordCount);
            if (renderedConfidence >= _options.ConfidenceThreshold)
            {
                isSoft404 = true;
                confidence = renderedConfidence;
                matches = renderedMatches;
                reason = "rendered-client-route";
                source = "rendered";
                rawTitle = title;
                title = renderedTitle;
                wordCount = renderedWordCount;
            }
        }

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
                reason,
                source,
                rawTitle))));
    }

    /// <summary>Shared error-signal heuristics (phrase matches + thin-page
    /// structural tokens) over one capture's title/text/word count.</summary>
    private (List<PhraseMatch> Matches, double Confidence) EvaluateErrorSignals(string title, string text, int wordCount)
    {
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

        return (matches, confidence);
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
        string Reason,
        string Source = "raw",
        string? RawTitle = null);

    private sealed record PhraseMatch(string Phrase, string Location);
}