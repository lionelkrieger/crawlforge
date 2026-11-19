using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// Pre-audit WAF fingerprinting evaluator (FS-CRAWL-002 §2): Stage A matches
/// header and cookie markers, Stage B matches the bounded leading body snippet
/// through the five-facet set (body substring, title, dom-marker selector,
/// script source/inline markers — pre-parse, never a full document parse).
/// Eligibility (§2.4.1): only 2xx responses with HTML content type may
/// transition to waf-challenge; signatures on other responses are evidence
/// annotations only. Corroboration guard (§2.3.3): a challenge verdict needs
/// the policy confidence threshold (default 0.8) AND either at least two
/// independent facet types or one high-specificity provider-specific
/// header/cookie marker; weaker hits are reported as suspected. Detection and
/// reporting only — no challenge solving, replay, token forging, or evasion
/// (exclusion E3); verdicts derive solely from the local catalogue and the
/// observed response (exclusion E4).
/// </summary>
public sealed class WafChallengeEvaluator : IWafChallengeEvaluator
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IWafSignatureCatalogue _catalogue;
    private readonly WafDetectionPolicy _policy;
    private ParsedCatalogue? _parsed;

    /// <summary>Creates an evaluator over the effective signature catalogue.</summary>
    /// <param name="catalogue">Effective catalogue (bundled asset + user-local entries).</param>
    /// <param name="policy">Detection policy; <see langword="null"/> uses the documented defaults.</param>
    public WafChallengeEvaluator(IWafSignatureCatalogue catalogue, WafDetectionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        _catalogue = catalogue;
        _policy = policy ?? WafDetectionPolicy.Default;
    }

    /// <summary>Creates and initializes an evaluator in one step.</summary>
    /// <param name="catalogue">Effective catalogue.</param>
    /// <param name="policy">Detection policy; <see langword="null"/> uses the defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An evaluator ready to classify responses.</returns>
    public static async Task<WafChallengeEvaluator> CreateAsync(
        IWafSignatureCatalogue catalogue,
        WafDetectionPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        var evaluator = new WafChallengeEvaluator(catalogue, policy);
        await evaluator.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return evaluator;
    }

    /// <summary>
    /// Loads the effective catalogue snapshot. Call again to refresh after
    /// catalogue edits (for example, an offline re-audit with new user entries).
    /// Must complete before <see cref="Evaluate"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the snapshot is loaded.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _catalogue.GetEffectiveCatalogueAsync(cancellationToken).ConfigureAwait(false);
        var signatures = snapshot.Signatures
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new ParsedSignature(
                s,
                s.HeaderMarkers.Select(WafMarker.ParseHeaderMarker).ToList(),
                s.BodyMarkers.Select(WafMarker.ParseBodyMarker).ToList()))
            .ToList();
        _parsed = new ParsedCatalogue(snapshot.BundledVersion, snapshot.Revision, signatures);
    }

    /// <inheritdoc />
    public WafEvaluationResult Evaluate(WafEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var parsed = _parsed ?? throw new InvalidOperationException(
            "The evaluator is not initialized; call InitializeAsync before Evaluate.");

        // Eligibility (§2.4.1): only 2xx HTML responses may become waf-challenge;
        // signatures on other responses are evidence annotations only.
        var (eligible, statusEligible, contentTypeEligible) = AssessEligibility(input);

        if (!_policy.Enabled)
        {
            return CleanResult(input, parsed, disabled: true, eligible, statusEligible, contentTypeEligible);
        }

        var snippet = BoundSnippet(input.BodySnippet);

        // Stage B evidence is extracted once per response, bounded by policy.
        var stageB = snippet is null ? null : StageBExtractor.Extract(snippet, _policy.StageBNodeLimit);

        var candidates = new List<Candidate>();
        var suspectedKeys = new List<string>();
        var suspectedCandidates = new List<Candidate>();

        foreach (var signature in parsed.Signatures)
        {
            var matches = new List<MarkerMatch>();

            // Stage A: header and cookie facets.
            foreach (var marker in signature.HeaderMarkers)
            {
                if (marker.MatchesHeaders(input.Headers))
                {
                    matches.Add(new MarkerMatch(signature.Signature.Key, marker.Facet, FormatMarker(marker), marker.Weight, marker.HighSpecificity));
                }
            }

            // Stage B: bounded pre-parse — body, title, dom-marker, script facets.
            if (stageB is not null)
            {
                foreach (var marker in signature.BodyMarkers)
                {
                    if (marker.MatchesStageB(stageB))
                    {
                        matches.Add(new MarkerMatch(signature.Signature.Key, marker.Facet, FormatMarker(marker), marker.Weight, marker.HighSpecificity));
                    }
                }
            }

            if (matches.Count == 0)
            {
                continue;
            }

            var facets = matches.Select(m => m.Facet).Distinct().OrderBy(f => f).ToList();
            var confidence = Math.Min(1.0, matches.Sum(m => m.Weight));

            // Corroboration guard (§2.3.3): two independent facet types OR one
            // high-specificity provider-specific header/cookie marker.
            var corroborated = confidence >= _policy.ConfidenceThreshold
                && (facets.Count >= _policy.MinimumCorroboratingFacets
                    || matches.Any(m => m.HighSpecificity));

            if (eligible && corroborated)
            {
                candidates.Add(new Candidate(signature.Signature, confidence, facets, matches));
            }
            else
            {
                suspectedKeys.Add(signature.Signature.Key);
                suspectedCandidates.Add(new Candidate(signature.Signature, confidence, facets, matches));
            }
        }

        if (candidates.Count == 0)
        {
            // Suspected evidence carries the strongest candidate's facets and
            // confidence (§2.3.3 Notice path) — attribution stays non-blocking.
            var bestSuspected = suspectedCandidates
                .OrderByDescending(c => c.Confidence)
                .ThenByDescending(c => c.Facets.Count)
                .ThenBy(c => c.Signature.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            return CleanResult(input, parsed, disabled: false, eligible, statusEligible, contentTypeEligible, suspectedKeys, bestSuspected);
        }

        var winner = candidates
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.Facets.Count)
            .ThenBy(c => c.Signature.Key, StringComparer.Ordinal)
            .First();

        var matchedKeys = candidates
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.Signature.Key, StringComparer.Ordinal)
            .Select(c => c.Signature.Key)
            .ToList();

        var evidence = BuildEvidence(
            input,
            parsed,
            isChallenge: true,
            suspected: false,
            winner.Signature.Provider,
            winner.Signature.ChallengeClass,
            winner.Confidence,
            winner.Facets,
            winner.Matches,
            eligible,
            statusEligible,
            contentTypeEligible);

        return new WafEvaluationResult(
            IsChallenge: true,
            winner.Signature.Provider,
            winner.Signature.ChallengeClass,
            matchedKeys,
            evidence);
    }

    private string? BoundSnippet(string? bodySnippet)
    {
        if (bodySnippet is null || bodySnippet.Length <= _policy.BodySnippetLimit)
        {
            return bodySnippet;
        }

        return bodySnippet[.._policy.BodySnippetLimit];
    }

    private static (bool Eligible, bool StatusEligible, bool ContentTypeEligible) AssessEligibility(WafEvaluationInput input)
    {
        var statusEligible = input.StatusCode is >= 200 and < 300;

        string? contentType = null;
        foreach (var (key, value) in input.Headers)
        {
            if (string.Equals(key, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
                break;
            }
        }

        // An absent content type is treated as potentially HTML (the fetch
        // layer records the header when the server sends one); an explicit
        // non-HTML type is never eligible (§2.4.1).
        var contentTypeEligible = string.IsNullOrWhiteSpace(contentType)
            || contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

        return (statusEligible && contentTypeEligible, statusEligible, contentTypeEligible);
    }

    private WafEvaluationResult CleanResult(
        WafEvaluationInput input,
        ParsedCatalogue parsed,
        bool disabled,
        bool eligible,
        bool statusEligible,
        bool contentTypeEligible,
        IReadOnlyList<string>? suspectedKeys = null,
        Candidate? bestSuspected = null)
    {
        var evidence = BuildEvidence(
            input,
            parsed,
            isChallenge: false,
            suspected: !disabled && suspectedKeys is { Count: > 0 },
            WafProvider.Unknown,
            WafChallengeClass.Unknown,
            confidence: bestSuspected?.Confidence ?? 0,
            facets: bestSuspected?.Facets ?? [],
            matches: bestSuspected?.Matches ?? [],
            eligible,
            statusEligible,
            contentTypeEligible);
        return new WafEvaluationResult(
            IsChallenge: false,
            WafProvider.Unknown,
            WafChallengeClass.Unknown,
            suspectedKeys ?? [],
            evidence);
    }

    private string BuildEvidence(
        WafEvaluationInput input,
        ParsedCatalogue parsed,
        bool isChallenge,
        bool suspected,
        WafProvider provider,
        WafChallengeClass challengeClass,
        double confidence,
        IReadOnlyList<WafFacet> facets,
        IReadOnlyList<MarkerMatch> matches,
        bool eligible,
        bool statusEligible,
        bool contentTypeEligible)
    {
        var headers = input.Headers
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);

        var payload = new
        {
            url = input.Url,
            statusCode = input.StatusCode,
            isChallenge,
            suspected,
            eligible,
            statusEligible,
            contentTypeEligible,
            provider = (int)provider,
            providerName = provider.ToString(),
            challengeClass = (int)challengeClass,
            challengeClassName = challengeClass.ToString(),
            confidence,
            matchedFacets = facets.Select(f => f.ToString()).ToList(),
            matches = matches.Select(m => new
            {
                signatureKey = m.SignatureKey,
                facet = m.Facet.ToString(),
                marker = m.Marker,
                weight = m.Weight,
                highSpecificity = m.HighSpecificity,
            }).ToList(),
            observedHeaders = headers,
            catalogueVersion = parsed.BundledVersion,
            catalogueRevision = parsed.Revision,
            policy = new
            {
                confidenceThreshold = _policy.ConfidenceThreshold,
                minimumCorroboratingFacets = _policy.MinimumCorroboratingFacets,
                bodySnippetLimit = _policy.BodySnippetLimit,
                stageBNodeLimit = _policy.StageBNodeLimit,
            },
        };
        return JsonSerializer.Serialize(payload, EvidenceJsonOptions);
    }

    private static string FormatMarker(WafMarker marker)
    {
        var text = marker.Facet switch
        {
            WafFacet.Cookie => "cookie:" + marker.Name + (marker.ValueSubstring is null ? string.Empty : "~" + marker.ValueSubstring),
            WafFacet.Header => marker.Name + (marker.ValueSubstring is null ? string.Empty : "~" + marker.ValueSubstring),
            WafFacet.Title => "title:" + marker.Name,
            WafFacet.DomMarker => "dom:" + marker.Name,
            WafFacet.Script => "script:" + marker.Name,
            _ => marker.Name,
        };
        return marker.HighSpecificity ? text + "|high" : text;
    }

    private sealed record ParsedCatalogue(string BundledVersion, string Revision, IReadOnlyList<ParsedSignature> Signatures);

    private sealed record ParsedSignature(
        WafSignature Signature,
        IReadOnlyList<WafMarker> HeaderMarkers,
        IReadOnlyList<WafMarker> BodyMarkers);

    private sealed record MarkerMatch(string SignatureKey, WafFacet Facet, string Marker, double Weight, bool HighSpecificity);

    private sealed record Candidate(
        WafSignature Signature,
        double Confidence,
        IReadOnlyList<WafFacet> Facets,
        IReadOnlyList<MarkerMatch> Matches);
}
