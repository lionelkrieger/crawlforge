using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// Pre-audit WAF fingerprinting evaluator (FS-CRAWL-002 §2): Stage A matches
/// header and cookie markers, Stage B matches a bounded leading body snippet
/// (pre-parse, never a full document parse). A response is classified as a
/// challenge only when at least two independent facets corroborate and the
/// aggregate confidence reaches the policy threshold (default 0.8); weaker
/// hits are reported as suspected. Detection and reporting only — no challenge
/// solving, replay, token forging, or evasion (exclusion E3); verdicts derive
/// solely from the local catalogue and the observed response (exclusion E4).
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

        if (!_policy.Enabled)
        {
            return CleanResult(input, parsed, disabled: true);
        }

        var snippet = BoundSnippet(input.BodySnippet);
        var candidates = new List<Candidate>();
        var suspectedKeys = new List<string>();

        foreach (var signature in parsed.Signatures)
        {
            var matches = new List<MarkerMatch>();

            // Stage A: header and cookie facets.
            foreach (var marker in signature.HeaderMarkers)
            {
                if (marker.MatchesHeaders(input.Headers))
                {
                    matches.Add(new MarkerMatch(signature.Signature.Key, marker.Facet, FormatMarker(marker), marker.Weight));
                }
            }

            // Stage B: bounded pre-parse body facet.
            if (snippet is not null)
            {
                foreach (var marker in signature.BodyMarkers)
                {
                    if (marker.MatchesBody(snippet))
                    {
                        matches.Add(new MarkerMatch(signature.Signature.Key, marker.Facet, FormatMarker(marker), marker.Weight));
                    }
                }
            }

            if (matches.Count == 0)
            {
                continue;
            }

            var facets = matches.Select(m => m.Facet).Distinct().OrderBy(f => f).ToList();
            var confidence = Math.Min(1.0, matches.Sum(m => m.Weight));
            var corroborated = facets.Count >= _policy.MinimumCorroboratingFacets
                && confidence >= _policy.ConfidenceThreshold;

            if (corroborated)
            {
                candidates.Add(new Candidate(signature.Signature, confidence, facets, matches));
            }
            else
            {
                suspectedKeys.Add(signature.Signature.Key);
            }
        }

        if (candidates.Count == 0)
        {
            return CleanResult(input, parsed, disabled: false, suspectedKeys);
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
            winner.Matches);

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

    private WafEvaluationResult CleanResult(
        WafEvaluationInput input,
        ParsedCatalogue parsed,
        bool disabled,
        IReadOnlyList<string>? suspectedKeys = null)
    {
        var evidence = BuildEvidence(
            input,
            parsed,
            isChallenge: false,
            suspected: !disabled && suspectedKeys is { Count: > 0 },
            WafProvider.Unknown,
            WafChallengeClass.Unknown,
            confidence: 0,
            facets: [],
            matches: []);
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
        IReadOnlyList<MarkerMatch> matches)
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
            }).ToList(),
            observedHeaders = headers,
            catalogueVersion = parsed.BundledVersion,
            catalogueRevision = parsed.Revision,
            policy = new
            {
                confidenceThreshold = _policy.ConfidenceThreshold,
                minimumCorroboratingFacets = _policy.MinimumCorroboratingFacets,
                bodySnippetLimit = _policy.BodySnippetLimit,
            },
        };
        return JsonSerializer.Serialize(payload, EvidenceJsonOptions);
    }

    private static string FormatMarker(WafMarker marker) => marker.Facet switch
    {
        WafFacet.Cookie => "cookie:" + marker.Name + (marker.ValueSubstring is null ? string.Empty : "~" + marker.ValueSubstring),
        WafFacet.Header => marker.Name + (marker.ValueSubstring is null ? string.Empty : "~" + marker.ValueSubstring),
        _ => marker.Name,
    };

    private sealed record ParsedCatalogue(string BundledVersion, string Revision, IReadOnlyList<ParsedSignature> Signatures);

    private sealed record ParsedSignature(
        WafSignature Signature,
        IReadOnlyList<WafMarker> HeaderMarkers,
        IReadOnlyList<WafMarker> BodyMarkers);

    private sealed record MarkerMatch(string SignatureKey, WafFacet Facet, string Marker, double Weight);

    private sealed record Candidate(
        WafSignature Signature,
        double Confidence,
        IReadOnlyList<WafFacet> Facets,
        IReadOnlyList<MarkerMatch> Matches);
}
