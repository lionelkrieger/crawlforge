using System.Globalization;
using System.Text.Json;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Structured-data validator (FS-CRAWL-001 §7.13, criterion #27): parses JSON-LD
/// and Microdata into a normalized graph, validates syntax (well-formedness,
/// context and type presence) and semantics (required properties per the bundled
/// <see cref="StructuredDataVocabulary"/>), and reports rich-result eligibility
/// with a structured error taxonomy (<see cref="StructuredDataErrorCodes"/>).
/// Markup is parsed through the injected tolerant DOM parser; validation never
/// throws on malformed input and performs zero network access.
/// </summary>
public sealed class StructuredDataValidator : IStructuredDataValidator
{
    private static readonly string[] DateProperties =
    [
        "datePublished", "dateModified", "uploadDate", "startDate", "endDate", "datePosted", "validThrough",
    ];

    private readonly ITolerantDomParser _parser;

    /// <summary>Creates the validator over the shared tolerant DOM parser.</summary>
    /// <param name="parser">Tolerant parser supplied by the audit pipeline.</param>
    public StructuredDataValidator(ITolerantDomParser parser)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    /// <inheritdoc />
    public StructuredDataValidationResult Validate(string markup, string? sourceUrl)
    {
        var blocks = new List<StructuredDataBlock>();
        if (string.IsNullOrWhiteSpace(markup))
        {
            return new StructuredDataValidationResult(blocks);
        }

        var document = _parser.Parse(markup, sourceUrl ?? string.Empty);
        foreach (var node in Walk(document.Root))
        {
            if (node.TagName == "script" && IsJsonLd(node))
            {
                blocks.AddRange(ParseJsonLdScript(node.TextContent));
            }
            else if (HasAttribute(node, "itemscope") && !HasAttribute(node, "itemprop"))
            {
                blocks.Add(ParseMicrodataItem(node));
            }
        }

        return new StructuredDataValidationResult(blocks);
    }

    private static IEnumerable<StructuredDataBlock> ParseJsonLdScript(string text)
    {
        JsonDocument? document = null;
        string? parseError = null;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text);
        }
        catch (JsonException exception)
        {
            parseError = exception.Message;
        }

        if (parseError is not null)
        {
            yield return new StructuredDataBlock(
                "json-ld", null, false,
                [StructuredDataErrorCodes.Format(StructuredDataErrorCodes.MalformedBlock, parseError)],
                false);
            yield break;
        }

        var parsed = document!;
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    yield return ValidateJsonLdItem(item, inheritedContext: null);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("@graph", out var graph) &&
                     graph.ValueKind == JsonValueKind.Array)
            {
                var hasContext = root.TryGetProperty("@context", out _);
                foreach (var item in graph.EnumerateArray())
                {
                    yield return ValidateJsonLdItem(item, inheritedContext: hasContext ? true : null);
                }
            }
            else
            {
                yield return ValidateJsonLdItem(root, inheritedContext: null);
            }
        }
    }

    private static StructuredDataBlock ValidateJsonLdItem(JsonElement item, bool? inheritedContext)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return new StructuredDataBlock(
                "json-ld", null, false,
                [StructuredDataErrorCodes.Format(StructuredDataErrorCodes.MalformedBlock, "block item is not a JSON object")],
                false);
        }

        var errors = new List<string>();
        if (!item.TryGetProperty("@context", out _) && inheritedContext != true)
        {
            errors.Add(StructuredDataErrorCodes.Format(StructuredDataErrorCodes.MissingContext, "JSON-LD block has no @context"));
        }

        var type = ReadType(item);
        if (type is null)
        {
            errors.Add(StructuredDataErrorCodes.Format(StructuredDataErrorCodes.MissingType, "JSON-LD block has no @type"));
        }

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in item.EnumerateObject())
        {
            properties[property.Name] = property.Value;
        }

        ValidateSemantics(type, properties, errors);
        return BuildBlock("json-ld", type, errors);
    }

    private static StructuredDataBlock ParseMicrodataItem(DomNode item)
    {
        var errors = new List<string>();
        var type = item.Attributes.TryGetValue("itemtype", out var itemType)
            ? itemType.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(type))
        {
            errors.Add(StructuredDataErrorCodes.Format(StructuredDataErrorCodes.MissingType, "Microdata item has no itemtype"));
            type = null;
        }

        var properties = CollectMicrodataProperties(item);
        ValidateSemantics(type, properties, errors);
        return BuildBlock("microdata", type, errors);
    }

    private static Dictionary<string, object?> CollectMicrodataProperties(DomNode item)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in WalkPropertyScope(item))
        {
            if (!child.Attributes.TryGetValue("itemprop", out var names))
            {
                continue;
            }

            // Nested items are collected by their own recursion; skip their subtree here.
            var value = HasAttribute(child, "itemscope")
                ? CollectMicrodataProperties(child)
                : ReadMicrodataValue(child);
            foreach (var name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (properties.TryGetValue(name, out var existing) && existing is List<object?> list)
                {
                    list.Add(value);
                }
                else if (properties.ContainsKey(name))
                {
                    properties[name] = new List<object?> { properties[name], value };
                }
                else
                {
                    properties[name] = value;
                }
            }
        }

        return properties;
    }

    private static object? ReadMicrodataValue(DomNode node) => node.TagName switch
    {
        "meta" => node.Attributes.TryGetValue("content", out var content) ? content : string.Empty,
        "a" or "area" or "link" => node.Attributes.TryGetValue("href", out var href) ? href : string.Empty,
        "img" or "source" or "embed" or "iframe" => node.Attributes.TryGetValue("src", out var src) ? src : string.Empty,
        "object" => node.Attributes.TryGetValue("data", out var data) ? data : string.Empty,
        "time" => node.Attributes.TryGetValue("datetime", out var datetime) ? datetime : AggregateText(node),
        _ => AggregateText(node),
    };

    private static void ValidateSemantics(string? type, IReadOnlyDictionary<string, object?> properties, List<string> errors)
    {
        var rule = StructuredDataVocabulary.Resolve(type);
        if (rule is null)
        {
            return;
        }

        var missing = new List<string>();
        foreach (var required in rule.Required)
        {
            if (!properties.TryGetValue(required, out var value) || IsEmptyValue(value))
            {
                missing.Add(required);
                errors.Add(StructuredDataErrorCodes.Format(
                    StructuredDataErrorCodes.MissingRequiredField, $"{type} requires '{required}'"));
            }
        }

        foreach (var property in properties)
        {
            ValidateValueFormat(type, property.Key, property.Value, errors);
        }

        if (string.Equals(StructuredDataVocabulary.LocalName(type ?? string.Empty), "Product", StringComparison.OrdinalIgnoreCase) &&
            properties.TryGetValue("offers", out var offers) && !IsEmptyValue(offers))
        {
            ValidateOffers(offers, errors);
        }

        if (rule.RichResultFeature && missing.Count > 0)
        {
            errors.Add(StructuredDataErrorCodes.Format(
                StructuredDataErrorCodes.RichResultMissingRequired,
                $"{type} rich result needs: {string.Join(", ", missing)}"));
        }
    }

    private static void ValidateOffers(object? offers, List<string> errors)
    {
        switch (offers)
        {
            case JsonElement element when element.ValueKind == JsonValueKind.Object:
                RequireOfferFields(element.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase), errors);
                break;
            case JsonElement element when element.ValueKind == JsonValueKind.Array:
                foreach (var offer in element.EnumerateArray())
                {
                    if (offer.ValueKind == JsonValueKind.Object)
                    {
                        RequireOfferFields(offer.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase), errors);
                    }
                }

                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                RequireOfferFields(dictionary, errors);
                break;
        }
    }

    private static void RequireOfferFields(IReadOnlyDictionary<string, object?> offer, List<string> errors)
    {
        foreach (var field in new[] { "price", "priceCurrency" })
        {
            if (!offer.TryGetValue(field, out var value) || IsEmptyValue(value))
            {
                errors.Add(StructuredDataErrorCodes.Format(
                    StructuredDataErrorCodes.MissingRequiredField, $"offers requires '{field}'"));
            }
        }

        if (offer.TryGetValue("price", out var price) && !IsEmptyValue(price) && !IsNumericPrice(price))
        {
            errors.Add(StructuredDataErrorCodes.Format(
                StructuredDataErrorCodes.InvalidValue, "offers.price must be a numeric value"));
        }
    }

    private static void ValidateValueFormat(string? type, string name, object? value, List<string> errors)
    {
        if (DateProperties.Contains(name, StringComparer.OrdinalIgnoreCase) && !IsEmptyValue(value))
        {
            var text = ScalarText(value);
            if (text is not null &&
                !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            {
                errors.Add(StructuredDataErrorCodes.Format(
                    StructuredDataErrorCodes.InvalidValue, $"{type}.{name} must be an ISO 8601 date ('{text}')"));
            }
        }
    }

    private static StructuredDataBlock BuildBlock(string format, string? type, List<string> errors)
    {
        var eligible = errors.Count == 0 && StructuredDataVocabulary.IsRichResultFeature(type);
        return new StructuredDataBlock(format, type, errors.Count == 0, errors, eligible);
    }

    private static string? ReadType(JsonElement item)
    {
        if (!item.TryGetProperty("@type", out var typeElement))
        {
            return null;
        }

        var raw = typeElement.ValueKind switch
        {
            JsonValueKind.String => typeElement.GetString(),
            JsonValueKind.Array => typeElement.EnumerateArray()
                .FirstOrDefault(t => t.ValueKind == JsonValueKind.String).GetString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static bool IsJsonLd(DomNode node) =>
        node.Attributes.TryGetValue("type", out var type) &&
        string.Equals(type.Trim(), "application/ld+json", StringComparison.OrdinalIgnoreCase);

    private static bool HasAttribute(DomNode node, string name) => node.Attributes.ContainsKey(name);

    private static bool IsEmptyValue(object? value) => value switch
    {
        null => true,
        JsonElement element => element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            (element.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(element.GetString())) ||
            (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 0),
        string text => string.IsNullOrWhiteSpace(text),
        IReadOnlyDictionary<string, object?> dictionary => dictionary.Count == 0,
        List<object?> list => list.Count == 0,
        _ => false,
    };

    private static string? ScalarText(object? value) => value switch
    {
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
        string text => text,
        _ => null,
    };

    private static bool IsNumericPrice(object? value) => value switch
    {
        JsonElement element when element.ValueKind == JsonValueKind.Number => true,
        JsonElement element when element.ValueKind == JsonValueKind.String =>
            decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out _),
        string text => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
        double or decimal or int or long => true,
        _ => false,
    };

    private static string AggregateText(DomNode node)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var descendant in Walk(node))
        {
            builder.Append(descendant.TextContent);
        }

        return builder.ToString().Trim();
    }

    private static IEnumerable<DomNode> Walk(DomNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Walks the property scope of a Microdata item: descendants in document
    /// order, stopping at nested itemscope boundaries (nested items belong to
    /// their own recursion, never to the parent's property list).
    /// </summary>
    private static IEnumerable<DomNode> WalkPropertyScope(DomNode item)
    {
        foreach (var child in item.Children)
        {
            yield return child;
            if (!HasAttribute(child, "itemscope"))
            {
                foreach (var descendant in WalkPropertyScope(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
