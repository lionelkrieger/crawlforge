using System.Text.Json;
using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// One Consent Mode v2 command observation (FS-CRAWL-002 §14.2): a default
/// or update declaration with the four consent-type states and a document
/// sequence marker.
/// </summary>
/// <param name="Sequence">Zero-based document-order sequence marker.</param>
/// <param name="Kind">Command kind: default | update.</param>
/// <param name="States">Consent-type states (granted/denied), when declared.</param>
/// <param name="Excerpt">Bounded command excerpt.</param>
internal sealed record ConsentCommand(
    int Sequence,
    string Kind,
    IReadOnlyDictionary<string, string> States,
    string Excerpt);

/// <summary>
/// Parses Consent Mode v2 default/update commands from inline scripts:
/// gtag('consent', 'default'|'update', {…}) and the equivalent
/// dataLayer.push(['consent', …]) form. Extracts the four consent types
/// (ad_storage, analytics_storage, ad_user_data, ad_personalization) into
/// an ordered timeline with sequence markers. Bounded and offline.
/// </summary>
internal static class ConsentTimelineParser
{
    /// <summary>The four Consent Mode v2 consent types.</summary>
    public static readonly string[] ConsentTypes =
    [
        "ad_storage", "analytics_storage", "ad_user_data", "ad_personalization",
    ];

    private const int MaxCommands = 100;

    private static readonly Regex CommandPattern = new(
        @"(?:gtag\(\s*['""]consent['""]\s*,\s*['""](default|update)['""]\s*,)|(?:dataLayer\.push\(\s*\[\s*['""]consent['""]\s*,\s*['""](default|update)['""]\s*,)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>Parses the consent command timeline from a document.</summary>
    public static IReadOnlyList<ConsentCommand> Parse(DomDocument? document, int excerptBound)
    {
        if (document is null)
        {
            return [];
        }

        var commands = new List<ConsentCommand>();
        foreach (var script in EnumerateInlineScripts(document.Root))
        {
            var content = script.TextContent;
            if (!content.Contains("consent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            MatchCollection matches;
            try
            {
                matches = CommandPattern.Matches(content);
            }
            catch (RegexMatchTimeoutException)
            {
                continue; // Time-bounded discipline: skip this script.
            }

            foreach (Match match in matches)
            {
                if (commands.Count >= MaxCommands)
                {
                    return commands;
                }

                var kind = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                var objectText = DataLayerParser.ExtractArgument(content, match.Index + match.Length);
                var states = ParseStates(objectText);
                var excerpt = objectText is null
                    ? match.Value
                    : match.Value + (objectText.Length <= excerptBound ? objectText : objectText[..excerptBound]);
                commands.Add(new ConsentCommand(commands.Count, kind.ToLowerInvariant(), states, excerpt));
            }
        }

        return commands;
    }

    private static IReadOnlyDictionary<string, string> ParseStates(string? objectText)
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        if (objectText is null)
        {
            return states;
        }

        try
        {
            using var document = JsonDocument.Parse(DataLayerParser.NormalizeJsLiteral(objectText));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return states;
            }

            foreach (var consentType in ConsentTypes)
            {
                if (document.RootElement.TryGetProperty(consentType, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    states[consentType] = value.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed consent object: the command is still observed; the
            // dataLayer inspector owns malformed-payload findings.
        }

        return states;
    }

    private static IEnumerable<DomNode> EnumerateInlineScripts(DomNode node)
    {
        if (node.TagName == "script" && !node.Attributes.ContainsKey("src"))
        {
            yield return node;
        }

        foreach (var child in node.Children)
        {
            foreach (var script in EnumerateInlineScripts(child))
            {
                yield return script;
            }
        }
    }
}
