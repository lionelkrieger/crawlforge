using System.Text.Json;
using System.Text.RegularExpressions;
using CrawlForge.Crawler.Politeness;

namespace CrawlForge.Crawler.Canonicalization;

/// <summary>
/// Effective per-audit crawl-scope configuration assembled from the stored
/// configuration payload: canonicalization options, scope rules, directive
/// toggles, resource toggles, cookie jar, user agent, UA comparison, custom
/// headers and user cookies. Every member defaults to the specified baseline,
/// so an absent payload reproduces the parent behavior exactly.
/// </summary>
/// <param name="Canonicalization">Canonicalization options (§5.2).</param>
/// <param name="Scope">Inclusion/exclusion scope options (§5.1/§5.13).</param>
/// <param name="Directives">Directive-handling toggles (§5.12).</param>
/// <param name="Resources">Resource fetch toggles (§5.13).</param>
/// <param name="CookieJar">Cookie jar toggle (§5.11).</param>
/// <param name="UserAgent">User-agent configuration (§4.4/§5.15).</param>
/// <param name="UserAgentComparison">UA comparison configuration (§5.15).</param>
/// <param name="Headers">Custom request headers (§5.1).</param>
/// <param name="Cookies">User-supplied cookies (§5.1).</param>
public sealed record CrawlScopeConfig(
    CanonicalizationOptions Canonicalization,
    CrawlScopeOptions Scope,
    DirectiveHandlingOptions Directives,
    ResourceScopeOptions Resources,
    CookieJarOptions CookieJar,
    UserAgentOptions UserAgent,
    UserAgentComparisonOptions UserAgentComparison,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Cookies)
{
    /// <summary>The baseline configuration (no payload present).</summary>
    public static CrawlScopeConfig Default { get; } = new(
        new CanonicalizationOptions(),
        new CrawlScopeOptions(),
        new DirectiveHandlingOptions(),
        new ResourceScopeOptions(),
        new CookieJarOptions(),
        new UserAgentOptions(),
        new UserAgentComparisonOptions(),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.Ordinal));
}

/// <summary>
/// Result of parsing a crawl-scope configuration payload. Parsing is total
/// for value kinds (invalid values fall back to the baseline with a note, the
/// AimdSettingsParser discipline); invalid regex patterns are hard errors —
/// the configuration must be rejected at config time (§4.3/§10.4), never
/// silently defaulted.
/// </summary>
/// <param name="Config">The assembled configuration (baseline on total failure).</param>
/// <param name="Fallbacks">Notes for values that were invalid and defaulted.</param>
/// <param name="Errors">Hard validation errors; non-empty means reject the configuration.</param>
public sealed record CrawlScopeConfigParseResult(
    CrawlScopeConfig Config,
    IReadOnlyList<string> Fallbacks,
    IReadOnlyList<string> Errors);

/// <summary>
/// Parses the per-audit crawl-scope JSON payload into the local options
/// records. The payload rides the existing configuration storage columns
/// (proposed home: <c>crawl_configurations.scope_rules_json</c>; see the
/// WP-09-02 wiring handoff for the exact REST/storage mapping). Shape
/// (camelCase, all members optional):
/// <code>
/// {
///   "canonicalization": { "queryPolicy": "keepAll|keepWhitelist|stripAll",
///                         "queryWhitelist": ["page"], "trailingSlash": "preserve|append|strip",
///                         "stripCookielessSessionSegments": true,
///                         "additionalSessionParameters": ["sid"] },
///   "scope": { "mode": "hostOnly|includeSubdomains|wholeDomain",
///              "includePatterns": ["..."], "excludePatterns": ["..."],
///              "pathPrefixes": ["/docs/"], "assetDomains": ["cdn.example.com"],
///              "checkExternalLinks": false },
///   "directives": { "followPageNofollow": false, "followLinkNofollow": false,
///                   "crawlNoindexPages": true, "useCanonicalDiscovery": true,
///                   "useHreflangAndPaginationDiscovery": true,
///                   "followMetaRefresh": true, "followIframes": false },
///   "resources": { "fetchScripts": false, "fetchStylesheets": false,
///                  "fetchImages": false, "fetchFonts": false, "fetchOtherFiles": false },
///   "cookieJar": { "enabled": true },
///   "userAgent": { "custom": null, "simulateGooglebot": false },
///   "userAgentComparison": { "enabled": false, "sampleSize": 50 },
///   "headers": { "X-Token": "..." },
///   "cookies": { "session": "..." }
/// }
/// </code>
/// </summary>
public static class CrawlScopeConfigParser
{
    /// <summary>Parses a stored payload; null/empty yields the baseline.</summary>
    /// <param name="json">Raw stored payload.</param>
    /// <returns>The configuration plus fallback notes and hard errors.</returns>
    public static CrawlScopeConfigParseResult Parse(string? json)
    {
        var fallbacks = new List<string>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CrawlScopeConfigParseResult(CrawlScopeConfig.Default, fallbacks, errors);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            fallbacks.Add($"crawl-scope payload is not valid JSON ({ex.Message}); using baseline defaults");
            return new CrawlScopeConfigParseResult(CrawlScopeConfig.Default, fallbacks, errors);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                fallbacks.Add("crawl-scope payload is not a JSON object; using baseline defaults");
                return new CrawlScopeConfigParseResult(CrawlScopeConfig.Default, fallbacks, errors);
            }

            var canonicalization = ParseCanonicalization(root, fallbacks);
            var scope = ParseScope(root, fallbacks, errors);
            var directives = ParseDirectives(root, fallbacks);
            var resources = ParseResources(root, fallbacks);
            var cookieJar = ParseCookieJar(root, fallbacks);
            var userAgent = ParseUserAgent(root, fallbacks);
            var comparison = ParseComparison(root, fallbacks, errors);
            var headers = ParseStringMap(root, "headers", fallbacks, StringComparer.OrdinalIgnoreCase);
            var cookies = ParseStringMap(root, "cookies", fallbacks, StringComparer.Ordinal);

            var config = new CrawlScopeConfig(
                canonicalization, scope, directives, resources, cookieJar, userAgent, comparison, headers, cookies);
            return new CrawlScopeConfigParseResult(config, fallbacks, errors);
        }
    }

    private static CanonicalizationOptions ParseCanonicalization(JsonElement root, List<string> fallbacks)
    {
        var defaults = new CanonicalizationOptions();
        if (!TryGetObject(root, "canonicalization", out var obj))
        {
            return defaults;
        }

        var queryPolicy = defaults.QueryPolicy;
        if (TryGetString(obj, "queryPolicy", out var policyText))
        {
            queryPolicy = policyText!.ToLowerInvariant() switch
            {
                "keepall" => QueryParameterPolicy.KeepAll,
                "keepwhitelist" => QueryParameterPolicy.KeepWhitelist,
                "stripall" => QueryParameterPolicy.StripAll,
                _ => Fallback(fallbacks, $"canonicalization.queryPolicy '{policyText}' is invalid; using keepAll", defaults.QueryPolicy)
            };
        }

        var trailingSlash = defaults.TrailingSlash;
        if (TryGetString(obj, "trailingSlash", out var slashText))
        {
            trailingSlash = slashText!.ToLowerInvariant() switch
            {
                "preserve" => TrailingSlashPolicy.Preserve,
                "append" => TrailingSlashPolicy.Append,
                "strip" => TrailingSlashPolicy.Strip,
                _ => Fallback(fallbacks, $"canonicalization.trailingSlash '{slashText}' is invalid; using preserve", defaults.TrailingSlash)
            };
        }

        return new CanonicalizationOptions
        {
            QueryPolicy = queryPolicy,
            QueryWhitelist = ParseStringArray(obj, "queryWhitelist", fallbacks),
            TrailingSlash = trailingSlash,
            StripCookielessSessionSegments = ParseBool(obj, "stripCookielessSessionSegments", defaults.StripCookielessSessionSegments, fallbacks),
            AdditionalSessionParameters = ParseStringArray(obj, "additionalSessionParameters", fallbacks)
        };
    }

    private static CrawlScopeOptions ParseScope(JsonElement root, List<string> fallbacks, List<string> errors)
    {
        var defaults = new CrawlScopeOptions();
        if (!TryGetObject(root, "scope", out var obj))
        {
            return defaults;
        }

        var mode = defaults.Mode;
        if (TryGetString(obj, "mode", out var modeText))
        {
            mode = modeText!.ToLowerInvariant() switch
            {
                "hostonly" => CrawlScopeMode.HostOnly,
                "includesubdomains" => CrawlScopeMode.IncludeSubdomains,
                "wholedomain" => CrawlScopeMode.WholeDomain,
                _ => Fallback(fallbacks, $"scope.mode '{modeText}' is invalid; using hostOnly", defaults.Mode)
            };
        }

        var include = ParseStringArray(obj, "includePatterns", fallbacks);
        var exclude = ParseStringArray(obj, "excludePatterns", fallbacks);
        ValidatePatterns(include, "scope.includePatterns", errors);
        ValidatePatterns(exclude, "scope.excludePatterns", errors);

        return new CrawlScopeOptions
        {
            Mode = mode,
            IncludePatterns = include,
            ExcludePatterns = exclude,
            PathPrefixes = ParseStringArray(obj, "pathPrefixes", fallbacks),
            AssetDomains = ParseStringArray(obj, "assetDomains", fallbacks),
            CheckExternalLinks = ParseBool(obj, "checkExternalLinks", defaults.CheckExternalLinks, fallbacks)
        };
    }

    private static DirectiveHandlingOptions ParseDirectives(JsonElement root, List<string> fallbacks)
    {
        var defaults = new DirectiveHandlingOptions();
        if (!TryGetObject(root, "directives", out var obj))
        {
            return defaults;
        }

        return new DirectiveHandlingOptions
        {
            FollowPageNofollow = ParseBool(obj, "followPageNofollow", defaults.FollowPageNofollow, fallbacks),
            FollowLinkNofollow = ParseBool(obj, "followLinkNofollow", defaults.FollowLinkNofollow, fallbacks),
            CrawlNoindexPages = ParseBool(obj, "crawlNoindexPages", defaults.CrawlNoindexPages, fallbacks),
            UseCanonicalDiscovery = ParseBool(obj, "useCanonicalDiscovery", defaults.UseCanonicalDiscovery, fallbacks),
            UseHreflangAndPaginationDiscovery = ParseBool(obj, "useHreflangAndPaginationDiscovery", defaults.UseHreflangAndPaginationDiscovery, fallbacks),
            FollowMetaRefresh = ParseBool(obj, "followMetaRefresh", defaults.FollowMetaRefresh, fallbacks),
            FollowIframes = ParseBool(obj, "followIframes", defaults.FollowIframes, fallbacks)
        };
    }

    private static ResourceScopeOptions ParseResources(JsonElement root, List<string> fallbacks)
    {
        var defaults = new ResourceScopeOptions();
        if (!TryGetObject(root, "resources", out var obj))
        {
            return defaults;
        }

        return new ResourceScopeOptions
        {
            FetchScripts = ParseBool(obj, "fetchScripts", defaults.FetchScripts, fallbacks),
            FetchStylesheets = ParseBool(obj, "fetchStylesheets", defaults.FetchStylesheets, fallbacks),
            FetchImages = ParseBool(obj, "fetchImages", defaults.FetchImages, fallbacks),
            FetchFonts = ParseBool(obj, "fetchFonts", defaults.FetchFonts, fallbacks),
            FetchOtherFiles = ParseBool(obj, "fetchOtherFiles", defaults.FetchOtherFiles, fallbacks)
        };
    }

    private static CookieJarOptions ParseCookieJar(JsonElement root, List<string> fallbacks)
    {
        var defaults = new CookieJarOptions();
        if (!TryGetObject(root, "cookieJar", out var obj))
        {
            return defaults;
        }

        return new CookieJarOptions
        {
            Enabled = ParseBool(obj, "enabled", defaults.Enabled, fallbacks)
        };
    }

    private static UserAgentOptions ParseUserAgent(JsonElement root, List<string> fallbacks)
    {
        var defaults = new UserAgentOptions();
        if (!TryGetObject(root, "userAgent", out var obj))
        {
            return defaults;
        }

        string? custom = null;
        if (TryGetProperty(obj, "custom", out var customElement) && customElement.ValueKind != JsonValueKind.Null)
        {
            if (customElement.ValueKind == JsonValueKind.String)
            {
                custom = customElement.GetString();
            }
            else
            {
                fallbacks.Add("userAgent.custom is not a string; using the resolved default identity");
            }
        }

        return new UserAgentOptions
        {
            CustomUserAgent = custom,
            SimulateGooglebot = ParseBool(obj, "simulateGooglebot", defaults.SimulateGooglebot, fallbacks)
        };
    }

    private static UserAgentComparisonOptions ParseComparison(JsonElement root, List<string> fallbacks, List<string> errors)
    {
        var defaults = new UserAgentComparisonOptions();
        if (!TryGetObject(root, "userAgentComparison", out var obj))
        {
            return defaults;
        }

        int? sampleSize = defaults.SampleSize;
        if (TryGetProperty(obj, "sampleSize", out var sizeElement))
        {
            if (sizeElement.ValueKind == JsonValueKind.Null)
            {
                // Explicit null means "all URLs" (§5.15: sample 50 or all).
                sampleSize = null;
            }
            else if (sizeElement.ValueKind == JsonValueKind.Number && sizeElement.TryGetInt32(out var size))
            {
                if (size > 0)
                {
                    sampleSize = size;
                }
                else
                {
                    errors.Add($"userAgentComparison.sampleSize must be positive or null (all URLs); got {size}");
                }
            }
            else
            {
                fallbacks.Add("userAgentComparison.sampleSize is invalid; using 50");
            }
        }

        return new UserAgentComparisonOptions
        {
            Enabled = ParseBool(obj, "enabled", defaults.Enabled, fallbacks),
            SampleSize = sampleSize
        };
    }

    private static IReadOnlyDictionary<string, string> ParseStringMap(
        JsonElement root, string name, List<string> fallbacks, StringComparer comparer)
    {
        var result = new Dictionary<string, string>(comparer);
        if (!TryGetObject(root, name, out var obj))
        {
            return result;
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            }
            else
            {
                fallbacks.Add($"{name}.{property.Name} is not a string; ignored");
            }
        }

        return result;
    }

    private static void ValidatePatterns(IReadOnlyList<string> patterns, string label, List<string> errors)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(CrawlScopeOptions.DefaultRegexTimeoutMs));
            }
            catch (ArgumentException ex)
            {
                errors.Add($"{label} contains an invalid regex '{pattern}': {ex.Message}");
            }
        }
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement obj, string name, List<string> fallbacks)
    {
        if (!TryGetProperty(obj, name, out var element))
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            fallbacks.Add($"{name} is not an array; ignored");
            return [];
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
            {
                result.Add(value);
            }
            else
            {
                fallbacks.Add($"{name} contains a non-string entry; ignored");
            }
        }

        return result;
    }

    private static bool ParseBool(JsonElement obj, string name, bool fallback, List<string> fallbacks)
    {
        if (!TryGetProperty(obj, name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        fallbacks.Add($"{name} is not a boolean; using {fallback}");
        return fallback;
    }

    private static T Fallback<T>(List<string> fallbacks, string note, T value)
    {
        fallbacks.Add(note);
        return value;
    }

    private static bool TryGetObject(JsonElement obj, string name, out JsonElement value)
    {
        if (TryGetProperty(obj, name, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        if (TryGetProperty(obj, name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
