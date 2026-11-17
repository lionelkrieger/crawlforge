using System.Text.Json;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Writes the audit-completion host-throttle summary into <c>metric_summaries</c>
/// under the additive <c>host-throttle</c> metric key (FS-CRAWL-002 §3.5/§3.6):
/// the value is the total number of rate adjustments across hosts, and the
/// dimensions payload carries the per-host final state. Local persistence only.
/// </summary>
public sealed class AimdMetricSummarizer
{
    private static readonly JsonSerializerOptions DimensionsOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Persists the completion summary of one controller through the locked
    /// metric-summary write repository.
    /// </summary>
    /// <param name="controller">The audit's AIMD controller.</param>
    /// <param name="auditId">Audit reference.</param>
    /// <param name="repository">Locked metric-summary write repository.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted metric summary row.</returns>
    public async Task<MetricSummaryDto> WriteCompletionSummaryAsync(
        AimdPolitenessController controller,
        long auditId,
        IWriteRepository<MetricSummaryDto> repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(repository);

        var checkpoint = controller.CreateCheckpointState();
        var totalAdjustments = 0L;
        var adjustedHosts = 0;
        var hostDimensions = new List<object>(checkpoint.Hosts.Count);
        foreach (var host in checkpoint.Hosts)
        {
            totalAdjustments += host.AdjustmentCount;
            if (host.AdjustmentCount > 0)
            {
                adjustedHosts++;
            }

            hostDimensions.Add(new
            {
                host = host.Host,
                rate = host.CurrentRate,
                concurrency = host.CurrentConcurrency,
                adjustments = host.AdjustmentCount
            });
        }

        var dimensions = JsonSerializer.Serialize(
            new
            {
                hostCount = checkpoint.Hosts.Count,
                adjustedHostCount = adjustedHosts,
                hosts = hostDimensions
            },
            DimensionsOptions);

        var summary = new MetricSummaryDto(
            Id: 0,
            AuditId: auditId,
            MetricKey: MetricKeys.HostThrottle,
            DimensionsJson: dimensions,
            Value: totalAdjustments);
        return await repository.CreateAsync(summary, cancellationToken).ConfigureAwait(false);
    }
}
