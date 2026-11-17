using System.Text.Json;

namespace CrawlForge.Api.Controllers;

/// <summary>Wire crawl counters; wafBlocked is the additive WAF sub-count.</summary>
/// <param name="Queued">URLs waiting in the frontier.</param>
/// <param name="Fetched">URLs fetched successfully.</param>
/// <param name="Error">URLs that ended in errors.</param>
/// <param name="Blocked">URLs blocked (robots/WAF).</param>
/// <param name="WafBlocked">Additive: URLs blocked by a detected WAF.</param>
public sealed record AuditCountersWire(long Queued, long Fetched, long Error, long Blocked, long? WafBlocked);

/// <summary>POST /api/audits — start payload; returns immediately.</summary>
/// <param name="ConfigurationId">Configuration to run.</param>
/// <param name="LimitOverrides">Optional per-audit limit overrides.</param>
/// <param name="UrlList">Additive: explicit URL list for list-mode ingestion.</param>
/// <param name="ListMode">Additive: enable list-mode semantics.</param>
public sealed record AuditStartRequest(string ConfigurationId, CrawlLimitsWire? LimitOverrides, IReadOnlyList<string>? UrlList, bool? ListMode);

/// <summary>Audit start response; progress flows over the hub.</summary>
/// <param name="AuditId">New audit identifier.</param>
/// <param name="State">Initial lifecycle state.</param>
public sealed record AuditStartResponse(string AuditId, string State);

/// <summary>Wire audit summary row.</summary>
/// <param name="Id">Audit identifier.</param>
/// <param name="ProjectId">Owning project identifier.</param>
/// <param name="ConfigurationId">Source configuration identifier.</param>
/// <param name="State">Lifecycle state wire value.</param>
/// <param name="Phase">Pipeline phase identifier.</param>
/// <param name="StartedAt">Start timestamp, when started.</param>
/// <param name="CompletedAt">Finish timestamp, when finished.</param>
/// <param name="Counters">Crawl counters.</param>
public sealed record AuditSummaryWire(
    string Id,
    string ProjectId,
    string ConfigurationId,
    string State,
    string Phase,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    AuditCountersWire Counters)
{
    /// <summary>HOTFIX-01: failure reason persisted with the run, when present.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>Wire audit detail with derived progress fields.</summary>
/// <param name="Id">Audit identifier.</param>
/// <param name="ProjectId">Owning project identifier.</param>
/// <param name="ConfigurationId">Source configuration identifier.</param>
/// <param name="State">Lifecycle state wire value.</param>
/// <param name="Phase">Pipeline phase identifier.</param>
/// <param name="StartedAt">Start timestamp, when started.</param>
/// <param name="CompletedAt">Finish timestamp, when finished.</param>
/// <param name="Counters">Crawl counters.</param>
/// <param name="ElapsedSeconds">Elapsed run time in seconds.</param>
/// <param name="ThroughputPerSecond">Fetched URLs per second.</param>
/// <param name="EstimatedRemainderSeconds">Estimated remaining seconds, when derivable.</param>
public sealed record AuditDetailWire(
    string Id,
    string ProjectId,
    string ConfigurationId,
    string State,
    string Phase,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    AuditCountersWire Counters,
    double ElapsedSeconds,
    double ThroughputPerSecond,
    double? EstimatedRemainderSeconds)
{
    /// <summary>HOTFIX-01: failure reason persisted with the run, when present.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>Lifecycle transition response.</summary>
/// <param name="AuditId">Audit identifier.</param>
/// <param name="State">New lifecycle state.</param>
public sealed record AuditStateResponse(string AuditId, string State);

/// <summary>POST /api/audits/{id}/reaudit — optional refresh flag.</summary>
/// <param name="Refresh">When true, refresh stored content before re-auditing.</param>
public sealed record ReauditRequest(bool? Refresh);

/// <summary>Reaudit response linking the new run to its source.</summary>
/// <param name="AuditId">New audit identifier.</param>
/// <param name="SourceAuditId">Source audit identifier.</param>
/// <param name="State">Initial lifecycle state.</param>
public sealed record ReauditResponse(string AuditId, string SourceAuditId, string State);

/// <summary>Wire finding evidence (local observation only).</summary>
/// <param name="Snippet">Observed snippet, when present.</param>
/// <param name="Selector">Source selector, when present.</param>
/// <param name="Details">Structured evidence details.</param>
public sealed record FindingEvidenceWire(string? Snippet, string? Selector, IReadOnlyDictionary<string, JsonElement>? Details);

/// <summary>Wire finding row; severity/category are bundled catalogue keys.</summary>
/// <param name="Id">Finding identifier.</param>
/// <param name="RuleKey">Rule catalogue key.</param>
/// <param name="Severity">Severity wire value.</param>
/// <param name="Category">Category wire value.</param>
/// <param name="UrlId">URL reference, when URL-scoped.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="Evidence">Evidence, when present.</param>
public sealed record FindingWire(string Id, string RuleKey, string Severity, string Category, string? UrlId, string Message, FindingEvidenceWire? Evidence);

/// <summary>Wire metric delta between two audits.</summary>
/// <param name="Metric">Metric key.</param>
/// <param name="Before">Base audit value.</param>
/// <param name="After">Target audit value.</param>
/// <param name="Delta">After minus before.</param>
public sealed record MetricDeltaWire(string Metric, double Before, double After, double Delta);

/// <summary>GET /api/audits/compare — new/resolved findings and metric deltas.</summary>
/// <param name="NewFindings">Findings present only in the target audit.</param>
/// <param name="ResolvedFindings">Findings present only in the base audit.</param>
/// <param name="MetricDeltas">Metric deltas.</param>
public sealed record AuditCompareResponse(IReadOnlyList<FindingWire> NewFindings, IReadOnlyList<FindingWire> ResolvedFindings, IReadOnlyList<MetricDeltaWire> MetricDeltas);
