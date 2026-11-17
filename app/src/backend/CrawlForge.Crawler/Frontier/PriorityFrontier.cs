using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Frontier;

/// <summary>
/// Priority frontier with per-host sub-queues (FS-CRAWL-001 §5.3). Ordering:
/// seeds first, then higher scheduling priority, with depth as the breadth-first
/// tie-breaker and FIFO sequence as the final stable tie-break. Dequeue is
/// politeness-aware: rate decisions are delegated to the locked
/// <see cref="IAimdPolitenessController"/> contract (implemented by the politeness
/// workpiece; injected here, never referenced concretely). Snapshots serialize in
/// dequeue order so checkpoint restore reproduces the exact schedule (§5.7).
/// </summary>
public sealed class PriorityFrontier : IPriorityFrontier
{
    private readonly IAimdPolitenessController _politeness;
    private readonly Dictionary<string, PriorityQueue<QueuedEntry, OrderKey>> _hostQueues = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sequence;

    /// <summary>Creates the frontier.</summary>
    /// <param name="politeness">The AIMD politeness controller owning per-host rate decisions.</param>
    public PriorityFrontier(IAimdPolitenessController politeness)
    {
        _politeness = politeness;
    }

    /// <inheritdoc />
    public long Count
    {
        get
        {
            _gate.Wait();
            try
            {
                long total = 0;
                foreach (var queue in _hostQueues.Values)
                {
                    total += queue.Count;
                }

                return total;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask EnqueueAsync(FrontierEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_hostQueues.TryGetValue(entry.Host, out var queue))
            {
                queue = new PriorityQueue<QueuedEntry, OrderKey>();
                _hostQueues[entry.Host] = queue;
            }

            var key = CreateKey(entry);
            queue.Enqueue(new QueuedEntry(entry, key), key);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<FrontierEntry?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        FrontierEntry? next = null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? bestHost = null;
            OrderKey bestKey = default;
            foreach (var (host, queue) in _hostQueues)
            {
                if (queue.Count == 0)
                {
                    continue;
                }

                var head = queue.Peek();
                if (bestHost is null || head.Key.CompareTo(bestKey) < 0)
                {
                    bestHost = host;
                    bestKey = head.Key;
                }
            }

            if (bestHost is not null)
            {
                next = _hostQueues[bestHost].Dequeue().Entry;
                if (_hostQueues[bestHost].Count == 0)
                {
                    _hostQueues.Remove(bestHost);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (next is null)
        {
            return null;
        }

        // Politeness hook: the controller owns the rate decision (and any delay).
        // Awaited outside the gate so other hosts/operations are not blocked.
        try
        {
            await _politeness.WaitAsync(next.Host, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation during the rate wait must not lose the entry (§5.7):
            // return it to the queue before propagating.
            await EnqueueAsync(next, CancellationToken.None);
            throw;
        }

        return next;
    }

    /// <inheritdoc />
    public FrontierSnapshot CreateSnapshot()
    {
        _gate.Wait();
        try
        {
            var ordered = new List<SnapshotItem>();
            foreach (var queue in _hostQueues.Values)
            {
                foreach (var item in queue.UnorderedItems)
                {
                    ordered.Add(new SnapshotItem(item.Priority, item.Element.Entry));
                }
            }

            ordered.Sort(static (a, b) => a.Key.CompareTo(b.Key));
            return new FrontierSnapshot(ordered.Select(item => item.Entry).ToList());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Restore(FrontierSnapshot snapshot)
    {
        _gate.Wait();
        try
        {
            _hostQueues.Clear();
            _sequence = 0;
            foreach (var entry in snapshot.Entries)
            {
                if (!_hostQueues.TryGetValue(entry.Host, out var queue))
                {
                    queue = new PriorityQueue<QueuedEntry, OrderKey>();
                    _hostQueues[entry.Host] = queue;
                }

                var key = CreateKey(entry);
                queue.Enqueue(new QueuedEntry(entry, key), key);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private OrderKey CreateKey(FrontierEntry entry)
    {
        var seedRank = entry.DiscoverySource is DiscoverySource.Seed or DiscoverySource.List ? 0 : 1;
        return new OrderKey(seedRank, -entry.Priority, entry.Depth, Interlocked.Increment(ref _sequence));
    }

    private sealed record QueuedEntry(FrontierEntry Entry, OrderKey Key);

    private sealed record SnapshotItem(OrderKey Key, FrontierEntry Entry);

    private readonly record struct OrderKey(int SeedRank, double NegatedPriority, int Depth, long Sequence) : IComparable<OrderKey>
    {
        public int CompareTo(OrderKey other)
        {
            var bySeed = SeedRank.CompareTo(other.SeedRank);
            if (bySeed != 0)
            {
                return bySeed;
            }

            var byPriority = NegatedPriority.CompareTo(other.NegatedPriority);
            if (byPriority != 0)
            {
                return byPriority;
            }

            var byDepth = Depth.CompareTo(other.Depth);
            return byDepth != 0 ? byDepth : Sequence.CompareTo(other.Sequence);
        }
    }
}
