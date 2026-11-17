using System.Globalization;
using System.Text.Json;

namespace CrawlForge.Mcp;

/// <summary>
/// Argument coercion for MCP tool/prompt invocations. Arguments arrive either
/// as <see cref="JsonElement"/> values (wire path) or CLR primitives (direct
/// composition path); both are handled uniformly.
/// </summary>
public static class McpArguments
{
    /// <summary>Reads a required string argument.</summary>
    /// <param name="arguments">Argument map.</param>
    /// <param name="name">Argument name.</param>
    /// <returns>The string value.</returns>
    /// <exception cref="McpArgumentMissingException">The argument is absent or not a string.</exception>
    public static string RequireString(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        var value = GetString(arguments, name);
        return value ?? throw new McpArgumentMissingException(name);
    }

    /// <summary>Reads an optional string argument.</summary>
    /// <param name="arguments">Argument map.</param>
    /// <param name="name">Argument name.</param>
    /// <returns>The string value, or null.</returns>
    public static string? GetString(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
    }

    /// <summary>Reads a required 64-bit integer argument.</summary>
    /// <param name="arguments">Argument map.</param>
    /// <param name="name">Argument name.</param>
    /// <returns>The integer value.</returns>
    /// <exception cref="McpArgumentMissingException">The argument is absent or not numeric.</exception>
    public static long RequireLong(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        var value = GetLong(arguments, name);
        return value ?? throw new McpArgumentMissingException(name);
    }

    /// <summary>Reads an optional 64-bit integer argument (numeric strings accepted).</summary>
    /// <param name="arguments">Argument map.</param>
    /// <param name="name">Argument name.</param>
    /// <returns>The integer value, or null.</returns>
    public static long? GetLong(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw is null)
        {
            return null;
        }

        switch (raw)
        {
            case long number:
                return number;
            case int number:
                return number;
            case JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var parsed):
                return parsed;
            case JsonElement { ValueKind: JsonValueKind.String } element
                when long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            default:
                return null;
        }
    }

    /// <summary>Reads an optional boolean argument.</summary>
    /// <param name="arguments">Argument map.</param>
    /// <param name="name">Argument name.</param>
    /// <param name="fallback">Value when absent.</param>
    /// <returns>The boolean value.</returns>
    public static bool GetBool(IReadOnlyDictionary<string, object?> arguments, string name, bool fallback = false)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw is null)
        {
            return fallback;
        }

        return raw switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } element
                when bool.TryParse(element.GetString(), out var parsed) => parsed,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => fallback,
        };
    }

    /// <summary>Reads an optional string-list argument.</summary>
    /// <param name="arguments">Argument map.</param>
    /// <param name="name">Argument name.</param>
    /// <returns>The list, or null when absent.</returns>
    public static IReadOnlyList<string>? GetStringList(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.Array } element)
        {
            var items = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                {
                    items.Add(text);
                }
            }

            return items;
        }

        return raw is IEnumerable<string> sequence ? sequence.ToArray() : null;
    }

    /// <summary>Builds a bounded, privacy-safe argument summary for the action log.</summary>
    /// <param name="arguments">Argument map.</param>
    /// <returns>A summary truncated to 512 characters.</returns>
    public static string Summarize(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        var summary = JsonSerializer.Serialize(arguments, McpProtocol.JsonOptions);
        return summary.Length <= 512 ? summary : summary[..512];
    }
}

/// <summary>
/// A required MCP argument was missing or malformed (maps to JSON-RPC
/// invalid params).
/// </summary>
public sealed class McpArgumentMissingException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="argumentName">Missing argument name.</param>
    public McpArgumentMissingException(string argumentName)
        : base($"Required argument '{argumentName}' is missing or malformed.")
    {
        ArgumentName = argumentName;
    }

    /// <summary>The missing argument name.</summary>
    public string ArgumentName { get; }
}
