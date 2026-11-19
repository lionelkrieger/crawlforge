using System.Globalization;
using System.Text.RegularExpressions;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// Independent evidence facet a signature marker can match (FS-CRAWL-002
/// §2.3.1: response-header, cookie, title, dom-marker, script). The
/// corroboration guard counts distinct facet types (§2.3.3). <see cref="Body"/>
/// is the legacy generic body-substring facet kept for baseline catalogue
/// compatibility; it counts as its own independent facet type.
/// </summary>
public enum WafFacet
{
    /// <summary>Response header presence or value characteristic.</summary>
    Header = 0,

    /// <summary>Cookie characteristic observed in the set-cookie header.</summary>
    Cookie = 1,

    /// <summary>Bounded leading body snippet characteristic (legacy generic facet).</summary>
    Body = 2,

    /// <summary>Document title characteristic (bounded pre-parse extraction).</summary>
    Title = 3,

    /// <summary>Element/attribute selector pattern in the bounded pre-parse DOM.</summary>
    DomMarker = 4,

    /// <summary>Script source-URL pattern or inline-script marker.</summary>
    Script = 5
}

/// <summary>
/// One parsed signature marker with its evidence facet and confidence weight.
/// Marker grammar (clean-room vocabulary, FS-CRAWL-002 §2.3.1):
/// <list type="bullet">
/// <item>header: <c>name[~valueSubstring][|weight][|high]</c></item>
/// <item>cookie: <c>cookie:name[~valueSubstring][|weight][|high]</c></item>
/// <item>body: <c>substring[|weight]</c></item>
/// <item>title: <c>title:substring[|weight]</c></item>
/// <item>dom-marker: <c>dom:selector[|weight]</c> (selector: <c>tag#id.class[attr][attr=value]</c>)</item>
/// <item>script: <c>script:substring[|weight]</c> (matches script src URLs and inline script text)</item>
/// </list>
/// Matching is case-insensitive; weights default per facet and aggregate into
/// the candidate confidence (capped at 1.0). The <c>|high</c> flag marks a
/// high-specificity provider-specific header or cookie marker: one such facet
/// alone satisfies the corroboration guard (§2.3.3). The flag is rejected on
/// any other facet — generic title-only matches must never block alone.
/// </summary>
/// <param name="Facet">Evidence facet the marker corroborates.</param>
/// <param name="Name">Header or cookie name; the substring itself for body/title/script markers; the selector for dom markers.</param>
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

    /// <summary>Default weight of a matched title marker.</summary>
    public const double DefaultTitleWeight = 0.5;

    /// <summary>Default weight of a matched dom-marker marker.</summary>
    public const double DefaultDomMarkerWeight = 0.6;

    /// <summary>Default weight of a matched script marker.</summary>
    public const double DefaultScriptWeight = 0.6;

    private const string CookiePrefix = "cookie:";
    private const string TitlePrefix = "title:";
    private const string DomPrefix = "dom:";
    private const string ScriptPrefix = "script:";
    private const string HighSpecificityToken = "high";

    /// <summary>
    /// Whether the marker is a high-specificity provider-specific header or
    /// cookie marker (§2.3.3): a single such facet satisfies the corroboration
    /// guard on its own. Always false for body/title/dom/script facets.
    /// </summary>
    public bool HighSpecificity { get; init; }

    /// <summary>Parses a header/cookie marker pattern (HeaderMarkers list entry).</summary>
    /// <param name="pattern">Marker pattern; a <c>cookie:</c> prefix selects the cookie facet.</param>
    /// <returns>The parsed marker.</returns>
    public static WafMarker ParseHeaderMarker(string pattern) => Parse(pattern, WafFacet.Header);

    /// <summary>
    /// Parses a body-family marker pattern (BodyMarkers list entry). Facet
    /// prefixes select the five-facet set: <c>title:</c>, <c>dom:</c>,
    /// <c>script:</c>; an unprefixed pattern is the legacy body-substring facet.
    /// </summary>
    /// <param name="pattern">Marker pattern.</param>
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

    /// <summary>Matches the marker against a bounded body snippet (legacy body facet).</summary>
    /// <param name="bodySnippet">Leading body snippet (already bounded by policy).</param>
    /// <returns>True when the marker matches; body markers only.</returns>
    public bool MatchesBody(string? bodySnippet) =>
        Facet == WafFacet.Body
        && bodySnippet is not null
        && bodySnippet.Contains(Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Matches a body-family marker against the bounded pre-parse document
    /// (Stage B): body substring, title text, dom selector, or script
    /// source/inline markers. Header/cookie markers never match here.
    /// </summary>
    /// <param name="document">The bounded Stage B extraction.</param>
    /// <returns>True when the marker matches.</returns>
    public bool MatchesStageB(StageBDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Facet switch
        {
            WafFacet.Body => document.RawSnippet.Contains(Name, StringComparison.OrdinalIgnoreCase),
            WafFacet.Title => document.Title is not null
                && document.Title.Contains(Name, StringComparison.OrdinalIgnoreCase),
            WafFacet.Script => document.ScriptSources.Any(src => src.Contains(Name, StringComparison.OrdinalIgnoreCase))
                || document.InlineScripts.Any(inline => inline.Contains(Name, StringComparison.OrdinalIgnoreCase)),
            WafFacet.DomMarker => MatchesDomSelector(document.Tags),
            _ => false,
        };
    }

    private bool MatchesDomSelector(IReadOnlyList<StageBTag> tags)
    {
        var selector = DomSelector.Parse(Name);
        foreach (var tag in tags)
        {
            if (selector.Tag is not null && !string.Equals(tag.Name, selector.Tag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (selector.Id is not null
                && (!tag.Attributes.TryGetValue("id", out var id) || !string.Equals(id, selector.Id, StringComparison.Ordinal)))
            {
                continue;
            }

            if (selector.Classes.Count > 0)
            {
                if (!tag.Attributes.TryGetValue("class", out var classValue))
                {
                    continue;
                }

                var classes = classValue.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (!selector.Classes.All(c => classes.Contains(c, StringComparer.Ordinal)))
                {
                    continue;
                }
            }

            var attributesMatch = true;
            foreach (var (attributeName, attributeValue) in selector.Attributes)
            {
                if (!tag.Attributes.TryGetValue(attributeName, out var observed))
                {
                    attributesMatch = false;
                    break;
                }

                if (attributeValue is not null && !string.Equals(observed, attributeValue, StringComparison.Ordinal))
                {
                    attributesMatch = false;
                    break;
                }
            }

            if (attributesMatch)
            {
                return true;
            }
        }

        return false;
    }

    private static WafMarker Parse(string pattern, WafFacet defaultFacet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var text = pattern.Trim();

        // Optional trailing |high flag (high-specificity header/cookie marker).
        var highSpecificity = false;
        var lastPipe = text.LastIndexOf('|');
        if (lastPipe > 0 && string.Equals(text[(lastPipe + 1)..].Trim(), HighSpecificityToken, StringComparison.OrdinalIgnoreCase))
        {
            highSpecificity = true;
            text = text[..lastPipe].TrimEnd();
            lastPipe = text.LastIndexOf('|');
        }

        double? explicitWeight = null;
        if (lastPipe > 0 && lastPipe < text.Length - 1)
        {
            var weightText = text[(lastPipe + 1)..];
            if (!double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                || double.IsNaN(parsed) || parsed <= 0)
            {
                throw new FormatException($"Invalid marker weight '{weightText}' in pattern '{pattern}'.");
            }

            explicitWeight = parsed;
            text = text[..lastPipe];
        }

        var facet = defaultFacet;
        if (defaultFacet == WafFacet.Body)
        {
            if (text.StartsWith(TitlePrefix, StringComparison.OrdinalIgnoreCase))
            {
                facet = WafFacet.Title;
                text = text[TitlePrefix.Length..];
            }
            else if (text.StartsWith(DomPrefix, StringComparison.OrdinalIgnoreCase))
            {
                facet = WafFacet.DomMarker;
                text = text[DomPrefix.Length..];
            }
            else if (text.StartsWith(ScriptPrefix, StringComparison.OrdinalIgnoreCase))
            {
                facet = WafFacet.Script;
                text = text[ScriptPrefix.Length..];
            }
        }
        else if (text.StartsWith(CookiePrefix, StringComparison.OrdinalIgnoreCase))
        {
            facet = WafFacet.Cookie;
            text = text[CookiePrefix.Length..];
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException($"Marker pattern '{pattern}' has an empty marker body.");
        }

        if (highSpecificity && facet is not (WafFacet.Header or WafFacet.Cookie))
        {
            throw new FormatException(
                $"The |high specificity flag is only valid on header/cookie markers (FS-CRAWL-002 §2.3.3); pattern '{pattern}'.");
        }

        var weight = explicitWeight ?? facet switch
        {
            WafFacet.Header => DefaultHeaderWeight,
            WafFacet.Cookie => DefaultCookieWeight,
            WafFacet.Title => DefaultTitleWeight,
            WafFacet.DomMarker => DefaultDomMarkerWeight,
            WafFacet.Script => DefaultScriptWeight,
            _ => DefaultBodyWeight,
        };

        if (facet is WafFacet.Body or WafFacet.Title or WafFacet.Script)
        {
            return new WafMarker(facet, text, null, weight) { HighSpecificity = highSpecificity };
        }

        if (facet == WafFacet.DomMarker)
        {
            // Validate the selector at parse time so hostile catalogue data
            // fails fast instead of at match time.
            _ = DomSelector.Parse(text);
            return new WafMarker(facet, text, null, weight) { HighSpecificity = highSpecificity };
        }

        string name;
        string? valueSubstring = null;
        var tilde = text.IndexOf('~');
        if (tilde >= 0)
        {
            name = text[..tilde].Trim();
            valueSubstring = text[(tilde + 1)..].Trim();
        }
        else
        {
            name = text.Trim();
        }

        if (name.Length == 0)
        {
            throw new FormatException($"Marker pattern '{pattern}' has an empty name.");
        }

        return new WafMarker(facet, name, string.IsNullOrEmpty(valueSubstring) ? null : valueSubstring, weight)
        {
            HighSpecificity = highSpecificity
        };
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

            // Cookie names appear at the start of each set-cookie value or
            // after a comma/semicolon separator; match the name token exactly.
            var needle = Name + "=";
            var index = value.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                // The name must start a cookie: at position 0 or after a
                // cookie separator boundary, not inside another name.
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

    /// <summary>
    /// Minimal element/attribute selector for dom-marker facets:
    /// <c>tag#id.class[attr][attr=value]</c> — optional tag name followed by
    /// any mix of id, class and attribute constraints. Deliberately small:
    /// the Stage B pass never runs a full DOM query engine (§2.2).
    /// </summary>
    private sealed record DomSelector(
        string? Tag,
        string? Id,
        IReadOnlyList<string> Classes,
        IReadOnlyList<(string Name, string? Value)> Attributes)
    {
        private static readonly Regex AttributeName = new(
            @"^[a-zA-Z_:][-a-zA-Z0-9_:.]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

        public static DomSelector Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new FormatException("Empty dom-marker selector.");
            }

            string? tag = null;
            string? id = null;
            var classes = new List<string>();
            var attributes = new List<(string Name, string? Value)>();
            var i = 0;

            if (char.IsAsciiLetter(text[0]))
            {
                var start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '-' or '_' or ':'))
                {
                    i++;
                }

                tag = text[start..i];
            }

            while (i < text.Length)
            {
                switch (text[i])
                {
                    case '#':
                    {
                        var start = ++i;
                        while (i < text.Length && text[i] is not ('#' or '.' or '['))
                        {
                            i++;
                        }

                        if (i == start)
                        {
                            throw new FormatException($"Empty id in dom-marker selector '{text}'.");
                        }

                        id = text[start..i];
                        break;
                    }

                    case '.':
                    {
                        var start = ++i;
                        while (i < text.Length && text[i] is not ('#' or '.' or '['))
                        {
                            i++;
                        }

                        if (i == start)
                        {
                            throw new FormatException($"Empty class in dom-marker selector '{text}'.");
                        }

                        classes.Add(text[start..i]);
                        break;
                    }

                    case '[':
                    {
                        var end = text.IndexOf(']', i + 1);
                        if (end < 0)
                        {
                            throw new FormatException($"Unterminated attribute constraint in dom-marker selector '{text}'.");
                        }

                        var inner = text[(i + 1)..end].Trim();
                        var eq = inner.IndexOf('=');
                        var attributeName = (eq < 0 ? inner : inner[..eq]).Trim();
                        if (!AttributeName.IsMatch(attributeName))
                        {
                            throw new FormatException($"Invalid attribute name '{attributeName}' in dom-marker selector '{text}'.");
                        }

                        string? attributeValue = null;
                        if (eq >= 0)
                        {
                            attributeValue = inner[(eq + 1)..].Trim();
                            if (attributeValue.Length >= 2
                                && ((attributeValue[0] == '"' && attributeValue[^1] == '"')
                                    || (attributeValue[0] == '\'' && attributeValue[^1] == '\'')))
                            {
                                attributeValue = attributeValue[1..^1];
                            }
                        }

                        attributes.Add((attributeName.ToLowerInvariant(), attributeValue));
                        i = end + 1;
                        break;
                    }

                    default:
                        throw new FormatException($"Unexpected character '{text[i]}' in dom-marker selector '{text}'.");
                }
            }

            if (tag is null && id is null && classes.Count == 0 && attributes.Count == 0)
            {
                throw new FormatException($"Dom-marker selector '{text}' has no constraints.");
            }

            return new DomSelector(tag, id, classes, attributes);
        }
    }
}
