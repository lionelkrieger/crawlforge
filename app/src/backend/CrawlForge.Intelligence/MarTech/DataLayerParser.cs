using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrawlForge.Intelligence.MarTech;

/// <summary>One bounded dataLayer event observation.</summary>
/// <param name="Sequence">Zero-based push sequence.</param>
/// <param name="EventName">The event name, when present.</param>
/// <param name="PayloadExcerpt">Bounded payload excerpt.</param>
/// <param name="WellFormed">Whether the payload parsed.</param>
internal sealed record DataLayerEvent(int Sequence, string? EventName, string PayloadExcerpt, bool WellFormed);

/// <summary>Aggregated dataLayer parse outcome.</summary>
/// <param name="PushCount">Total push occurrences observed.</param>
/// <param name="Events">Bounded event observations (first N).</param>
/// <param name="SyntaxIssues">Syntax issues (bounded).</param>
/// <param name="Truncated">Whether the event bound truncated capture.</param>
internal sealed record DataLayerParseResult(
    int PushCount,
    IReadOnlyList<DataLayerEvent> Events,
    IReadOnlyList<string> SyntaxIssues,
    bool Truncated);

/// <summary>
/// Bounded dataLayer payload parser (FS-CRAWL-002 §14.2/§14.3): extracts
/// dataLayer.push arguments from keystone snippets with balanced-delimiter
/// scanning, tolerates common JS-literal conventions (unquoted keys, single
/// quotes, trailing commas, function values), and validates against an
/// optional user-supplied JSON schema subset (type/required/properties at
/// the event root). All work is bounded and offline.
/// </summary>
internal static class DataLayerParser
{
    private const int MaxSyntaxIssues = 25;
    private const int MaxSchemaIssues = 50;
    private const int MaxArgumentChars = 16 * 1024;

    /// <summary>Parses push payloads from keystone dataLayer snippets.</summary>
    public static DataLayerParseResult Parse(
        IReadOnlyList<string> snippets,
        int eventBound,
        int excerptBound)
    {
        var events = new List<DataLayerEvent>();
        var issues = new List<string>();
        var pushCount = 0;
        var truncated = false;

        foreach (var snippet in snippets)
        {
            var searchFrom = 0;
            while (true)
            {
                var pushIndex = snippet.IndexOf("dataLayer.push(", searchFrom, StringComparison.Ordinal);
                if (pushIndex < 0)
                {
                    break;
                }

                searchFrom = pushIndex + "dataLayer.push(".Length;
                pushCount++;
                var argument = ExtractArgument(snippet, searchFrom);
                if (argument is null)
                {
                    if (issues.Count < MaxSyntaxIssues)
                    {
                        issues.Add($"push #{pushCount}: unbalanced or truncated push argument.");
                    }

                    if (events.Count < eventBound)
                    {
                        events.Add(new DataLayerEvent(pushCount - 1, null, string.Empty, WellFormed: false));
                    }
                    else
                    {
                        truncated = true;
                    }

                    continue;
                }

                var normalized = NormalizeJsLiteral(argument);
                string? eventName = null;
                var wellFormed = false;
                try
                {
                    using var document = JsonDocument.Parse(normalized);
                    wellFormed = true;
                    eventName = ExtractEventName(document.RootElement);
                }
                catch (JsonException)
                {
                    if (issues.Count < MaxSyntaxIssues)
                    {
                        issues.Add($"push #{pushCount}: payload is not valid JSON after JS-literal normalization.");
                    }
                }

                if (events.Count < eventBound)
                {
                    events.Add(new DataLayerEvent(
                        pushCount - 1,
                        eventName,
                        argument.Length <= excerptBound ? argument : argument[..excerptBound],
                        wellFormed));
                }
                else
                {
                    truncated = true;
                }
            }
        }

        return new DataLayerParseResult(pushCount, events, issues, truncated);
    }

    /// <summary>
    /// Validates well-formed event payloads against the user schema subset:
    /// root "required" (string array) and "properties" (name → { type }).
    /// Returns bounded human-readable issues.
    /// </summary>
    public static IReadOnlyList<string> ValidateSchema(
        string? schemaJson,
        IReadOnlyList<DataLayerEvent> events)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return [];
        }

        JsonElement schema;
        try
        {
            using var schemaDocument = JsonDocument.Parse(schemaJson);
            schema = schemaDocument.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ["The configured dataLayer schema is not valid JSON; schema validation was skipped."];
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return ["The configured dataLayer schema must be a JSON object; schema validation was skipped."];
        }

        var required = new List<string>();
        if (schema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    required.Add(item.GetString()!);
                }
            }
        }

        var propertyTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("type", out var typeElement)
                    && typeElement.ValueKind == JsonValueKind.String)
                {
                    propertyTypes[property.Name] = typeElement.GetString()!;
                }
            }
        }

        var issues = new List<string>();
        foreach (var dataLayerEvent in events)
        {
            if (!dataLayerEvent.WellFormed || dataLayerEvent.PayloadExcerpt.Length == 0)
            {
                continue;
            }

            JsonElement payload;
            try
            {
                using var payloadDocument = JsonDocument.Parse(NormalizeJsLiteral(dataLayerEvent.PayloadExcerpt));
                payload = payloadDocument.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue; // Excerpt may be bounded mid-payload; syntax already judged.
            }

            if (payload.ValueKind != JsonValueKind.Object)
            {
                continue; // Array commands (e.g. consent) are not schema-scoped events.
            }

            foreach (var key in required)
            {
                if (!payload.TryGetProperty(key, out _))
                {
                    AddIssue(issues, $"event #{dataLayerEvent.Sequence} ('{dataLayerEvent.EventName ?? "?"}'): missing required key '{key}'.");
                }
            }

            foreach (var (key, expectedType) in propertyTypes)
            {
                if (payload.TryGetProperty(key, out var value) && !MatchesType(value, expectedType))
                {
                    AddIssue(issues, $"event #{dataLayerEvent.Sequence} ('{dataLayerEvent.EventName ?? "?"}'): key '{key}' should be {expectedType}.");
                }
            }
        }

        return issues;
    }

    private static void AddIssue(List<string> issues, string issue)
    {
        if (issues.Count < MaxSchemaIssues)
        {
            issues.Add(issue);
        }
    }

    private static bool MatchesType(JsonElement value, string expectedType) => expectedType switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true, // Unknown type keywords are ignored (bounded subset).
    };

    private static string? ExtractEventName(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("event", out var eventElement)
            && eventElement.ValueKind == JsonValueKind.String)
        {
            return eventElement.GetString();
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    return item.GetString();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the balanced first argument of a push starting at
    /// <paramref name="start"/> (just after the opening parenthesis).
    /// </summary>
    internal static string? ExtractArgument(string text, int start)
    {
        var index = start;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        if (index >= text.Length)
        {
            return null;
        }

        var open = text[index];
        char close;
        switch (open)
        {
            case '{': close = '}'; break;
            case '[': close = ']'; break;
            default:
                // Scalar argument: read to the closing parenthesis.
                var scalarEnd = text.IndexOf(')', index);
                return scalarEnd < 0 ? null : text[index..scalarEnd].Trim();
        }

        var depth = 0;
        var inString = false;
        var stringChar = '\0';
        var end = -1;
        for (var i = index; i < text.Length && i - index < MaxArgumentChars; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '\\')
                {
                    i++;
                }
                else if (ch == stringChar)
                {
                    inString = false;
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                inString = true;
                stringChar = ch;
            }
            else if (ch == open)
            {
                depth++;
            }
            else if (ch == close)
            {
                depth--;
                if (depth == 0)
                {
                    end = i;
                    break;
                }
            }
        }

        return end < 0 ? null : text[index..(end + 1)];
    }

    /// <summary>
    /// Normalizes common JS object-literal conventions to JSON: unquoted
    /// keys, single-quoted strings, trailing commas, and function values
    /// (replaced with null). Bounded, heuristic, offline.
    /// </summary>
    internal static string NormalizeJsLiteral(string argument)
    {
        try
        {
            var text = ReplaceFunctionValues(argument);
            text = Regex.Replace(text, @"([{,]\s*)([A-Za-z_$][\w$]*)\s*:", "$1\"$2\":",
                RegexOptions.None, TimeSpan.FromMilliseconds(250));
            text = Regex.Replace(text, @"'([^'\\]*(?:\\.[^'\\]*)*)'", "\"$1\"",
                RegexOptions.None, TimeSpan.FromMilliseconds(250));
            text = Regex.Replace(text, @",(\s*[}\]])", "$1",
                RegexOptions.None, TimeSpan.FromMilliseconds(250));
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            // Bounded discipline: return the raw text; the JSON parse will
            // judge it malformed rather than crashing the audit.
            return argument;
        }
    }

    private static string ReplaceFunctionValues(string text)
    {
        var result = text;
        var guard = 0;
        while (guard++ < 32)
        {
            var match = Regex.Match(result, @"function\s*\([^)]*\)\s*\{",
                RegexOptions.None, TimeSpan.FromMilliseconds(250));
            if (!match.Success)
            {
                break;
            }

            var braceStart = match.Index + match.Length - 1;
            var depth = 0;
            var end = -1;
            for (var i = braceStart; i < result.Length; i++)
            {
                if (result[i] == '{')
                {
                    depth++;
                }
                else if (result[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            if (end < 0)
            {
                break;
            }

            result = result[..match.Index] + "null" + result[(end + 1)..];
        }

        return result;
    }
}
