using System.Data.Common;
using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Fetching;

namespace CrawlForge.Crawler.Frontier;

/// <summary>
/// Broken-link analysis options (local configuration only — exclusion rows
/// 5/8). External checking implements the "check external link status without
/// crawling further" behavior (CAP-A-X22): external targets are probed once
/// each, never enqueued into the crawl.
/// </summary>
/// <param name="CheckExternalLinks">Whether external link targets are probed (CAP-B-047).</param>
/// <param name="MaxExternalChecks">Maximum distinct external targets probed per run (at least 1).</param>
/// <param name="MaxSourcesPerFinding">Maximum source URLs retained in one finding's evidence (at least 1).</param>
/// <param name="ExternalTimeoutSeconds">Per-probe timeout for external targets (at least 1).</param>
public sealed record BrokenLinkOptions(
    bool CheckExternalLinks = true,
    int MaxExternalChecks = 1_000,
    int MaxSourcesPerFinding = 10,
    int ExternalTimeoutSeconds = 15)
{
    /// <summary>Validates the bounds.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is invalid.</exception>
    public BrokenLinkOptions Validate()
    {
        if (MaxExternalChecks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxExternalChecks), MaxExternalChecks, "The external-check bound must be positive.");
        }

        if (MaxSourcesPerFinding < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSourcesPerFinding), MaxSourcesPerFinding, "The source-evidence bound must be positive.");
        }

        if (ExternalTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ExternalTimeoutSeconds), ExternalTimeoutSeconds, "The probe timeout must be at least 1 second.");
        }

        return this;
    }
}

/// <summary>
/// Broken-link analysis outcome.
/// </summary>
/// <param name="Findings">Finding drafts (links.broken-internal / links.broken-external).</param>
/// <param name="Diagnostics">Bounded diagnostics (bounds reached, probe notes).</param>
/// <param name="ExternalChecked">Distinct external targets probed.</param>
/// <param name="ExternalBroken">External targets found broken.</param>
public sealed record BrokenLinkAnalysisResult(
    IReadOnlyList<FindingDto> Findings,
    IReadOnlyList<string> Diagnostics,
    int ExternalChecked,
    int ExternalBroken);

/// <summary>
/// Broken internal and external link findings with source-URL evidence
/// (CAP-B-046/047; reopens WP-03-04: probe-2 never reported the external
/// DNS/connect failure). Internal: a link target whose url_records row is a
/// 4xx/5xx status or a fetch error, reported per target with the declaring
/// source URLs. External: each distinct external link target is probed once
/// (never crawled further); DNS/connect/TLS/timeout failures and 4xx/5xx
/// statuses are reported with the declaring source URLs.
/// </summary>
/// <remarks>
/// Streaming passes over link_edges/url_records with compact per-target
/// evidence state (source lists bounded by <see cref="BrokenLinkOptions.MaxSourcesPerFinding"/>,
/// distinct external probes bounded by <see cref="BrokenLinkOptions.MaxExternalChecks"/>).
/// Outbound probes go only to link targets declared by the crawled site —
/// the crawl-target network boundary (§2.6). Pure reader + prober: emits
/// <see cref="FindingDto"/> drafts; persistence goes through
/// <see cref="CrawlFindingRecorder"/> (WP-09-11 wiring). Thread-safe.
/// </remarks>
public sealed class BrokenLinkAnalyzer
{
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDbProvider _provider;
    private readonly IHttpFetcher _fetcher;

    /// <summary>Creates the analyzer over the configured provider and fetcher.</summary>
    /// <param name="provider">The configured database provider.</param>
    /// <param name="fetcher">HTTP fetcher for external link probes.</param>
    public BrokenLinkAnalyzer(IDbProvider provider, IHttpFetcher fetcher)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
    }

    /// <summary>
    /// Analyzes the audit's link graph for broken internal and external links.
    /// </summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="options">Analysis options; defaults when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Findings plus diagnostics and probe counters.</returns>
    public async Task<BrokenLinkAnalysisResult> AnalyzeAsync(
        long auditId,
        BrokenLinkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = (options ?? new BrokenLinkOptions()).Validate();
        var findings = new List<FindingDto>();
        var diagnostics = new List<string>();

        await AnalyzeInternalAsync(auditId, effective, findings, cancellationToken).ConfigureAwait(false);
        var (externalChecked, externalBroken) = await AnalyzeExternalAsync(auditId, effective, findings, diagnostics, cancellationToken).ConfigureAwait(false);

        return new BrokenLinkAnalysisResult(findings, diagnostics, externalChecked, externalBroken);
    }

    private async Task AnalyzeInternalAsync(long auditId, BrokenLinkOptions options, List<FindingDto> findings, CancellationToken cancellationToken)
    {
        // Error targets: 4xx/5xx status classes and transport failures.
        var errorTargets = new Dictionary<long, ErrorTarget>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id, canonical_url, status_class, final_status_code FROM url_records " +
                $"WHERE audit_id = @audit AND status_class IN ({(int)CrawlStatusClass.ClientError}, {(int)CrawlStatusClass.ServerError}, {(int)CrawlStatusClass.FetchError}) " +
                "ORDER BY id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                errorTargets[reader.GetInt64(0)] = new ErrorTarget(
                    reader.GetString(1),
                    (CrawlStatusClass)reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3));
            }
        }

        if (errorTargets.Count == 0)
        {
            return;
        }

        // Internal edges pointing at error targets, with declaring source URLs.
        var sourcesByTarget = new Dictionary<long, SourceEvidence>();
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT e.target_url_id, u.canonical_url FROM link_edges e " +
                "JOIN url_records u ON u.id = e.source_url_id " +
                "WHERE e.audit_id = @audit AND e.is_internal = 1 AND e.target_url_id IS NOT NULL ORDER BY e.id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var targetId = reader.GetInt64(0);
                if (!errorTargets.ContainsKey(targetId))
                {
                    continue;
                }

                if (!sourcesByTarget.TryGetValue(targetId, out var evidence))
                {
                    evidence = new SourceEvidence(options.MaxSourcesPerFinding);
                    sourcesByTarget[targetId] = evidence;
                }

                evidence.Add(reader.GetString(1));
            }
        }

        foreach (var (targetId, evidence) in sourcesByTarget.OrderBy(kv => kv.Key))
        {
            var target = errorTargets[targetId];
            findings.Add(new FindingDto(
                Id: 0,
                AuditId: auditId,
                RuleKey: LinkRuleKeys.BrokenInternal,
                Category: AuditCategory.Linking,
                Severity: FindingSeverity.Error,
                UrlId: targetId,
                EvidenceJson: JsonSerializer.Serialize(new
                {
                    targetUrl = target.CanonicalUrl,
                    targetUrlId = targetId,
                    statusCode = target.StatusCode,
                    statusClass = target.StatusClass.ToString(),
                    sourceUrls = evidence.Sources,
                    sourceCount = evidence.Count,
                }, EvidenceJson),
                RecommendationKey: null,
                IsSuppressed: false));
        }
    }

    private async Task<(int Checked, int Broken)> AnalyzeExternalAsync(
        long auditId,
        BrokenLinkOptions options,
        List<FindingDto> findings,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!options.CheckExternalLinks)
        {
            return (0, 0);
        }

        // Distinct external targets with declaring source URLs (bounded).
        var targets = new Dictionary<string, SourceEvidence>(StringComparer.OrdinalIgnoreCase);
        var boundReached = false;
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT e.canonical_target_url, u.canonical_url FROM link_edges e " +
                "JOIN url_records u ON u.id = e.source_url_id " +
                "WHERE e.audit_id = @audit AND e.is_internal = 0 ORDER BY e.id";
            AddParameter(command, "@audit", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var targetUrl = reader.GetString(0);
                if (!targets.TryGetValue(targetUrl, out var evidence))
                {
                    if (targets.Count >= options.MaxExternalChecks)
                    {
                        if (!boundReached)
                        {
                            diagnostics.Add($"External link checking stopped at the {options.MaxExternalChecks}-target bound; remaining targets were not probed.");
                            boundReached = true;
                        }

                        continue;
                    }

                    evidence = new SourceEvidence(options.MaxSourcesPerFinding);
                    targets[targetUrl] = evidence;
                }

                evidence.Add(reader.GetString(1));
            }
        }

        var broken = 0;
        foreach (var (targetUrl, evidence) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExternalProbeOutcome outcome = await ProbeExternalAsync(targetUrl, options, cancellationToken).ConfigureAwait(false);
            if (!outcome.IsBroken)
            {
                continue;
            }

            broken++;
            findings.Add(new FindingDto(
                Id: 0,
                AuditId: auditId,
                RuleKey: LinkRuleKeys.BrokenExternal,
                Category: AuditCategory.Linking,
                Severity: FindingSeverity.Warning,
                UrlId: null,
                EvidenceJson: JsonSerializer.Serialize(new
                {
                    targetUrl,
                    statusCode = outcome.StatusCode,
                    failureKind = outcome.FailureKind?.ToString(),
                    error = outcome.Error,
                    sourceUrls = evidence.Sources,
                    sourceCount = evidence.Count,
                }, EvidenceJson),
                RecommendationKey: null,
                IsSuppressed: false));
        }

        return (targets.Count, broken);
    }

    private async Task<ExternalProbeOutcome> ProbeExternalAsync(string targetUrl, BrokenLinkOptions options, CancellationToken cancellationToken)
    {
        try
        {
            HttpFetchResult result = await _fetcher
                .FetchAsync(new HttpFetchRequest(targetUrl, TimeoutSeconds: options.ExternalTimeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
            return result.StatusCode >= 400
                ? new ExternalProbeOutcome(true, result.StatusCode, null, $"HTTP {result.StatusCode}")
                : new ExternalProbeOutcome(false, result.StatusCode, null, null);
        }
        catch (FetchFailedException ex)
        {
            return new ExternalProbeOutcome(true, null, ex.Kind, ex.Message);
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record ErrorTarget(string CanonicalUrl, CrawlStatusClass StatusClass, int? StatusCode);

    private sealed record ExternalProbeOutcome(bool IsBroken, int? StatusCode, FetchFailureKind? FailureKind, string? Error);

    private sealed class SourceEvidence
    {
        private readonly int _maxSources;

        public SourceEvidence(int maxSources)
        {
            _maxSources = maxSources;
        }

        public List<string> Sources { get; } = new();

        public long Count { get; private set; }

        public void Add(string sourceUrl)
        {
            Count++;
            if (Sources.Count < _maxSources && !Sources.Contains(sourceUrl, StringComparer.OrdinalIgnoreCase))
            {
                Sources.Add(sourceUrl);
            }
        }
    }
}
