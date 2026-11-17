using System.Collections.Concurrent;

namespace CrawlForge.Audit.Performance;

/// <summary>
/// Per-page lab sample cache shared by the performance analyzers within one
/// audit run: the Core Web Vitals analyzer, budget evaluator, and coverage
/// analyzer measure a page at most once. Local, in-memory, bounded by the
/// audit's page count; cleared by the composition root between audits.
/// </summary>
public sealed class LabVitalsSampleCache
{
    private readonly ConcurrentDictionary<(long AuditId, long UrlId), Lazy<Task<LabVitalsSample>>> _samples = new();

    /// <summary>
    /// Returns the cached sample for a page, collecting it exactly once via
    /// the supplied factory when absent. Concurrent callers share one
    /// measurement.
    /// </summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="urlId">Owning URL identifier.</param>
    /// <param name="factory">Measurement factory (probe collection).</param>
    /// <returns>The shared lab sample task.</returns>
    public Task<LabVitalsSample> GetOrCollectAsync(
        long auditId,
        long urlId,
        Func<Task<LabVitalsSample>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var lazy = _samples.GetOrAdd(
            (auditId, urlId),
            _ => new Lazy<Task<LabVitalsSample>>(factory, LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    /// <summary>Removes all cached samples (between audits).</summary>
    public void Clear() => _samples.Clear();
}
