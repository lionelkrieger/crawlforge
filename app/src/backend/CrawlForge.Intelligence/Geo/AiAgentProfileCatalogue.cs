using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Intelligence.Geo;

/// <summary>
/// One bundled AI-agent profile (FS-CRAWL-002 §13.2): agent token, user-agent
/// match patterns, purpose class, vendor, and policy notes.
/// </summary>
/// <param name="AgentToken">Canonical robots.txt token / agent key.</param>
/// <param name="UserAgentPatterns">Case-insensitive substring tokens for User-Agent matching.</param>
/// <param name="PurposeClass">Raw purpose class string from the asset.</param>
/// <param name="Purpose">Mapped purpose dimension.</param>
/// <param name="Vendor">Operating vendor label.</param>
/// <param name="PolicyNotes">Human-readable policy context (advisory surface).</param>
public sealed record AiAgentProfile(
    string AgentToken,
    IReadOnlyList<string> UserAgentPatterns,
    string PurposeClass,
    AiAgentPurpose Purpose,
    string Vendor,
    string PolicyNotes);

/// <summary>
/// Bundled, versioned AI-agent profile catalogue (FS-CRAWL-002 §13.2, the
/// §7.15 asset pattern). Loaded from the local packaged asset only — ZERO
/// runtime fetch and no remote refresh (exclusion E2). The catalogue feeds
/// the ADVISORY governance matrix; it never modifies robots.txt or crawl
/// behavior (criterion #46).
/// </summary>
public sealed class AiAgentProfileCatalogue
{
    private static readonly JsonSerializerOptions AssetOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private AiAgentProfileCatalogue(string version, string revision, IReadOnlyList<AiAgentProfile> profiles)
    {
        Version = version;
        Revision = revision;
        Profiles = profiles;
    }

    /// <summary>Catalogue version (audit snapshot pattern).</summary>
    public string Version { get; }

    /// <summary>Catalogue revision.</summary>
    public string Revision { get; }

    /// <summary>All profiles in asset order.</summary>
    public IReadOnlyList<AiAgentProfile> Profiles { get; }

    /// <summary>Loads and validates the bundled catalogue from a local asset path.</summary>
    /// <param name="assetPath">Path to ai-agent-profiles.json.</param>
    /// <returns>The validated catalogue.</returns>
    /// <exception cref="FileNotFoundException">The asset is absent.</exception>
    /// <exception cref="InvalidDataException">The asset is malformed or violates invariants.</exception>
    public static AiAgentProfileCatalogue Load(string assetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        if (!File.Exists(assetPath))
        {
            throw new FileNotFoundException("Bundled AI-agent profile asset not found.", assetPath);
        }

        var json = File.ReadAllText(assetPath);
        AssetDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<AssetDocument>(json, AssetOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("AI-agent profile asset is not valid JSON.", ex);
        }

        if (document is null || string.IsNullOrWhiteSpace(document.Version) || document.Profiles is null)
        {
            throw new InvalidDataException("AI-agent profile asset requires version and profiles.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new List<AiAgentProfile>(document.Profiles.Count);
        foreach (var entry in document.Profiles)
        {
            if (string.IsNullOrWhiteSpace(entry.AgentToken))
            {
                throw new InvalidDataException("AI-agent profile has an empty agentToken.");
            }

            if (!seen.Add(entry.AgentToken))
            {
                throw new InvalidDataException($"Duplicate agentToken '{entry.AgentToken}'.");
            }

            if (entry.UserAgentPatterns is null || entry.UserAgentPatterns.Count == 0 ||
                entry.UserAgentPatterns.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException($"Agent '{entry.AgentToken}' requires non-empty userAgentPatterns.");
            }

            var purpose = entry.PurposeClass switch
            {
                "search-index" => AiAgentPurpose.Search,
                "ai-rag-citation" => AiAgentPurpose.Rag,
                "llm-training" => AiAgentPurpose.Training,
                _ => throw new InvalidDataException(
                    $"Agent '{entry.AgentToken}' has unknown purposeClass '{entry.PurposeClass}'."),
            };

            profiles.Add(new AiAgentProfile(
                entry.AgentToken,
                entry.UserAgentPatterns,
                entry.PurposeClass!,
                purpose,
                entry.Vendor ?? string.Empty,
                entry.PolicyNotes ?? string.Empty));
        }

        return new AiAgentProfileCatalogue(document.Version, document.Revision ?? "r0", profiles);
    }

    /// <summary>
    /// Resolves the bundled asset path by walking up from the application base
    /// directory to the repository root (the directory containing CrawlForge.sln).
    /// </summary>
    /// <returns>The absolute asset path.</returns>
    /// <exception cref="InvalidOperationException">The repository root cannot be located.</exception>
    public static string ResolveDefaultAssetPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrawlForge.sln")))
            {
                return Path.Combine(directory.FullName, "assets", "ai-agent-profiles", "ai-agent-profiles.json");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate CrawlForge.sln above the application base directory.");
    }

    private sealed class AssetDocument
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("revision")]
        public string? Revision { get; set; }

        [JsonPropertyName("profiles")]
        public List<AssetProfile>? Profiles { get; set; }
    }

    private sealed class AssetProfile
    {
        [JsonPropertyName("agentToken")]
        public string? AgentToken { get; set; }

        [JsonPropertyName("userAgentPatterns")]
        public List<string>? UserAgentPatterns { get; set; }

        [JsonPropertyName("purposeClass")]
        public string? PurposeClass { get; set; }

        [JsonPropertyName("vendor")]
        public string? Vendor { get; set; }

        [JsonPropertyName("policyNotes")]
        public string? PolicyNotes { get; set; }
    }
}
