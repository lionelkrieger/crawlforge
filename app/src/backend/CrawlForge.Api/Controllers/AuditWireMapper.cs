using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Projects locked audit/finding entities onto the frozen wire shapes.
/// Counters payload layout (API-owned): queued/fetched/error/blocked/wafBlocked.
/// Finding messages project from evidence when present, otherwise from the
/// bundled rule key (no remote catalogue exists).
/// </summary>
internal static class AuditWireMapper
{
    public static string StateWire(AuditStatus status) => ApiEnumConverter<AuditStatus>.ToWireValue(status);

    public static string PhaseWire(AuditPhase phase) => ApiEnumConverter<AuditPhase>.ToWireValue(phase);

    /// <summary>
    /// WP-09-06 (G06 counter-key reconciliation): the engine producer
    /// (<c>AuditRunCounters.ToPayload()</c>, Host/Execution/AuditRunState.cs)
    /// stores <c>discovered/fetched/analyzed/failed</c>; the frozen wire
    /// (FS-CRAWL-001 §10.3 crawl-progress counters, frontend AuditCounters)
    /// carries <c>queued/fetched/error/blocked</c> plus the additive
    /// <c>wafBlocked</c> sub-count. Producer keys map first
    /// (queued↔discovered, fetched↔fetched, error↔failed); legacy wire keys
    /// remain accepted so pre-fix rows keep parsing. <c>analyzed</c> is an
    /// engine-internal dimension with no wire field. <c>blocked</c>/<c>wafBlocked</c>
    /// pass through when the producer supplies them, otherwise 0/null — the
    /// engine-side emission recipe (handoff) adds them from url_records
    /// classification. The hub normalizer shares this exact mapping so the
    /// live crawl-progress payload and the REST snapshot agree key-for-key.
    /// </summary>
    /// <param name="countersJson">Stored counters payload (producer or legacy wire keys).</param>
    /// <returns>The frozen wire counters.</returns>
    public static AuditCountersWire ParseCounters(string? countersJson)
    {
        var counters = ProjectWireMapper.ParseObject(countersJson);
        return new AuditCountersWire(
            ReadCounter(counters, "discovered", "queued"),
            ReadCounter(counters, "fetched"),
            ReadCounter(counters, "failed", "error"),
            ReadCounter(counters, "blocked"),
            ReadOptionalCounter(counters, "wafBlocked"));
    }

    private static long ReadCounter(JsonObject counters, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (counters[key] is { } node && node.GetValueKind() == JsonValueKind.Number)
            {
                return node.GetValue<long>();
            }
        }

        return 0;
    }

    private static long? ReadOptionalCounter(JsonObject counters, string key) =>
        counters[key] is { } node && node.GetValueKind() == JsonValueKind.Number
            ? node.GetValue<long>()
            : null;

    /// <summary>HOTFIX-01: reads the failure reason stored in the counters payload.</summary>
    private static string? ErrorMessage(string? countersJson) =>
        ProjectWireMapper.ParseObject(countersJson)["errorMessage"]?.GetValue<string>();

    public static AuditSummaryWire ToSummary(AuditDto audit) =>
        new(
            audit.Id.ToString(),
            audit.ProjectId.ToString(),
            audit.ConfigurationId.ToString(),
            StateWire(audit.Status),
            PhaseWire(audit.Phase),
            audit.StartedAt,
            audit.FinishedAt,
            ParseCounters(audit.CountersJson))
        {
            ErrorMessage = ErrorMessage(audit.CountersJson)
        };

    public static AuditDetailWire ToDetail(AuditDto audit)
    {
        var counters = ParseCounters(audit.CountersJson);
        var end = audit.FinishedAt ?? DateTimeOffset.UtcNow;
        var elapsed = audit.StartedAt is { } started ? Math.Max(0, (end - started).TotalSeconds) : 0;
        var throughput = elapsed > 0 ? counters.Fetched / elapsed : 0;
        double? remainder = counters.Queued > 0 && throughput > 0 ? counters.Queued / throughput : null;

        return new AuditDetailWire(
            audit.Id.ToString(),
            audit.ProjectId.ToString(),
            audit.ConfigurationId.ToString(),
            StateWire(audit.Status),
            PhaseWire(audit.Phase),
            audit.StartedAt,
            audit.FinishedAt,
            counters,
            Math.Round(elapsed, 3),
            Math.Round(throughput, 3),
            remainder is { } value ? Math.Round(value, 3) : null)
        {
            ErrorMessage = ErrorMessage(audit.CountersJson)
        };
    }

    public static FindingWire ToFinding(FindingDto finding)
    {
        var evidence = ProjectWireMapper.ParseObject(finding.EvidenceJson);
        var (message, evidenceWire) = ProjectEvidence(evidence, finding.RuleKey);
        return new FindingWire(
            finding.Id.ToString(),
            finding.RuleKey,
            ApiEnumConverter<FindingSeverity>.ToWireValue(finding.Severity),
            ApiEnumConverter<AuditCategory>.ToWireValue(finding.Category),
            finding.UrlId?.ToString(),
            message,
            evidenceWire);
    }

    /// <summary>
    /// Projects an unpersisted single-page finding draft onto the wire shape.
    /// Drafts carry no identifier yet, so the wire id is empty until persisted.
    /// </summary>
    /// <param name="draft">Finding draft from the single-page analyzer.</param>
    /// <returns>The wire finding.</returns>
    public static FindingWire ToFindingDraft(FindingDraft draft)
    {
        var evidence = ProjectWireMapper.ParseObject(draft.EvidenceJson);
        var (message, evidenceWire) = ProjectEvidence(evidence, draft.RuleKey);
        return new FindingWire(
            string.Empty,
            draft.RuleKey,
            ApiEnumConverter<FindingSeverity>.ToWireValue(draft.Severity),
            ApiEnumConverter<AuditCategory>.ToWireValue(draft.Category),
            draft.UrlId?.ToString(),
            message,
            evidenceWire);
    }

    /// <summary>
    /// Projects a live WAF evaluation verdict (tools/single-page) onto the wire
    /// detection shape. Confidence rides the evidence payload; the first matched
    /// signature key is surfaced as the detection's signature key.
    /// </summary>
    /// <param name="verdict">Evaluator verdict.</param>
    /// <returns>The wire WAF detection.</returns>
    public static WafDetectionWire ToWafVerdict(WafEvaluationResult verdict)
    {
        var evidence = ProjectWireMapper.ParseObject(verdict.EvidenceJson);
        var headers = evidence["headers"] as JsonObject;
        var signatureKey = verdict.MatchedSignatureKeys.Count > 0 ? verdict.MatchedSignatureKeys[0] : null;
        return new WafDetectionWire(
            ApiEnumConverter<WafProvider>.ToWireValue(verdict.Provider),
            ApiEnumConverter<WafChallengeClass>.ToWireValue(verdict.ChallengeClass),
            evidence["confidence"]?.GetValue<double>() ?? 1.0,
            signatureKey,
            new WafEvidenceWire(
                evidence["snippet"]?.GetValue<string>(),
                headers?.ToDictionary(h => h.Key, h => h.Value?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal),
                signatureKey));
    }

    private static (string Message, FindingEvidenceWire? Evidence) ProjectEvidence(JsonObject evidence, string ruleKey)
    {
        var message = evidence["message"]?.GetValue<string>()
            ?? ruleKey.Replace('.', ' ').Replace('-', ' ');

        FindingEvidenceWire? evidenceWire = null;
        if (evidence.Count > 0)
        {
            var details = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in evidence)
            {
                if (property.Key is "message" or "snippet" or "selector" || property.Value is null)
                {
                    continue;
                }

                details[property.Key] = JsonSerializer.SerializeToElement(property.Value);
            }

            evidenceWire = new FindingEvidenceWire(
                evidence["snippet"]?.GetValue<string>(),
                evidence["selector"]?.GetValue<string>(),
                details.Count > 0 ? details : null);
        }

        return (message, evidenceWire);
    }
}
