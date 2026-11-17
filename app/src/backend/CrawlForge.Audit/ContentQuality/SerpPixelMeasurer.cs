using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Pixel-width SERP measurer (FS-CRAWL-002 §15.3): width is the sum of glyph
/// advance widths from BUNDLED per-profile font-metric tables, scaled by the
/// profile font size. Truncation follows Google-style ellipsis behavior: the
/// longest prefix that fits the budget minus the ellipsis width, then "…".
/// No runtime fetching, no kerning (sum-of-advances model per spec).
/// </summary>
public sealed class SerpPixelMeasurer : ISerpPixelMeasurer
{
    private const string Ellipsis = "…";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, FontProfile> _profiles;
    private readonly string _fontMetricsDirectory;

    /// <summary>Creates the measurer over a bundled font-metric directory.</summary>
    /// <param name="fontMetricsDirectory">Directory containing the profile JSON tables; null resolves the bundled location.</param>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public SerpPixelMeasurer(string? fontMetricsDirectory = null)
    {
        _fontMetricsDirectory = fontMetricsDirectory ?? AssetLocator.ResolveFontMetricsDirectory();
        if (!Directory.Exists(_fontMetricsDirectory))
        {
            throw new DirectoryNotFoundException($"Font-metric directory '{_fontMetricsDirectory}' does not exist.");
        }

        _profiles = new Dictionary<string, FontProfile>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(_fontMetricsDirectory, "*.json"))
        {
            var profile = JsonSerializer.Deserialize<FontProfile>(File.ReadAllText(file), JsonOptions);
            if (profile is not null && !string.IsNullOrEmpty(profile.ProfileKey))
            {
                _profiles[profile.ProfileKey] = profile;
            }
        }
    }

    /// <summary>Profile keys available in the bundled directory.</summary>
    public IReadOnlyCollection<string> ProfileKeys => _profiles.Keys;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The font profile key is not bundled.</exception>
    public SerpPixelMeasurement Measure(string text, SerpMeasurementOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        if (!_profiles.TryGetValue(options.FontProfileKey, out var profile))
        {
            throw new ArgumentException(
                $"Font profile '{options.FontProfileKey}' is not bundled in '{_fontMetricsDirectory}'.",
                nameof(options));
        }

        var scale = profile.FontSizePx / (double)profile.UnitsPerEm;
        var width = MeasureWidth(text, profile, scale);
        if (width <= options.BudgetPixels)
        {
            return new SerpPixelMeasurement(width, false, null);
        }

        var ellipsisWidth = MeasureWidth(Ellipsis, profile, scale);
        var available = options.BudgetPixels - ellipsisWidth;
        var truncated = available <= 0 ? string.Empty : LongestFittingPrefix(text, profile, scale, available) + Ellipsis;
        return new SerpPixelMeasurement(width, true, truncated);
    }

    private static double MeasureWidth(string text, FontProfile profile, double scale)
    {
        var units = 0d;
        foreach (var rune in text.EnumerateRunes())
        {
            units += profile.Widths.TryGetValue(rune.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var width)
                ? width
                : profile.DefaultWidth;
        }

        return units * scale;
    }

    private static string LongestFittingPrefix(string text, FontProfile profile, double scale, double available)
    {
        var builder = new StringBuilder();
        var units = 0d;
        foreach (var rune in text.EnumerateRunes())
        {
            var advance = profile.Widths.TryGetValue(rune.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var width)
                ? width
                : profile.DefaultWidth;
            if ((units + advance) * scale > available)
            {
                break;
            }

            units += advance;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private sealed record FontProfile(
        string ProfileKey,
        string FontFamily,
        double FontSizePx,
        int UnitsPerEm,
        int DefaultWidth,
        Dictionary<string, int> Widths,
        string Provenance);
}
