using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Performance;

/// <summary>
/// Rendered Core Web Vitals analyzer (FS-CRAWL-001 §7.11, locked
/// <see cref="ICoreWebVitalsAnalyzer"/>): lab LCP/CLS/INP-proxy per page
/// through the bounded lab probe (instrumented local render, WP-03-06
/// approved design), plus TTFB from the fetch. Offline: measurement targets
/// only the crawl URL. Requires rendering mode; when rendering is off the
/// vitals are null and a requires-rendering notice is emitted. Band findings
/// use the publicly documented CWV thresholds (good/needs-improvement/poor).
/// </summary>
public sealed class CoreWebVitalsAnalyzer : ICoreWebVitalsAnalyzer
{
    private readonly PerformanceAnalyzerOptions _options;
    private readonly ILabVitalsProbe? _probe;
    private readonly LabVitalsSampleCache _cache;

    /// <summary>Creates the analyzer.</summary>
    /// <param name="options">Local options; null uses documented defaults.</param>
    /// <param name="probe">Lab probe; null disables measurement (notice emitted).</param>
    /// <param name="cache">Shared per-page sample cache; null creates a private one.</param>
    public CoreWebVitalsAnalyzer(
        PerformanceAnalyzerOptions? options = null,
        ILabVitalsProbe? probe = null,
        LabVitalsSampleCache? cache = null)
    {
        _options = options ?? new PerformanceAnalyzerOptions();
        _probe = probe;
        _cache = cache ?? new LabVitalsSampleCache();
    }

    /// <inheritdoc />
    public async Task<CoreWebVitalsResult> AnalyzeAsync(
        PageAuditContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ttfb = context.Fetch?.Ttfb.TotalMilliseconds ?? 0.0;
        if (!_options.Enabled)
        {
            return new CoreWebVitalsResult(null, null, null, ttfb, []);
        }

        var renderingEnabled = context.Configuration.RenderingMode != RenderMode.None;
        if (!renderingEnabled)
        {
            return new CoreWebVitalsResult(
                null, null, null, ttfb,
                [PerformanceFindingFactory.RequiresRenderingNotice(context.Url.Id, "core-web-vitals", renderingEnabled)]);
        }

        if (_probe is null)
        {
            return new CoreWebVitalsResult(
                null, null, null, ttfb,
                [PerformanceFindingFactory.ProbeUnavailableNotice(context.Url.Id, "core-web-vitals", "no lab probe is configured.")]);
        }

        LabVitalsSample sample;
        try
        {
            sample = await _cache.GetOrCollectAsync(
                context.AuditId,
                context.Url.Id,
                () => _probe.CollectAsync(
                    new LabVitalsRequest(context.Url.CanonicalUrl, _options.Device, _options.LabTimeoutSeconds),
                    cancellationToken)).ConfigureAwait(false);
        }
        catch (LabProbeException ex)
        {
            return new CoreWebVitalsResult(
                null, null, null, ttfb,
                [PerformanceFindingFactory.ProbeUnavailableNotice(context.Url.Id, "core-web-vitals", $"{ex.Kind}: {ex.Message}")]);
        }

        var findings = new List<FindingDraft>();
        AddBandFinding(findings, context.Url.Id, PerformanceRuleKeys.Lcp, PerformanceMetricKeys.Lcp,
            sample.LcpMilliseconds, _options.LcpGoodMilliseconds, _options.LcpPoorMilliseconds, "milliseconds");
        AddBandFinding(findings, context.Url.Id, PerformanceRuleKeys.Cls, PerformanceMetricKeys.Cls,
            sample.Cls, _options.ClsGood, _options.ClsPoor, "score");
        AddBandFinding(findings, context.Url.Id, PerformanceRuleKeys.Inp, PerformanceMetricKeys.InpProxy,
            sample.InpProxyMilliseconds, _options.InpGoodMilliseconds, _options.InpPoorMilliseconds, "milliseconds (lab proxy)");

        return new CoreWebVitalsResult(
            sample.LcpMilliseconds,
            sample.Cls,
            sample.InpProxyMilliseconds,
            ttfb,
            findings);
    }

    private static void AddBandFinding(
        List<FindingDraft> findings,
        long urlId,
        string ruleKey,
        string metricKey,
        double? value,
        double goodThreshold,
        double poorThreshold,
        string unit)
    {
        if (value is null)
        {
            return;
        }

        string band;
        FindingSeverity severity;
        if (value <= goodThreshold)
        {
            band = "good";
            severity = FindingSeverity.Pass;
        }
        else if (value > poorThreshold)
        {
            band = "poor";
            severity = FindingSeverity.Warning;
        }
        else
        {
            band = "needs-improvement";
            severity = FindingSeverity.Notice;
        }

        findings.Add(PerformanceFindingFactory.Create(
            ruleKey,
            severity,
            urlId,
            new PerformanceEvidence(
                ruleKey,
                metricKey,
                value,
                band == "good" ? goodThreshold : poorThreshold,
                band,
                $"{metricKey} is {value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} {unit} ({band}).",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["unit"] = unit,
                    ["goodThreshold"] = goodThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["poorThreshold"] = poorThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })));
    }
}
