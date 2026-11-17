namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// Classifies proxy-captured request URLs as analytics/tag hits and extracts
/// measurement parameters (FS-CRAWL-002 §14.1–§14.2): pageview markers,
/// container identifiers, and Consent Mode hit parameters (gcs/gcd).
/// Pure URL analysis over stored capture — no network access.
/// </summary>
internal static class NetworkHitClassifier
{
    private static readonly string[] AnalyticsHitMarkers =
    [
        "google-analytics.com/g/collect",
        "google-analytics.com/j/collect",
        "google-analytics.com/r/collect",
        "google-analytics.com/collect",
        "googletagmanager.com/gtag/js",
        "doubleclick.net/activity",
        "facebook.com/tr",
        "px.ads.linkedin.com",
        "clarity.ms/collect",
        "bat.bing.com/action",
        "googleadservices.com/pagead",
    ];

    /// <summary>Whether the captured URL is an analytics/advertising tag hit.</summary>
    public static bool IsAnalyticsHit(string url) =>
        AnalyticsHitMarkers.Any(marker => url.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the hit is an initial-load pageview: GA4 en=page_view,
    /// Universal Analytics t=pageview, Meta ev=PageView.
    /// </summary>
    public static bool IsPageViewHit(string url)
    {
        if (url.Contains("google-analytics.com/g/collect", StringComparison.OrdinalIgnoreCase)
            || url.Contains("google-analytics.com/j/collect", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(QueryParam(url, "en"), "page_view", StringComparison.OrdinalIgnoreCase);
        }

        if (url.Contains("google-analytics.com/collect", StringComparison.OrdinalIgnoreCase)
            || url.Contains("google-analytics.com/r/collect", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(QueryParam(url, "t"), "pageview", StringComparison.OrdinalIgnoreCase);
        }

        if (url.Contains("facebook.com/tr", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(QueryParam(url, "ev"), "PageView", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Extracts the Consent Mode gcs parameter (e.g. G100/G111).</summary>
    public static string? ExtractGcs(string url) => QueryParam(url, "gcs");

    /// <summary>Extracts the Consent Mode gcd parameter.</summary>
    public static string? ExtractGcd(string url) => QueryParam(url, "gcd");

    /// <summary>
    /// Extracts a container/measurement identifier from a hit URL:
    /// tid=G-…/UA-…, id=GTM-…, or an AW- conversion path segment.
    /// </summary>
    public static string? ContainerIdFromUrl(string url)
    {
        var tid = QueryParam(url, "tid");
        if (!string.IsNullOrEmpty(tid) && (tid.StartsWith("G-", StringComparison.Ordinal)
            || tid.StartsWith("UA-", StringComparison.Ordinal)))
        {
            return tid;
        }

        var id = QueryParam(url, "id");
        if (!string.IsNullOrEmpty(id) && id.StartsWith("GTM-", StringComparison.Ordinal))
        {
            return id;
        }

        var awIndex = url.IndexOf("AW-", StringComparison.Ordinal);
        if (awIndex >= 0)
        {
            var end = awIndex + 3;
            while (end < url.Length && char.IsAsciiDigit(url[end]))
            {
                end++;
            }

            if (end - awIndex >= 9)
            {
                return url[awIndex..end];
            }
        }

        return null;
    }

    /// <summary>Reads one query parameter from an absolute URL (null when absent).</summary>
    public static string? QueryParam(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = uri.Query;
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }
}
