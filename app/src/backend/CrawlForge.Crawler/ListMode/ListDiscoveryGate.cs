using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// List Mode discovery gate (FS-CRAWL-002 §10.2, criterion #43). The crawl
/// engine consults the gate before enqueueing any discovered URL:
/// <list type="bullet">
/// <item>list-only: link discovery and sitemap expansion are disabled — every
/// non-seed discovery is skipped with reason <c>discovery-disabled</c>;</item>
/// <item>list-bounded: discovery runs, but only URLs present in the uploaded
/// list (after canonicalization) are enqueued; off-list discoveries are
/// recorded as skipped with reason <c>out-of-list</c>.</item>
/// </list>
/// Seeds and list entries always pass. Skip diagnostics are bounded and local.
/// </summary>
public sealed class ListDiscoveryGate
{
    /// <summary>Skip reason for off-list discoveries in list-bounded mode (§10.2).</summary>
    public const string OutOfListReason = "out-of-list";

    /// <summary>Skip reason when discovery is disabled (list-only mode).</summary>
    public const string DiscoveryDisabledReason = "discovery-disabled";

    /// <summary>Skip reason when the depth ceiling is exceeded.</summary>
    public const string DepthCeilingReason = "depth-ceiling";

    /// <summary>Maximum retained skip diagnostics; further skips are counted only.</summary>
    public const int MaxSkipDiagnostics = 1000;

    private readonly ListModePolicy _policy;
    private readonly List<DiscoverySkip> _skips = [];

    /// <summary>Creates the gate over a resolved per-audit policy.</summary>
    /// <param name="policy">The resolved List Mode policy.</param>
    public ListDiscoveryGate(ListModePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <summary>Total number of skipped discoveries (including beyond the diagnostic bound).</summary>
    public int SkipCount { get; private set; }

    /// <summary>Number of skips with reason <c>out-of-list</c>.</summary>
    public int OutOfListSkipCount { get; private set; }

    /// <summary>Bounded skip diagnostics in discovery order.</summary>
    public IReadOnlyList<DiscoverySkip> Skips => _skips;

    /// <summary>
    /// Decides whether a discovered URL may enter the frontier. Seed and list
    /// entries pass unconditionally; link/sitemap/redirect discoveries are
    /// gated by the resolved mode, depth ceiling, and list membership.
    /// </summary>
    /// <param name="entry">The discovered frontier entry (canonical URL).</param>
    /// <returns>The enqueue/skip decision; skips carry a machine-readable reason.</returns>
    public DiscoveryDecision Decide(FrontierEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.DiscoverySource is DiscoverySource.Seed or DiscoverySource.List)
        {
            return DiscoveryDecision.Allowed;
        }

        if (!_policy.DiscoveryEnabled || _policy.Mode == ListDiscoveryMode.ListOnly)
        {
            return RecordSkip(entry, DiscoveryDisabledReason);
        }

        if (entry.Depth > _policy.DepthCeiling)
        {
            return RecordSkip(entry, DepthCeilingReason);
        }

        if (_policy.Mode == ListDiscoveryMode.ListBounded &&
            !_policy.AllowedCanonicalUrls.Contains(entry.Url))
        {
            return RecordSkip(entry, OutOfListReason);
        }

        return DiscoveryDecision.Allowed;
    }

    private DiscoveryDecision RecordSkip(FrontierEntry entry, string reason)
    {
        SkipCount++;
        if (string.Equals(reason, OutOfListReason, StringComparison.Ordinal))
        {
            OutOfListSkipCount++;
        }

        if (_skips.Count < MaxSkipDiagnostics)
        {
            _skips.Add(new DiscoverySkip(entry.Url, entry.DiscoverySource, reason));
        }

        return DiscoveryDecision.Skip(reason);
    }
}
