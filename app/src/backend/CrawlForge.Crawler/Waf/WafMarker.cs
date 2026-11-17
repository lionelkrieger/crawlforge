using System.Globalization;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// Independent evidence facet a signature marker can match. The corroboration
/// guard counts distinct facets, so a single facet never classifies a response
/// as a challenge on its own (FS-CRAWL-002 §2).
/// </summary>
public enum WafFacet
{
    /// <summary>Response header presence or value characteristic.</summary>
    Header = 0,

    /// <summary>Cookie characteristic observed in the set-cookie header.</summary>
    Cookie = 1,

    /// <summary>Bounded leading body snippet characteristic.</summary>
    Body = 2,
}

/// <summary>
/// One parsed signature marker with its evidence facet and confidence weight.
/// Marker grammar (clean-room vocabulary, FS-CRAWL-002 §1.2):
/// <list type="bullet">
/// <item>header: <c>name[~valueSubstring][|weight]</c></item>
/// <item>cookie: <c>cookie:name[~valueSubstring][|weight]</c></item>
/// <item>body: <c>substring[|weight]</c></item>
/// </list>
/// Matching is case-insensitive; weights default per facet and aggregate into
/// the candidate confidence (capped at 1.0).
/// </summary>
/// <param name="Facet">Evidence facet the marker corroborates.</param>
/// <param name="Name">Header or cookie name; the substring itself for body markers.</param>
/// <param name="ValueSubstring">Optional case-insensitive value substring requirement.</param>
/// <param name="Weight">Confidence weight contributed when the marker matches.</param>
public sealed record WafMarker(WafFacet Facet, string Name, string? ValueSubstring, double Weight)
{
    /// <summary>Default weight of a matched header marker.</summary>
    public const double DefaultHeaderWeight = 0.5;

    /// <summary>Default weight of a matched cookie marker.</summary>
    public const double DefaultCookieWeight = 0.5;

    /// <summary>Default weight of a matched body marker.</summary>
    public const double DefaultBodyWeight = 0.6;

    private const string CookiePrefix = "cookie:";

    /// <summary>Parses a header/cookie marker pattern (HeaderMarkers list entry).</summary>
    /// <param name="pattern">Marker pattern; a <c>cookie:</c> prefix selects the cookie facet.</param>
    /// <returns>The parsed marker.</returns>
    public static WafMarker ParseHeaderMarker(string pattern) => Parse(pattern, WafFacet.Header);

    /// <summary>Parses a body marker pattern (BodyMarkers list entry).</summary>
    /// <param name="pattern">Marker pattern (case-insensitive substring).</param>
    /// <returns>The parsed marker.</returns>
    public static WafMarker ParseBodyMarker(string pattern) => Parse(pattern, WafFacet.Body);

    /// <summary>
    /// Matches the marker against observed response headers. Header markers
    /// match any header by name; cookie markers match cookie names inside the
    /// set-cookie header.
    /// </summary>
    /// <param name="headers">Observed response headers (any key casing).</param>
    /// <returns>True when the marker matches.</returns>
    public bool MatchesHeaders(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return Facet switch
        {
            WafFacet.Header => MatchesHeader(headers),
            WafFacet.Cookie => MatchesCookie(headers),
            _ => false,
        };
    }

    /// <summary>Matches the marker against a bounded body snippet.</summary>
    /// <param name="bodySnippet">Leading body snippet (already bounded by policy).</param>
    /// <returns>True when the marker matches; body markers only.</returns>
    public bool MatchesBody(string? bodySnippet) =>
        Facet == WafFacet.Body
        && bodySnippet is not null
        && bodySnippet.Contains(Name, StringComparison.OrdinalIgnoreCase);

    private static WafMarker Parse(string pattern, WafFacet defaultFacet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var text = pattern.Trim();

        var weight = defaultFacet switch
        {
            WafFacet.Header => DefaultHeaderWeight,
            WafFacet.Cookie => DefaultCookieWeight,
            _ => DefaultBodyWeight,
        };

        var separator = text.LastIndexOf('|');
        if (separator >= 0)
        {
            var weightText = text[(separator + 1)..];
            if (!double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out weight)
                || weight <= 0 || double.IsNaN(weight) || double.IsInfinity(weight))
            {
                throw new FormatException($"Invalid marker weight '{weightText}' in pattern '{pattern}'.");
            }

            text = text[..separator].TrimEnd();
        }

        var facet = defaultFacet;
        if (facet != WafFacet.Body && text.StartsWith(CookiePrefix, StringComparison.OrdinalIgnoreCase))
        {
            facet = WafFacet.Cookie;
            text = text[CookiePrefix.Length..];
        }

        if (facet == WafFacet.Body)
        {
            if (text.Length == 0)
            {
                throw new FormatException($"Empty body marker pattern '{pattern}'.");
            }

            return new WafMarker(facet, text, null, weight);
        }

        var valueSeparator = text.IndexOf('~', StringComparison.Ordinal);
        var name = valueSeparator >= 0 ? text[..valueSeparator].Trim() : text.Trim();
        var valueSubstring = valueSeparator >= 0 ? text[(valueSeparator + 1)..].Trim() : null;
        if (name.Length == 0)
        {
            throw new FormatException($"Empty header/cookie name in pattern '{pattern}'.");
        }

        return new WafMarker(facet, name, string.IsNullOrEmpty(valueSubstring) ? null : valueSubstring, weight);
    }

    private bool MatchesHeader(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (key, value) in headers)
        {
            if (!string.Equals(key, Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ValueSubstring is null || value.Contains(ValueSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchesCookie(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (key, value) in headers)
        {
            if (!string.Equals(key, "set-cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var needle = Name + "=";
            var index = value.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                // A cookie name must start at a cookie boundary, not inside another name.
                var atBoundary = index == 0 || value[index - 1] is ',' or ';' or ' ';
                if (atBoundary)
                {
                    if (ValueSubstring is null)
                    {
                        return true;
                    }

                    var start = index + needle.Length;
                    var end = value.IndexOfAny([';', ','], start);
                    var cookieValue = end >= 0 ? value[start..end] : value[start..];
                    if (cookieValue.Contains(ValueSubstring, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                index = value.IndexOf(needle, index + needle.Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}
