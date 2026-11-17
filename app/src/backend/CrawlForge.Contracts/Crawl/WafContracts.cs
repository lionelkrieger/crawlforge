using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Crawl;

/// <summary>
/// Pre-audit fingerprinting pass at response receipt (FS-CRAWL-002 §2):
/// classifies 200-OK challenge pages and other WAF responses. Detection and
/// reporting only — no challenge solving, replay, token forging, or evasion
/// (exclusion E3); verdicts derive solely from the local catalogue and the
/// observed response (exclusion E4).
/// </summary>
public interface IWafChallengeEvaluator
{
    /// <summary>Evaluates an observed response against the effective signature catalogue.</summary>
    /// <param name="input">Observed response facts.</param>
    /// <returns>Challenge verdict with provider attribution and evidence.</returns>
    WafEvaluationResult Evaluate(WafEvaluationInput input);
}

/// <summary>
/// Observed response facts supplied to the WAF evaluator.
/// </summary>
/// <param name="Url">URL that produced the response.</param>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="Headers">Response headers.</param>
/// <param name="BodySnippet">Bounded leading snippet of the response body.</param>
public sealed record WafEvaluationInput(
    string Url,
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    string? BodySnippet);

/// <summary>
/// WAF evaluation verdict.
/// </summary>
/// <param name="IsChallenge">Whether the response is a challenge/block page.</param>
/// <param name="Provider">Attributed provider label.</param>
/// <param name="ChallengeClass">Challenge page class.</param>
/// <param name="MatchedSignatureKeys">Catalogue signature keys that matched.</param>
/// <param name="EvidenceJson">Structured evidence payload.</param>
public sealed record WafEvaluationResult(
    bool IsChallenge,
    WafProvider Provider,
    WafChallengeClass ChallengeClass,
    IReadOnlyList<string> MatchedSignatureKeys,
    string EvidenceJson);

/// <summary>
/// Effective WAF signature catalogue: bundled entries plus user-local entries
/// (schema catalog: waf_user_signatures). Bundled signatures are packaged assets
/// updated only with product updates; runtime fetch is forbidden (exclusion E2).
/// </summary>
public interface IWafSignatureCatalogue
{
    /// <summary>Returns the effective catalogue (bundled version + user entries).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Catalogue snapshot with version and revision.</returns>
    Task<WafCatalogueSnapshot> GetEffectiveCatalogueAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a user-local signature entry.</summary>
    /// <param name="signature">User signature payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted user signature.</returns>
    Task<WafUserSignatureDto> UpsertUserSignatureAsync(WafUserSignatureDto signature, CancellationToken cancellationToken = default);

    /// <summary>Deletes a user entry, restoring any bundled default with the same key.</summary>
    /// <param name="key">Signature key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when an entry was deleted.</returns>
    Task<bool> DeleteUserSignatureAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Snapshot of the effective WAF signature catalogue.
/// </summary>
/// <param name="BundledVersion">Version of the bundled asset catalogue.</param>
/// <param name="Revision">Revision of the effective (merged) catalogue.</param>
/// <param name="Signatures">Effective signatures.</param>
public sealed record WafCatalogueSnapshot(
    string BundledVersion,
    string Revision,
    IReadOnlyList<WafSignature> Signatures);

/// <summary>
/// One WAF signature authored from publicly documented or publicly observable
/// response characteristics (clean-room vocabulary, FS-CRAWL-002 §1.2).
/// </summary>
/// <param name="Key">Stable signature key.</param>
/// <param name="Provider">Provider label the signature attributes to.</param>
/// <param name="ChallengeClass">Challenge class the signature indicates.</param>
/// <param name="HeaderMarkers">Header marker patterns.</param>
/// <param name="BodyMarkers">Body marker patterns.</param>
/// <param name="IsBundled">Whether the signature ships with the product.</param>
public sealed record WafSignature(
    string Key,
    WafProvider Provider,
    WafChallengeClass ChallengeClass,
    IReadOnlyList<string> HeaderMarkers,
    IReadOnlyList<string> BodyMarkers,
    bool IsBundled);
