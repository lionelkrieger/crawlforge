using System.Globalization;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Bounded host-throttle history over the locked generic repositories
/// (FS-CRAWL-002 §3.6): append-only writes, newest-first reads through the
/// (audit, host, timestamp) index, and pruning to the newest N rows per
/// (audit, host). Provider-agnostic — the same code serves the embedded and
/// the external provider.
/// </summary>
public sealed class RepositoryHostThrottleEventStore : IHostThrottleEventStore
{
    private readonly IReadRepository<HostThrottleEventDto> _readRepository;
    private readonly IWriteRepository<HostThrottleEventDto> _writeRepository;

    /// <summary>Creates the store over the locked repository contracts.</summary>
    /// <param name="readRepository">Read side of the host-throttle repository.</param>
    /// <param name="writeRepository">Write side of the host-throttle repository.</param>
    public RepositoryHostThrottleEventStore(
        IReadRepository<HostThrottleEventDto> readRepository,
        IWriteRepository<HostThrottleEventDto> writeRepository)
    {
        ArgumentNullException.ThrowIfNull(readRepository);
        ArgumentNullException.ThrowIfNull(writeRepository);
        _readRepository = readRepository;
        _writeRepository = writeRepository;
    }

    /// <inheritdoc/>
    public async Task AppendAsync(HostThrottleEventDto throttleEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(throttleEvent);
        await _writeRepository.CreateAsync(throttleEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HostThrottleEventDto>> GetHistoryAsync(long auditId, string host, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var query = new ListQuery(
            Offset: 0,
            Limit: Math.Max(1, limit),
            Filters: HostFilters(auditId, host),
            SortBy: nameof(HostThrottleEventDto.Timestamp),
            SortDescending: true);
        var page = await _readRepository.ListAsync(query, cancellationToken).ConfigureAwait(false);
        return page.Items;
    }

    /// <inheritdoc/>
    public async Task TrimAsync(long auditId, string host, int keepLast, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (keepLast < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keepLast), keepLast, "The retained history size must be non-negative.");
        }

        var countQuery = new ListQuery(Offset: 0, Limit: 1, Filters: HostFilters(auditId, host));
        var countPage = await _readRepository.ListAsync(countQuery, cancellationToken).ConfigureAwait(false);
        var excess = countPage.TotalCount - keepLast;
        if (excess <= 0)
        {
            return;
        }

        var oldestQuery = new ListQuery(
            Offset: 0,
            Limit: (int)Math.Min(excess, 10_000),
            Filters: HostFilters(auditId, host),
            SortBy: nameof(HostThrottleEventDto.Timestamp),
            SortDescending: false);
        var oldest = await _readRepository.ListAsync(oldestQuery, cancellationToken).ConfigureAwait(false);
        foreach (var row in oldest.Items)
        {
            await _writeRepository.DeleteAsync(row.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> HostFilters(long auditId, string host) => new()
    {
        [nameof(HostThrottleEventDto.AuditId)] = auditId.ToString(CultureInfo.InvariantCulture),
        [nameof(HostThrottleEventDto.Host)] = host
    };
}
