using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Canonicalization;

/// <summary>
/// User-agent comparison configuration (FS-CRAWL-001 §5.15). Off by default;
/// when enabled a sample of URLs (default 50, configurable, or all) is fetched
/// both as Googlebot Smartphone and as a standard Chrome browser.
/// </summary>
public sealed record UserAgentComparisonOptions
{
    /// <summary>Default sample size (§5.15).</summary>
    public const int DefaultSampleSize = 50;

    /// <summary>Whether the comparison runs (default off).</summary>
    public bool Enabled { get; init; }

    /// <summary>Sample size; null means all URLs.</summary>
    public int? SampleSize { get; init; } = DefaultSampleSize;

    /// <summary>Validates the configuration (§4.3).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The sample size is not positive.</exception>
    public void Validate()
    {
        if (SampleSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleSize), SampleSize, "The comparison sample size must be positive or null (all URLs).");
        }
    }
}

/// <summary>
/// One observed fetch of a URL under one identity (§5.15 comparison inputs):
/// status, redirects, meta robots, canonical, content hash and links.
/// </summary>
/// <param name="Url">The sampled canonical URL.</param>
/// <param name="StatusCode">Final HTTP status code.</param>
/// <param name="FinalUrl">Final URL after redirects.</param>
/// <param name="MetaRobotsDirectives">Observed meta-robots directive tokens (lowercase).</param>
/// <param name="CanonicalUrl">Declared canonical target, when present.</param>
/// <param name="ContentHash">Hash of the response body (stable algorithm of the caller).</param>
/// <param name="DiscoveredLinks">Discovered link targets (canonicalized).</param>
public sealed record UaFetchObservation(
    string Url,
    int StatusCode,
    string? FinalUrl,
    IReadOnlyList<string> MetaRobotsDirectives,
    string? CanonicalUrl,
    string ContentHash,
    IReadOnlyList<string> DiscoveredLinks);

/// <summary>One material difference between the two identities.</summary>
/// <param name="Field">Compared field name.</param>
/// <param name="GooglebotValue">Value observed as Googlebot Smartphone.</param>
/// <param name="ChromeValue">Value observed as the Chrome browser.</param>
public sealed record UaDifference(string Field, string? GooglebotValue, string? ChromeValue);

/// <summary>Comparison result for one sampled URL.</summary>
/// <param name="Url">The sampled URL.</param>
/// <param name="Differences">Material differences in deterministic field order.</param>
/// <param name="IsMaterial">True when any difference was observed.</param>
public sealed record UserAgentComparisonResult(
    string Url,
    IReadOnlyList<UaDifference> Differences,
    bool IsMaterial);

/// <summary>
/// Deterministic sampler and comparer for the optional user-agent comparison
/// (§5.15). Sampling is reproducible: URLs are ordered ordinally and the first
/// N are taken (no randomness — the adjustment-sequence determinism discipline
/// of §3.4 applies to every crawl-time decision). Comparison covers status,
/// redirects, meta robots, canonical, content hash and links; any difference
/// is material (possible cloaking, dynamic rendering or bot blocking).
/// </summary>
public static class UserAgentComparisonEvaluator
{
    /// <summary>
    /// Selects the deterministic comparison sample from the audit's URLs.
    /// </summary>
    /// <param name="canonicalUrls">All candidate URLs.</param>
    /// <param name="options">Comparison options.</param>
    /// <returns>The sampled URLs in ordinal order (empty when disabled).</returns>
    public static IReadOnlyList<string> SelectSample(IEnumerable<string> canonicalUrls, UserAgentComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(canonicalUrls);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!options.Enabled)
        {
            return [];
        }

        var ordered = canonicalUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(url => url, StringComparer.Ordinal)
            .ToList();
        return options.SampleSize is { } size ? ordered.Take(size).ToList() : ordered;
    }

    /// <summary>
    /// Compares the two identity observations of one URL. Both observations
    /// must refer to the same URL.
    /// </summary>
    /// <param name="googlebot">Observation fetched as Googlebot Smartphone.</param>
    /// <param name="chrome">Observation fetched as the Chrome browser.</param>
    /// <returns>The comparison result with deterministic difference order.</returns>
    public static UserAgentComparisonResult Compare(UaFetchObservation googlebot, UaFetchObservation chrome)
    {
        ArgumentNullException.ThrowIfNull(googlebot);
        ArgumentNullException.ThrowIfNull(chrome);
        if (!string.Equals(googlebot.Url, chrome.Url, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Both observations must refer to the same URL.", nameof(chrome));
        }

        var differences = new List<UaDifference>();
        if (googlebot.StatusCode != chrome.StatusCode)
        {
            differences.Add(new UaDifference("status", googlebot.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture), chrome.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (!string.Equals(googlebot.FinalUrl, chrome.FinalUrl, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add(new UaDifference("redirect", googlebot.FinalUrl, chrome.FinalUrl));
        }

        var googlebotRobots = string.Join(",", googlebot.MetaRobotsDirectives.OrderBy(d => d, StringComparer.Ordinal));
        var chromeRobots = string.Join(",", chrome.MetaRobotsDirectives.OrderBy(d => d, StringComparer.Ordinal));
        if (!string.Equals(googlebotRobots, chromeRobots, StringComparison.Ordinal))
        {
            differences.Add(new UaDifference("meta-robots", googlebotRobots, chromeRobots));
        }

        if (!string.Equals(googlebot.CanonicalUrl, chrome.CanonicalUrl, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add(new UaDifference("canonical", googlebot.CanonicalUrl, chrome.CanonicalUrl));
        }

        if (!string.Equals(googlebot.ContentHash, chrome.ContentHash, StringComparison.Ordinal))
        {
            differences.Add(new UaDifference("content-hash", googlebot.ContentHash, chrome.ContentHash));
        }

        var googlebotLinks = string.Join("\n", googlebot.DiscoveredLinks.OrderBy(l => l, StringComparer.Ordinal));
        var chromeLinks = string.Join("\n", chrome.DiscoveredLinks.OrderBy(l => l, StringComparer.Ordinal));
        if (!string.Equals(googlebotLinks, chromeLinks, StringComparison.Ordinal))
        {
            differences.Add(new UaDifference("links", googlebotLinks, chromeLinks));
        }

        return new UserAgentComparisonResult(googlebot.Url, differences, differences.Count > 0);
    }
}

/// <summary>
/// Produces the §5.15 comparison finding: material crawler-vs-browser
/// differences are raised as a Warning (possible cloaking, dynamic rendering
/// or bot blocking). Detection and reporting only — never evasion (E3).
/// </summary>
public static class UserAgentComparisonFindingFactory
{
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates the material-difference finding, or null when the comparison
    /// observed no difference.
    /// </summary>
    /// <param name="urlId">URL record of the sampled URL.</param>
    /// <param name="result">The comparison result.</param>
    /// <returns>A Warning finding draft, or null.</returns>
    public static FindingDraft? Create(long urlId, UserAgentComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsMaterial)
        {
            return null;
        }

        var evidence = JsonSerializer.Serialize(new
        {
            url = result.Url,
            differences = result.Differences.Select(d => new { field = d.Field, googlebot = d.GooglebotValue, chrome = d.ChromeValue }),
            interpretation = "possible cloaking, dynamic rendering or bot blocking",
        }, EvidenceOptions);

        return new FindingDraft(
            CrawlFindingRuleKeys.UaComparisonMaterialDifference,
            AuditCategory.Indexability,
            FindingSeverity.Warning,
            urlId,
            evidence,
            RecommendationKey: null);
    }
}
