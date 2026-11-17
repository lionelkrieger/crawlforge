using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrawlForge.Tools.Governance;

/// <summary>
/// Approved outbound network-boundary scopes (FS-CRAWL-001 §2.6): user-configured crawl
/// targets (or configured proxy), user-configured export destinations, the user-configured
/// external database, and the optional consented DNS check. Call sites inside these scopes
/// are the approved target classes; everything else must stay loopback.
///
/// The allowlist is name-convention based (contracts are locked separately) and configurable
/// via boundary-allowlist.json or --boundary-config, so later workpieces never need to edit
/// analyzer code to declare a boundary class.
/// </summary>
internal sealed class NetworkBoundaryAllowlist
{
    [JsonPropertyName("namespacePrefixes")]
    public string[] NamespacePrefixes { get; set; } = [];

    [JsonPropertyName("typeNamePatterns")]
    public string[] TypeNamePatterns { get; set; } = [];

    [JsonPropertyName("pathFragments")]
    public string[] PathFragments { get; set; } = [];

    public static NetworkBoundaryAllowlist Default { get; } = new()
    {
        NamespacePrefixes =
        [
            "CrawlForge.Crawler",
            "CrawlForge.Export",
            "CrawlForge.Data.MySql",
            "CrawlForge.Intelligence.LogAnalyzer",
        ],
        TypeNamePatterns =
        [
            "*CrawlTarget*", "*TargetFetcher*", "*Proxy*", "*Destination*", "*ExternalDb*", "*Dns*",
        ],
        PathFragments =
        [
            "crawler/fetching", "crawler/proxy", "crawlforge.export/", "data.mysql/",
            "loganalyzer/", "plugins/",
        ],
    };

    public static NetworkBoundaryAllowlist Load(string? configPath)
    {
        var path = configPath;
        if (string.IsNullOrEmpty(path))
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "boundary-allowlist.json");
            path = File.Exists(candidate) ? candidate : null;
        }

        if (path is null)
        {
            return Default;
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<NetworkBoundaryAllowlist>(json);
            return loaded ?? Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot load boundary allowlist '{path}': {ex.Message}", ex);
        }
    }

    public bool IsApprovedCSharpScope(string namespaceName, string typeName, string relativePath)
    {
        foreach (var prefix in NamespacePrefixes)
        {
            if (namespaceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (namespaceName.Length == prefix.Length || namespaceName[prefix.Length] == '.'))
            {
                return true;
            }
        }

        foreach (var pattern in TypeNamePatterns)
        {
            if (MatchesWildcard(typeName, pattern))
            {
                return true;
            }
        }

        return IsApprovedPath(relativePath);
    }

    public bool IsApprovedTextScope(string relativePath) => IsApprovedPath(relativePath);

    private bool IsApprovedPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').ToLowerInvariant();
        foreach (var fragment in PathFragments)
        {
            if (normalized.Contains(fragment.Replace('\\', '/').ToLowerInvariant(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesWildcard(string value, string pattern)
    {
        if (pattern.Length == 0)
        {
            return false;
        }

        var parts = pattern.Split('*');
        var index = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }

            var found = value.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            if (i == 0 && !pattern.StartsWith('*') && found != 0)
            {
                return false;
            }

            index = found + part.Length;
        }

        return pattern.EndsWith('*') || index == value.Length;
    }
}
