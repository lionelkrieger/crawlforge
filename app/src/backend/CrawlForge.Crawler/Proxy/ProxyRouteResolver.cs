namespace CrawlForge.Crawler.Proxy;

/// <summary>
/// Pure routing decision for one crawl-traffic destination (§5.9): direct, or
/// through the user-configured upstream proxy honoring the per-host bypass list.
/// </summary>
public static class ProxyRouteResolver
{
    /// <summary>
    /// Resolves the route for a target host. With no upstream configured every
    /// destination is direct; bypassed hosts are always direct.
    /// </summary>
    /// <param name="targetHost">Destination host name.</param>
    /// <param name="settings">Local routing settings.</param>
    /// <returns>The route decision.</returns>
    public static ProxyRoute Resolve(string targetHost, ProxyRoutingSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.UpstreamProxyUrl))
        {
            return ProxyRoute.Direct;
        }

        foreach (var pattern in settings.BypassHosts)
        {
            if (HostMatches(pattern, targetHost))
            {
                return ProxyRoute.Direct;
            }
        }

        return ProxyRoute.ViaUpstream;
    }

    /// <summary>
    /// Matches a host against a bypass/blocking pattern: exact, ".suffix"
    /// (matches the apex and all subdomains), or "*.suffix" (subdomains only).
    /// </summary>
    /// <param name="pattern">The host pattern.</param>
    /// <param name="host">The host to test.</param>
    /// <returns>True when the host matches.</returns>
    public static bool HostMatches(string pattern, string host)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var normalizedPattern = pattern.Trim().ToLowerInvariant();
        var normalizedHost = host.Trim().ToLowerInvariant();

        if (normalizedPattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = normalizedPattern[1..]; // ".example.com"
            return normalizedHost.EndsWith(suffix, StringComparison.Ordinal)
                && normalizedHost.Length > suffix.Length;
        }

        if (normalizedPattern.StartsWith('.'))
        {
            return normalizedHost.EndsWith(normalizedPattern, StringComparison.Ordinal)
                || string.Equals(normalizedHost, normalizedPattern[1..], StringComparison.Ordinal);
        }

        return string.Equals(normalizedHost, normalizedPattern, StringComparison.Ordinal);
    }
}

/// <summary>The route decision for one destination.</summary>
public enum ProxyRoute
{
    /// <summary>Connect directly to the crawl target.</summary>
    Direct = 0,

    /// <summary>Connect through the user-configured upstream proxy.</summary>
    ViaUpstream = 1,
}
