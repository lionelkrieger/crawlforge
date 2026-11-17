using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CrawlForge.Intelligence.LogAnalyzer;

/// <summary>
/// Purpose classes of the bot catalogue (FS-CRAWL-002 §11.3, shared vocabulary
/// with §11.5 correlation): search bots drive the search-bot-hit counters.
/// </summary>
public static class BotPurposes
{
    /// <summary>Search-index crawlers (Googlebot, Bingbot, Applebot).</summary>
    public const string Search = "search";

    /// <summary>AI search/retrieval crawlers (OAI-SearchBot, PerplexityBot).</summary>
    public const string AiSearch = "ai-search";

    /// <summary>AI training crawlers (GPTBot, ClaudeBot).</summary>
    public const string AiTraining = "ai-training";
}

/// <summary>
/// One parsed CIDR range with an offline containment test (IPv4 and IPv6).
/// </summary>
public sealed class CidrRange
{
    private readonly byte[] _network;
    private readonly byte[] _broadcast;
    private readonly AddressFamily _family;

    /// <summary>Parses a CIDR notation range.</summary>
    /// <param name="cidr">CIDR notation (e.g. 66.249.64.0/19 or 2001:4860:4801::/48).</param>
    /// <exception cref="FormatException">The notation is invalid.</exception>
    public CidrRange(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out var prefix))
        {
            throw new FormatException($"Invalid CIDR notation: '{cidr}'.");
        }

        var bytes = address.GetAddressBytes();
        if (prefix < 0 || prefix > bytes.Length * 8)
        {
            throw new FormatException($"Invalid prefix length in '{cidr}'.");
        }

        _family = address.AddressFamily;
        _network = new byte[bytes.Length];
        _broadcast = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var bit = i * 8;
            var maskBits = Math.Clamp(prefix - bit, 0, 8);
            var mask = (byte)(0xFF << (8 - maskBits));
            _network[i] = (byte)(bytes[i] & mask);
            _broadcast[i] = (byte)(_network[i] | ~mask);
        }

        Notation = cidr;
    }

    /// <summary>The original CIDR notation.</summary>
    public string Notation { get; }

    /// <summary>Tests whether an IP address falls inside the range (offline).</summary>
    /// <param name="address">The address to test.</param>
    /// <returns>True when contained; false for family mismatches.</returns>
    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != _family)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] < _network[i] || bytes[i] > _broadcast[i])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One bot family of the bundled catalogue: UA patterns, purpose class,
/// forward-confirmed rDNS suffixes, and published CIDR ranges.
/// </summary>
public sealed class BotFamilyDefinition
{
    /// <summary>Creates a family definition.</summary>
    /// <param name="key">Stable family key.</param>
    /// <param name="name">Display label.</param>
    /// <param name="purpose">Purpose class (see <see cref="BotPurposes"/>).</param>
    /// <param name="matchOrder">Ascending evaluation order.</param>
    /// <param name="userAgentPatterns">Case-insensitive UA regexes.</param>
    /// <param name="dnsSuffixes">Reverse-DNS zones for verification.</param>
    /// <param name="cidrs">Published CIDR ranges.</param>
    public BotFamilyDefinition(
        string key,
        string name,
        string purpose,
        int matchOrder,
        IReadOnlyList<Regex> userAgentPatterns,
        IReadOnlyList<string> dnsSuffixes,
        IReadOnlyList<CidrRange> cidrs)
    {
        Key = key;
        Name = name;
        Purpose = purpose;
        MatchOrder = matchOrder;
        UserAgentPatterns = userAgentPatterns;
        DnsSuffixes = dnsSuffixes;
        Cidrs = cidrs;
    }

    /// <summary>Stable family key (persisted in breakdowns; never repurposed).</summary>
    public string Key { get; }

    /// <summary>Display label used as BotClassification.Family.</summary>
    public string Name { get; }

    /// <summary>Purpose class: search, ai-search, or ai-training.</summary>
    public string Purpose { get; }

    /// <summary>Ascending evaluation order; first match wins.</summary>
    public int MatchOrder { get; }

    /// <summary>Case-insensitive user-agent regexes (bounded match timeout).</summary>
    public IReadOnlyList<Regex> UserAgentPatterns { get; }

    /// <summary>Vendor-documented reverse-DNS zones.</summary>
    public IReadOnlyList<string> DnsSuffixes { get; }

    /// <summary>Published CIDR ranges for offline verification.</summary>
    public IReadOnlyList<CidrRange> Cidrs { get; }

    /// <summary>Whether the family counts toward search-bot hits (§11.5).</summary>
    public bool IsSearchBot => Purpose == BotPurposes.Search;

    /// <summary>Tests the family's UA patterns against a user-agent string.</summary>
    /// <param name="userAgent">The user-agent string.</param>
    /// <returns>True when any pattern matches (timeouts count as no match).</returns>
    public bool MatchesUserAgent(string userAgent)
    {
        foreach (var pattern in UserAgentPatterns)
        {
            try
            {
                if (pattern.IsMatch(userAgent))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Tolerant posture: a timeout counts as no match.
            }
        }

        return false;
    }

    /// <summary>Tests an IP against the family's bundled CIDR ranges (offline).</summary>
    /// <param name="address">The parsed client address.</param>
    /// <returns>True when any range contains the address.</returns>
    public bool MatchesCidr(IPAddress address) => Cidrs.Any(range => range.Contains(address));
}

/// <summary>
/// Bundled, versioned bot catalogue (FS-CRAWL-002 §11.3, asset pattern §7.15).
/// Loaded exclusively from local disk — no runtime fetch path exists (E8).
/// User-local CIDR additions merge at load time without mutating the asset.
/// </summary>
public sealed class BotCatalogue
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    private readonly IReadOnlyList<BotFamilyDefinition> _families;

    private BotCatalogue(string version, string revision, IReadOnlyList<BotFamilyDefinition> families)
    {
        Version = version;
        Revision = revision;
        _families = families;
    }

    /// <summary>Catalogue version (product-update cadence).</summary>
    public string Version { get; }

    /// <summary>Catalogue revision within the version.</summary>
    public string Revision { get; }

    /// <summary>Families in evaluation order (matchOrder ascending).</summary>
    public IReadOnlyList<BotFamilyDefinition> Families => _families;

    /// <summary>Loads the catalogue from a local JSON asset path.</summary>
    /// <param name="path">Local path to bot-catalogue.json.</param>
    /// <param name="userCidrAdditions">Optional user-local CIDR additions (application_settings).</param>
    /// <returns>The loaded catalogue.</returns>
    /// <exception cref="FileNotFoundException">The asset is missing.</exception>
    /// <exception cref="InvalidDataException">The asset is malformed.</exception>
    public static BotCatalogue Load(string path, IReadOnlyList<UserCidrAddition>? userCidrAdditions = null)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The bundled bot catalogue is missing.", path);
        }

        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<CatalogueDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("The bot catalogue is empty.");

        var families = new List<BotFamilyDefinition>();
        foreach (var family in document.Families ?? [])
        {
            var patterns = (family.UserAgentPatterns ?? [])
                .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout))
                .ToList();
            var cidrs = (family.Cidrs ?? []).Select(c => new CidrRange(c)).ToList();
            foreach (var addition in userCidrAdditions ?? [])
            {
                if (string.Equals(addition.FamilyKey, family.Key, StringComparison.Ordinal))
                {
                    cidrs.Add(new CidrRange(addition.Cidr));
                }
            }

            families.Add(new BotFamilyDefinition(
                family.Key ?? string.Empty,
                family.Name ?? family.Key ?? string.Empty,
                family.Purpose ?? BotPurposes.Search,
                family.MatchOrder,
                patterns,
                family.DnsSuffixes ?? [],
                cidrs));
        }

        families.Sort(static (a, b) => a.MatchOrder.CompareTo(b.MatchOrder));
        return new BotCatalogue(document.Version ?? "unknown", document.Revision ?? "unknown", families);
    }

    /// <summary>
    /// Locates the bundled asset by walking up from the application base directory
    /// (local disk only). Returns null when no bundled asset is found.
    /// </summary>
    /// <returns>The asset path, when found.</returns>
    public static string? FindDefaultAssetPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var relative in new[] { "assets/bot-cidr/bot-catalogue.json", "app/assets/bot-cidr/bot-catalogue.json" })
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Finds the first family whose UA patterns match, in evaluation order.
    /// </summary>
    /// <param name="userAgent">The user-agent string (non-empty).</param>
    /// <returns>The matched family, or null.</returns>
    public BotFamilyDefinition? MatchFamily(string userAgent) =>
        _families.FirstOrDefault(family => family.MatchesUserAgent(userAgent));

    private sealed record CatalogueDocument(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("revision")] string? Revision,
        [property: JsonPropertyName("families")] List<FamilyDocument>? Families);

    private sealed record FamilyDocument(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("purpose")] string? Purpose,
        [property: JsonPropertyName("matchOrder")] int MatchOrder,
        [property: JsonPropertyName("userAgentPatterns")] List<string>? UserAgentPatterns,
        [property: JsonPropertyName("dnsSuffixes")] List<string>? DnsSuffixes,
        [property: JsonPropertyName("cidrs")] List<string>? Cidrs);
}

/// <summary>
/// One user-local CIDR addition (persisted in application_settings JSON;
/// merged at catalogue load time, never written back to the bundled asset).
/// </summary>
/// <param name="FamilyKey">Target family key.</param>
/// <param name="Cidr">CIDR notation to add.</param>
public sealed record UserCidrAddition(string FamilyKey, string Cidr);
