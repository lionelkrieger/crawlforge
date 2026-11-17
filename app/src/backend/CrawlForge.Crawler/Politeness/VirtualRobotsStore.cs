using System.Collections.Concurrent;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// User-local virtual robots.txt overrides (FS-CRAWL-001 v1.4): a per-host
/// robots body supplied by the user that replaces the fetched document for
/// allow/disallow and crawl-delay decisions. Virtual robots affect politeness
/// only — they never alter limits resolution (FS-CRAWL-001 §4 stays independent).
/// All state is local; nothing is uploaded or shared.
/// </summary>
public sealed class VirtualRobotsStore
{
    private readonly ConcurrentDictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets (or replaces) the virtual robots body for a host.</summary>
    /// <param name="host">Target host.</param>
    /// <param name="robotsBody">The virtual robots.txt content.</param>
    public void Set(string host, string robotsBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(robotsBody);
        _overrides[Normalize(host)] = robotsBody;
    }

    /// <summary>Removes the virtual robots override for a host.</summary>
    /// <param name="host">Target host.</param>
    /// <returns>True when an override was removed.</returns>
    public bool Clear(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return _overrides.TryRemove(Normalize(host), out _);
    }

    /// <summary>Reads the virtual robots body for a host, when present.</summary>
    /// <param name="host">Target host.</param>
    /// <param name="robotsBody">The virtual body, when an override exists.</param>
    /// <returns>True when an override exists.</returns>
    public bool TryGet(string host, out string? robotsBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return _overrides.TryGetValue(Normalize(host), out robotsBody);
    }

    private static string Normalize(string host) => host.Trim();
}
