namespace CrawlForge.Contracts.Api;

/// <summary>
/// Pull-only local metrics exposition (FS-CRAWL-001 §10.5): renders local
/// technical metric families in Prometheus-class text format at GET /metrics.
/// Metrics are never pushed anywhere; no identity, machine, or account
/// dimension may appear in any metric or label (privacy invariant).
/// </summary>
public interface IMetricsExposition
{
    /// <summary>Renders the current metric families as Prometheus-class text.</summary>
    /// <returns>Text exposition payload.</returns>
    string Render();

    /// <summary>Records an observation into a local metric family.</summary>
    /// <param name="metricFamily">Metric family name.</param>
    /// <param name="value">Observed value.</param>
    /// <param name="labels">Optional technical labels (never identity dimensions).</param>
    void Record(string metricFamily, double value, IReadOnlyDictionary<string, string>? labels = null);
}
