using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Pipeline;

/// <summary>
/// Pipeline-local diagnostic findings (FS-CRAWL-002 §4.2: evaluation failures
/// are isolated and logged as rule-level diagnostics; they never fail the
/// audit). Diagnostic keys are open-string taxonomy members reserved for the
/// pipeline itself, not product checks.
/// </summary>
public static class PipelineDiagnostics
{
    /// <summary>Rule key: an analyzer threw and was isolated.</summary>
    public const string AnalyzerFailureRuleKey = "pipeline.analyzer-failure";

    /// <summary>Rule key: the tolerant parser reported markup warnings.</summary>
    public const string ParseWarningsRuleKey = "pipeline.parse-warnings";

    /// <summary>Rule key: no stored document was available for the page.</summary>
    public const string NoDocumentRuleKey = "pipeline.no-document";

    /// <summary>Rule key: soft-404 verdict from the injected detector.</summary>
    public const string Soft404RuleKey = "indexability.soft-404";

    /// <summary>Builds an isolated analyzer-failure diagnostic.</summary>
    /// <param name="analyzerName">Analyzer type name.</param>
    /// <param name="error">Failure message.</param>
    /// <param name="urlId">Affected URL reference.</param>
    /// <returns>A Notice diagnostic finding.</returns>
    public static FindingDraft AnalyzerFailure(string analyzerName, string error, long? urlId) =>
        new(
            AnalyzerFailureRuleKey,
            AuditCategory.Crawl,
            FindingSeverity.Notice,
            urlId,
            JsonSerializer.Serialize(new { analyzer = analyzerName, error }),
            RecommendationKeys.ForRule(AnalyzerFailureRuleKey));

    /// <summary>Builds a parse-warning summary diagnostic.</summary>
    /// <param name="warnings">Tolerant-parse warnings.</param>
    /// <param name="urlId">Affected URL reference.</param>
    /// <returns>A Notice diagnostic finding.</returns>
    public static FindingDraft ParseWarnings(IReadOnlyList<string> warnings, long? urlId) =>
        new(
            ParseWarningsRuleKey,
            AuditCategory.Crawl,
            FindingSeverity.Notice,
            urlId,
            JsonSerializer.Serialize(new { warningCount = warnings.Count, warnings = warnings.Take(20).ToArray() }),
            RecommendationKeys.ForRule(ParseWarningsRuleKey));

    /// <summary>Builds a missing-document diagnostic.</summary>
    /// <param name="urlId">Affected URL reference.</param>
    /// <returns>A Notice diagnostic finding.</returns>
    public static FindingDraft NoDocument(long? urlId) =>
        new(
            NoDocumentRuleKey,
            AuditCategory.Crawl,
            FindingSeverity.Notice,
            urlId,
            JsonSerializer.Serialize(new { reason = "no stored document or fetch result in context" }),
            RecommendationKeys.ForRule(NoDocumentRuleKey));

    /// <summary>
    /// Merges additional fields into an evidence JSON object. Non-object or
    /// invalid payloads are preserved under an "original" field so evidence is
    /// never lost.
    /// </summary>
    /// <param name="evidenceJson">Existing evidence payload.</param>
    /// <param name="fields">Fields to merge.</param>
    /// <returns>The merged evidence JSON.</returns>
    public static string MergeEvidence(string evidenceJson, IReadOnlyDictionary<string, object?> fields)
    {
        JsonObject? obj = null;
        try
        {
            obj = JsonNode.Parse(evidenceJson) as JsonObject;
        }
        catch (JsonException)
        {
            // Fall through: wrap the original payload.
        }

        if (obj is null)
        {
            obj = new JsonObject { ["original"] = evidenceJson };
        }

        foreach (var (key, value) in fields)
        {
            obj[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
        }

        return obj.ToJsonString();
    }
}
