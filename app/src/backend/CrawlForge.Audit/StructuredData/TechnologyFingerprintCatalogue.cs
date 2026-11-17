using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrawlForge.Audit.StructuredData;

/// <summary>One header-name/value fingerprint signature.</summary>
/// <param name="Name">Header name (case-insensitive exact match).</param>
/// <param name="Pattern">Regex matched against the header value.</param>
/// <param name="Weight">Confidence weight contributed on match (0–1].</param>
public sealed record HeaderFingerprint(string Name, string Pattern, double Weight);

/// <summary>One cookie-name fingerprint signature.</summary>
/// <param name="Name">Cookie name or name prefix.</param>
/// <param name="IsPrefix">Whether the name is a prefix (e.g. "wordpress_").</param>
/// <param name="Weight">Confidence weight contributed on match (0–1].</param>
public sealed record CookieFingerprint(string Name, bool IsPrefix, double Weight);

/// <summary>One meta-tag fingerprint signature.</summary>
/// <param name="Name">Meta name attribute (case-insensitive exact match).</param>
/// <param name="Pattern">Regex matched against the content attribute.</param>
/// <param name="Weight">Confidence weight contributed on match (0–1].</param>
public sealed record MetaFingerprint(string Name, string Pattern, double Weight);

/// <summary>One regex pattern fingerprint (script src or raw HTML).</summary>
/// <param name="Pattern">Regex pattern (case-insensitive).</param>
/// <param name="Weight">Confidence weight contributed on match (0–1].</param>
public sealed record PatternFingerprint(string Pattern, double Weight);

/// <summary>One technology entry of the bundled fingerprint catalogue.</summary>
/// <param name="Key">Stable technology key (technology_detections.technology_key).</param>
/// <param name="Name">Human-readable technology name.</param>
/// <param name="Category">Technology category (CMS, framework, analytics, CDN, ...).</param>
/// <param name="Headers">Response-header signatures.</param>
/// <param name="Cookies">Cookie-name signatures.</param>
/// <param name="Meta">Meta-tag signatures.</param>
/// <param name="Scripts">Script-src signatures.</param>
/// <param name="Html">Raw-HTML signatures.</param>
public sealed record TechnologyFingerprint(
    string Key,
    string Name,
    string Category,
    IReadOnlyList<HeaderFingerprint> Headers,
    IReadOnlyList<CookieFingerprint> Cookies,
    IReadOnlyList<MetaFingerprint> Meta,
    IReadOnlyList<PatternFingerprint> Scripts,
    IReadOnlyList<PatternFingerprint> Html);

/// <summary>
/// Bundled, versioned technology fingerprint catalogue (FS-CRAWL-001 §7.15,
/// criterion #29). Loaded exclusively from the local packaged asset via
/// <see cref="File.ReadAllText(string)"/> — there is no runtime fetch path and
/// no remote catalogue service (exclusions E2/E4). Updates ship with product
/// updates only. Provenance is documented in the asset README.
/// </summary>
public sealed class TechnologyFingerprintCatalogue
{
    private static readonly JsonSerializerOptions AssetJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private TechnologyFingerprintCatalogue(string version, string revision, IReadOnlyList<TechnologyFingerprint> technologies)
    {
        Version = version;
        Revision = revision;
        Technologies = technologies;
    }

    /// <summary>Catalogue version (product-update cadence).</summary>
    public string Version { get; }

    /// <summary>Catalogue revision within the version.</summary>
    public string Revision { get; }

    /// <summary>All technology entries in asset order.</summary>
    public IReadOnlyList<TechnologyFingerprint> Technologies { get; }

    /// <summary>Loads and validates the bundled catalogue asset from a local path.</summary>
    /// <param name="assetPath">Absolute path of tech-catalogue.json.</param>
    /// <returns>The parsed catalogue.</returns>
    /// <exception cref="FileNotFoundException">The asset is missing.</exception>
    /// <exception cref="InvalidDataException">The asset is malformed.</exception>
    public static TechnologyFingerprintCatalogue Load(string assetPath)
    {
        if (!File.Exists(assetPath))
        {
            throw new FileNotFoundException($"Bundled technology catalogue not found at '{assetPath}'.", assetPath);
        }

        var json = File.ReadAllText(assetPath);
        CatalogueAsset? asset;
        try
        {
            asset = JsonSerializer.Deserialize<CatalogueAsset>(json, AssetJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Technology catalogue asset is malformed: {exception.Message}", exception);
        }

        if (asset is null || string.IsNullOrWhiteSpace(asset.Version) || asset.Technologies is null)
        {
            throw new InvalidDataException("Technology catalogue asset must declare version and technologies.");
        }

        var technologies = new List<TechnologyFingerprint>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in asset.Technologies)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Category))
            {
                throw new InvalidDataException("Every technology entry requires key and category.");
            }

            if (!keys.Add(entry.Key))
            {
                throw new InvalidDataException($"Duplicate technology key '{entry.Key}'.");
            }

            ValidateWeights((entry.Headers ?? []).Select(h => h.Weight)
                .Concat((entry.Cookies ?? []).Select(c => c.Weight))
                .Concat((entry.Meta ?? []).Select(m => m.Weight))
                .Concat((entry.Scripts ?? []).Select(s => s.Weight))
                .Concat((entry.Html ?? []).Select(h => h.Weight)), entry.Key);

            technologies.Add(new TechnologyFingerprint(
                entry.Key,
                entry.Name ?? entry.Key,
                entry.Category,
                entry.Headers ?? [],
                entry.Cookies ?? [],
                entry.Meta ?? [],
                entry.Scripts ?? [],
                entry.Html ?? []));
        }

        return new TechnologyFingerprintCatalogue(asset.Version, asset.Revision ?? "r1", technologies);
    }

    /// <summary>
    /// Resolves the bundled asset path by walking up from the application base
    /// directory to the application root containing <c>assets\tech-fingerprints</c>.
    /// Composition roots may instead pass an explicit packaged path to <see cref="Load"/>.
    /// </summary>
    /// <returns>Absolute path of the bundled tech-catalogue.json.</returns>
    /// <exception cref="FileNotFoundException">No bundled asset found above the base directory.</exception>
    public static string ResolveDefaultAssetPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "tech-fingerprints", "tech-catalogue.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Bundled technology catalogue not found above the application base directory (assets/tech-fingerprints/tech-catalogue.json).");
    }

    private static void ValidateWeights(IEnumerable<double> weights, string key)
    {
        foreach (var weight in weights)
        {
            if (weight is <= 0 or > 1)
            {
                throw new InvalidDataException($"Technology '{key}' has a signature weight outside (0, 1]: {weight}.");
            }
        }
    }

    private sealed record CatalogueAsset
    {
        [JsonRequired]
        public string Version { get; init; } = string.Empty;

        public string? Revision { get; init; }

        [JsonRequired]
        public IReadOnlyList<TechnologyAsset> Technologies { get; init; } = [];
    }

    private sealed record TechnologyAsset
    {
        [JsonRequired]
        public string Key { get; init; } = string.Empty;

        public string? Name { get; init; }

        [JsonRequired]
        public string Category { get; init; } = string.Empty;

        public IReadOnlyList<HeaderFingerprint>? Headers { get; init; }

        public IReadOnlyList<CookieFingerprint>? Cookies { get; init; }

        public IReadOnlyList<MetaFingerprint>? Meta { get; init; }

        public IReadOnlyList<PatternFingerprint>? Scripts { get; init; }

        public IReadOnlyList<PatternFingerprint>? Html { get; init; }
    }
}
