using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Performance;

/// <summary>
/// Code coverage measurement plus its findings (the locked interface returns
/// measurements only; the pipeline consumes the detailed surface for
/// finding emission).
/// </summary>
/// <param name="Result">Coverage measurements.</param>
/// <param name="Findings">Coverage findings (unused payloads, notices).</param>
public sealed record CodeCoverageAnalysis(CodeCoverageResult Result, IReadOnlyList<FindingDraft> Findings);

/// <summary>
/// Code coverage analyzer (FS-CRAWL-001 §7.11, locked
/// <see cref="ICodeCoverageAnalyzer"/>): measures the fraction of fetched
/// stylesheet and script bytes actually used during render through the lab
/// probe's CDP coverage, correlated with the proxy-captured request
/// inventory. Large unused payloads emit findings via
/// <see cref="AnalyzeDetailedAsync"/>. Requires rendering; without it the
/// result is zero and a requires-rendering notice is emitted.
/// </summary>
public sealed class CodeCoverageAnalyzer : ICodeCoverageAnalyzer
{
    private const int MaxEvidenceEntries = 25;

    private readonly PerformanceAnalyzerOptions _options;
    private readonly ILabVitalsProbe? _probe;
    private readonly LabVitalsSampleCache _cache;

    /// <summary>Creates the analyzer.</summary>
    /// <param name="options">Local options; null uses documented defaults.</param>
    /// <param name="probe">Lab probe; null disables measurement (notice emitted).</param>
    /// <param name="cache">Shared per-page sample cache; null creates a private one.</param>
    public CodeCoverageAnalyzer(
        PerformanceAnalyzerOptions? options = null,
        ILabVitalsProbe? probe = null,
        LabVitalsSampleCache? cache = null)
    {
        _options = options ?? new PerformanceAnalyzerOptions();
        _probe = probe;
        _cache = cache ?? new LabVitalsSampleCache();
    }

    /// <inheritdoc />
    public async Task<CodeCoverageResult> AnalyzeAsync(
        PageAuditContext context,
        CancellationToken cancellationToken = default)
    {
        var analysis = await AnalyzeDetailedAsync(context, cancellationToken).ConfigureAwait(false);
        return analysis.Result;
    }

    /// <summary>
    /// Measures coverage and produces the associated findings: large unused
    /// payloads, render-dependent skips, and local probe failures.
    /// </summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Measurements and findings.</returns>
    public async Task<CodeCoverageAnalysis> AnalyzeDetailedAsync(
        PageAuditContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var zero = new CodeCoverageResult(0, 0, 0);
        if (!_options.Enabled)
        {
            return new CodeCoverageAnalysis(zero, []);
        }

        var urlId = context.Url.Id;
        var renderingEnabled = context.Configuration.RenderingMode != RenderMode.None;
        if (!renderingEnabled)
        {
            return new CodeCoverageAnalysis(
                zero,
                [PerformanceFindingFactory.RequiresRenderingNotice(urlId, "code-coverage", renderingEnabled)]);
        }

        if (_probe is null)
        {
            return new CodeCoverageAnalysis(
                zero,
                [PerformanceFindingFactory.ProbeUnavailableNotice(urlId, "code-coverage", "no lab probe is configured.")]);
        }

        LabVitalsSample sample;
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
            return new CodeCoverageAnalysis(
                zero,
                [PerformanceFindingFactory.ProbeUnavailableNotice(urlId, "code-coverage", $"{ex.Kind}: {ex.Message}")]);
        }

        if (sample.Coverage is not { } coverage)
        {
            return new CodeCoverageAnalysis(zero, []);
        }

        var used = coverage.JsUsedBytes + coverage.CssUsedBytes;
        var total = coverage.JsTotalBytes + coverage.CssTotalBytes;
        var unusedPercent = total > 0 ? (total - used) * 100.0 / total : 0.0;
        var result = new CodeCoverageResult(used, total, unusedPercent);

        var findings = new List<FindingDraft>();
        if (total >= _options.UnusedCodeMinTotalBytes && unusedPercent >= _options.UnusedCodePercentThreshold)
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["jsUsedBytes"] = coverage.JsUsedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["jsTotalBytes"] = coverage.JsTotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["cssUsedBytes"] = coverage.CssUsedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["cssTotalBytes"] = coverage.CssTotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["capturedCodeRequests"] = CountCapturedCodeRequests(context).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            // Worst unused resources as evidence (bounded list).
            var worst = coverage.Entries
                .OrderByDescending(entry => entry.TotalBytes - entry.UsedBytes)
                .Take(MaxEvidenceEntries)
                .ToList();
            for (var i = 0; i < worst.Count; i++)
            {
                var entry = worst[i];
                details[$"resource[{i}].url"] = entry.Url;
                details[$"resource[{i}].kind"] = entry.Kind;
                details[$"resource[{i}].unusedBytes"] =
                    (entry.TotalBytes - entry.UsedBytes).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            findings.Add(PerformanceFindingFactory.Create(
                PerformanceRuleKeys.UnusedCode,
                FindingSeverity.Warning,
                urlId,
                new PerformanceEvidence(
                    PerformanceRuleKeys.UnusedCode,
                    PerformanceMetricKeys.CoverageUnusedPercent,
                    unusedPercent,
                    _options.UnusedCodePercentThreshold,
                    Band: null,
                    $"Large unused code payload: {unusedPercent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}% "
                        + $"of {total} fetched script/stylesheet bytes were unused during render.",
                    details)));
        }

        return new CodeCoverageAnalysis(result, findings);
    }

    private static int CountCapturedCodeRequests(PageAuditContext context)
    {
        if (context.CapturedRequests is not { } captured)
        {
            return 0;
        }

        var count = 0;
        foreach (var request in captured)
        {
            var contentType = request.ContentType ?? string.Empty;
            if (contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("text/css", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }
}
