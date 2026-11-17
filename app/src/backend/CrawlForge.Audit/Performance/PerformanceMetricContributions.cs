using System.Globalization;
using System.Text.Json;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Performance;

/// <summary>
/// Maps performance measurements onto <see cref="MetricContribution"/> rows
/// for metric_summaries (FS-CRAWL-001 §7.11). Additive public surface on the
/// WP-03-06 implementations: the locked analyzer interfaces return
/// findings/measurements; the pipeline calls these mappers to persist
/// per-page metrics for offline aggregation (phase-05: histograms, worst
/// pages, budget pass-rate). Keys: <see cref="PerformanceMetricKeys"/>.
/// </summary>
public static class PerformanceMetricContributions
{
    private static readonly JsonSerializerOptions DimensionsOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Builds the url-scoped dimensions payload.</summary>
    /// <param name="urlId">Owning URL identifier.</param>
    /// <returns>Serialized dimensions JSON.</returns>
    public static string Dimensions(long urlId) =>
        JsonSerializer.Serialize(new { urlId }, DimensionsOptions);

    /// <summary>Maps Core Web Vitals (LCP/CLS/INP-proxy/TTFB) to metrics.</summary>
    /// <param name="result">Vital measurement result.</param>
    /// <param name="urlId">Owning URL identifier.</param>
    /// <returns>Metric contributions for measured vitals.</returns>
    public static IReadOnlyList<MetricContribution> FromVitals(CoreWebVitalsResult result, long urlId)
    {
        ArgumentNullException.ThrowIfNull(result);
        var dimensions = Dimensions(urlId);
        var metrics = new List<MetricContribution>();
        if (result.LcpMilliseconds is { } lcp)
        {
            metrics.Add(new MetricContribution(PerformanceMetricKeys.Lcp, dimensions, lcp));
        }

        if (result.Cls is { } cls)
        {
            metrics.Add(new MetricContribution(PerformanceMetricKeys.Cls, dimensions, cls));
        }

        if (result.InpMilliseconds is { } inp)
        {
            metrics.Add(new MetricContribution(PerformanceMetricKeys.InpProxy, dimensions, inp));
        }

        metrics.Add(new MetricContribution(PerformanceMetricKeys.Ttfb, dimensions, result.TtfbMilliseconds));
        return metrics;
    }

    /// <summary>Maps the lab-only vitals (FCP/TBT) to metrics.</summary>
    /// <param name="sample">Lab sample.</param>
    /// <param name="urlId">Owning URL identifier.</param>
    /// <returns>Metric contributions for measured lab vitals.</returns>
    public static IReadOnlyList<MetricContribution> FromLabSample(LabVitalsSample sample, long urlId)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var dimensions = Dimensions(urlId);
        var metrics = new List<MetricContribution>();
        if (sample.FcpMilliseconds is { } fcp)
        {
            metrics.Add(new MetricContribution(PerformanceMetricKeys.Fcp, dimensions, fcp));
        }

        if (sample.TbtMilliseconds is { } tbt)
        {
            metrics.Add(new MetricContribution(PerformanceMetricKeys.Tbt, dimensions, tbt));
        }

        return metrics;
    }

    /// <summary>Maps code coverage measurements to metrics.</summary>
    /// <param name="result">Coverage result.</param>
    /// <param name="urlId">Owning URL identifier.</param>
    /// <returns>Used/total/unused-percent contributions.</returns>
    public static IReadOnlyList<MetricContribution> FromCoverage(CodeCoverageResult result, long urlId)
    {
        ArgumentNullException.ThrowIfNull(result);
        var dimensions = Dimensions(urlId);
        return
        [
            new MetricContribution(PerformanceMetricKeys.CoverageUsedBytes, dimensions, result.UsedBytes),
            new MetricContribution(PerformanceMetricKeys.CoverageTotalBytes, dimensions, result.TotalBytes),
            new MetricContribution(PerformanceMetricKeys.CoverageUnusedPercent, dimensions, result.UnusedPercent),
        ];
    }

    /// <summary>Maps page composition measurements to metrics.</summary>
    /// <param name="sample">Composition sample.</param>
    /// <param name="urlId">Owning URL identifier.</param>
    /// <returns>Composition contributions (font size only when measured).</returns>
    public static IReadOnlyList<MetricContribution> FromComposition(PageCompositionSample sample, long urlId)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var dimensions = Dimensions(urlId);
        var metrics = new List<MetricContribution>
        {
            new(PerformanceMetricKeys.DomNodes, dimensions, sample.DomNodes),
            new(PerformanceMetricKeys.DomMaxDepth, dimensions, sample.MaxTreeDepth),
            new(PerformanceMetricKeys.DomMaxWidth, dimensions, sample.MaxTreeWidth),
            new(PerformanceMetricKeys.ImageCount, dimensions, sample.ImageCount),
            new(PerformanceMetricKeys.ImageBytes, dimensions, sample.ImageBytes),
        };

        if (sample.SmallestFontSizePx is { } font)
        {
            metrics.Add(new MetricContribution(PerformanceMetricKeys.MinFontSizePx, dimensions, font));
        }

        return metrics;
    }

    /// <summary>Maps fetch/capture facts (total bytes, request count) to metrics.</summary>
    /// <param name="context">Page audit context.</param>
    /// <returns>Contributions for available facts.</returns>
    public static IReadOnlyList<MetricContribution> FromPageFacts(PageAuditContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var dimensions = Dimensions(context.Url.Id);
        var metrics = new List<MetricContribution>();
        if (context.Fetch is { } fetch)
        {
            metrics.Add(new MetricContribution(PerformanceMetricKeys.TotalBytes, dimensions, fetch.Body.Length));
        }

        if (context.CapturedRequests is { } captured)
        {
            metrics.Add(new MetricContribution(PerformanceMetricKeys.RequestCount, dimensions, captured.Count));
        }

        return metrics;
    }

    /// <summary>Formats a double invariantly for evidence payloads.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Invariant string representation.</returns>
    internal static string Invariant(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
