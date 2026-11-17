using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Performance;

/// <summary>
/// Performance budget evaluator (FS-CRAWL-001 §7.11, locked
/// <see cref="IPerformanceBudgetEvaluator"/>): user-defined per-metric
/// thresholds stored with the audit configuration; exceedance emits a
/// finding. Byte/request budgets evaluate from the fetch and proxy capture;
/// vital budgets reuse the shared lab sample (measured at most once per
/// page). Null budget fields are not evaluated; unevaluable metrics emit a
/// notice instead of silently passing.
/// </summary>
public sealed class PerformanceBudgetEvaluator : IPerformanceBudgetEvaluator
{
    private readonly PerformanceAnalyzerOptions _options;
    private readonly ILabVitalsProbe? _probe;
    private readonly LabVitalsSampleCache _cache;

    /// <summary>Creates the evaluator.</summary>
    /// <param name="options">Local options; null uses documented defaults.</param>
    /// <param name="probe">Lab probe for vital budgets; null skips vital evaluation.</param>
    /// <param name="cache">Shared per-page sample cache; null creates a private one.</param>
    public PerformanceBudgetEvaluator(
        PerformanceAnalyzerOptions? options = null,
        ILabVitalsProbe? probe = null,
        LabVitalsSampleCache? cache = null)
    {
        _options = options ?? new PerformanceAnalyzerOptions();
        _probe = probe;
        _cache = cache ?? new LabVitalsSampleCache();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FindingDraft>> EvaluateAsync(
        PageAuditContext context,
        PerformanceBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(budget);

        if (!_options.Enabled)
        {
            return [];
        }

        var findings = new List<FindingDraft>();
        var urlId = context.Url.Id;

        // Byte budget from the fetched document body.
        if (budget.MaxTotalBytes is { } maxBytes)
        {
            if (context.Fetch is { } fetch)
            {
                EvaluateThreshold(findings, urlId, PerformanceMetricKeys.TotalBytes,
                    fetch.Body.Length, maxBytes, "bytes");
            }
            else
            {
                findings.Add(NotEvaluated(urlId, PerformanceMetricKeys.TotalBytes, "no fetch result is available."));
            }
        }

        // Request budget from the proxy capture.
        if (budget.MaxRequests is { } maxRequests)
        {
            if (context.CapturedRequests is { } captured)
            {
                EvaluateThreshold(findings, urlId, PerformanceMetricKeys.RequestCount,
                    captured.Count, maxRequests, "requests");
            }
            else
            {
                findings.Add(NotEvaluated(urlId, PerformanceMetricKeys.RequestCount, "no proxy capture is available."));
            }
        }

        // Vital budgets share the lab sample with the CWV/coverage analyzers.
        var vitalBudgets = new (string Metric, double? Budget, Func<LabVitalsSample, double?> Selector)[]
        {
            (PerformanceMetricKeys.Lcp, budget.MaxLcpMilliseconds, sample => sample.LcpMilliseconds),
            (PerformanceMetricKeys.Cls, budget.MaxCls, sample => sample.Cls),
            (PerformanceMetricKeys.InpProxy, budget.MaxInpMilliseconds, sample => sample.InpProxyMilliseconds),
        };

        if (vitalBudgets.Any(entry => entry.Budget is not null))
        {
            var renderingEnabled = context.Configuration.RenderingMode != RenderMode.None;
            LabVitalsSample? sample = null;
            string? unavailableReason = null;
            if (!renderingEnabled)
            {
                unavailableReason = "rendering mode is disabled for this audit.";
            }
            else if (_probe is null)
            {
                unavailableReason = "no lab probe is configured.";
            }
            else
            {
                try
                {
                    sample = await _cache.GetOrCollectAsync(
                        context.AuditId,
                        urlId,
                        () => _probe.CollectAsync(
                            new LabVitalsRequest(context.Url.CanonicalUrl, _options.Device, _options.LabTimeoutSeconds),
                            cancellationToken)).ConfigureAwait(false);
                }
                catch (LabProbeException ex)
                {
                    unavailableReason = $"{ex.Kind}: {ex.Message}";
                }
            }

            foreach (var (metric, budgetValue, selector) in vitalBudgets)
            {
                if (budgetValue is null)
                {
                    continue;
                }

                if (sample is null)
                {
                    findings.Add(NotEvaluated(urlId, metric, unavailableReason ?? "measurement unavailable."));
                    continue;
                }

                var value = selector(sample);
                if (value is null)
                {
                    findings.Add(NotEvaluated(urlId, metric, "the metric could not be measured for this page."));
                    continue;
                }

                EvaluateThreshold(findings, urlId, metric, value.Value, budgetValue.Value,
                    metric == PerformanceMetricKeys.Cls ? "score" : "milliseconds");
            }
        }

        return findings;
    }

    private static void EvaluateThreshold(
        List<FindingDraft> findings,
        long urlId,
        string metricKey,
        double actual,
        double budget,
        string unit)
    {
        if (actual <= budget)
        {
            return;
        }

        findings.Add(PerformanceFindingFactory.Create(
            PerformanceRuleKeys.BudgetExceeded,
            FindingSeverity.Warning,
            urlId,
            new PerformanceEvidence(
                PerformanceRuleKeys.BudgetExceeded,
                metricKey,
                actual,
                budget,
                Band: null,
                $"Budget exceeded for {metricKey}: {actual.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} "
                    + $"> {budget.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} {unit}.",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["unit"] = unit })));
    }

    private static FindingDraft NotEvaluated(long urlId, string metricKey, string reason) =>
        PerformanceFindingFactory.Create(
            PerformanceRuleKeys.BudgetNotEvaluated,
            FindingSeverity.Notice,
            urlId,
            new PerformanceEvidence(
                PerformanceRuleKeys.BudgetNotEvaluated,
                metricKey,
                Value: null,
                Threshold: null,
                Band: null,
                $"Budget metric {metricKey} was not evaluated: {reason}",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason }));
}
