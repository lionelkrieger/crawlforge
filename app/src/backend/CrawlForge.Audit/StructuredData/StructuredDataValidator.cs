using System.Globalization;
using System.Text.Json;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Structured-data validator (FS-CRAWL-001 §7.13 as amended by CR-017/D-017,
/// criterion #27): parses JSON-LD, Microdata and RDFa into a normalized graph,
/// validates syntax (well-formedness, context and type presence) and semantics
/// (required and expected properties per the bundled
/// <see cref="StructuredDataVocabulary"/>), and reports rich-result eligibility
/// with a structured error taxonomy (<see cref="StructuredDataErrorCodes"/>).
/// Markup is parsed through the injected tolerant DOM parser; validation never
/// throws on malformed input and performs zero network access.
/// </summary>
/// <remarks>
/// <para>
/// One engine, two entry points: the locked <see cref="IStructuredDataValidator"/>
/// markup surface (consumed by the UI-19 structured-data checker tool) and the
/// additive <see cref="ValidateDetailed(DomDocument)"/> surface (consumed by the
/// audit pipeline rule adapter over the already-parsed page document). Both
/// project from the same extraction + validation core, so the tool and full
/// audits always share identical judgments.
/// </para>
/// <para>
/// The detailed result adds per-block facts the locked
/// <see cref="StructuredDataBlock"/> contract cannot carry: block ordinal,
/// named missing required/recommended properties (CAP-B2-075/076), and direct
/// nested entity types (CAP-B2-073 relationships). Recommended gaps are
/// advisory: they never invalidate a block and never block eligibility.
/// </para>
/// </remarks>
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
    public StructuredDataValidationResult Validate(string markup, string? sourceUrl) =>
        new(ValidateDetailed(markup, sourceUrl).Blocks.Select(detail => detail.Block).ToList());

    /// <summary>
    /// Validates markup with the additive per-block detail (tool and adapter
    /// share this core). The markup is parsed through the injected tolerant
    /// parser exactly as the locked <see cref="Validate"/> surface does.
    /// </summary>
    /// <param name="markup">HTML or markup fragment containing structured data.</param>
    /// <param name="sourceUrl">Optional source URL for context.</param>
    /// <returns>Per-block details in document order.</returns>
    public StructuredDataDetailedResult ValidateDetailed(string markup, string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(markup))
        {
            return new StructuredDataDetailedResult([]);
        }

        return ValidateDetailed(_parser.Parse(markup, sourceUrl ?? string.Empty));
    }

    /// <summary>
    /// Validates an already-parsed document (pipeline path: no re-parse). This
    /// is the same extraction + validation core the markup surface uses.
    /// </summary>
    /// <param name="document">Parsed page document.</param>
    /// <returns>Per-block details in document order.</returns>
    public StructuredDataDetailedResult ValidateDetailed(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var items = new List<ParsedItem>();
        foreach (var node in Walk(document.Root))
        {
            if (node.TagName == "script" && IsJsonLd(node))
            {
                items.AddRange(ParseJsonLdScript(node.TextContent));
            }
            else if (HasAttribute(node, "itemscope") && !HasAttribute(node, "itemprop"))
            {
                items.Add(ParseMicrodataItem(node));
            }
            else if (HasAttribute(node, "typeof") && !HasAttribute(node, "property"))
            {
                var rdfa = RdfaExtractor.ExtractItem(node);
                items.Add(new ParsedItem("rdfa", rdfa.Type, rdfa.Properties, [], rdfa.NestedEntityTypes));
            }
        }

        // Untyped RDFa: vocabulary-scoped properties without any typeof describe
        // the document subject; the missing-type taxonomy applies. Plain Open
        // Graph meta tags without a vocab/prefix context are not RDFa items.
        if (RdfaExtractor.ExtractUntypedItem(document.Root) is { } untyped)
        {
            items.Add(new ParsedItem(
                "rdfa",
                null,
                untyped.Properties,
                [StructuredDataErrorCodes.Format(StructuredDataErrorCodes.MissingType, "RDFa block has no typeof")],
                untyped.NestedEntityTypes));
        }

        var blocks = new List<StructuredDataBlockDetail>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            blocks.Add(Finalize(items[i], i));
        }

        return new StructuredDataDetailedResult(blocks);
    }

    private static IEnumerable<ParsedItem> ParseJsonLdScript(string text)
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
            yield return MalformedItem("json-ld", parseError);
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
                    yield return ParseJsonLdItem(item, inheritedContext: null);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("@graph", out var graph) &&
                     graph.ValueKind == JsonValueKind.Array)
            {
                var hasContext = root.TryGetProperty("@context", out _);
                foreach (var item in graph.EnumerateArray())
                {
                    yield return ParseJsonLdItem(item, inheritedContext: hasContext ? true : null);
                }
            }
            else
            {
                yield return ParseJsonLdItem(root, inheritedContext: null);
            }
        }
    }

    private static ParsedItem ParseJsonLdItem(JsonElement item, bool? inheritedContext)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return MalformedItem("json-ld", "block item is not a JSON object");
        }

        var syntaxErrors = new List<string>();
        if (!item.TryGetProperty("@context", out _) && inheritedContext != true)
        {
            syntaxErrors.Add(StructuredDataErrorCodes.Format(StructuredDataErrorCodes.MissingContext, "JSON-LD block has no @context"));
        }

        var type = ReadType(item);
        if (type is null)
        {
            syntaxErrors.Add(StructuredDataErrorCodes.Format(StructuredDataErrorCodes.MissingType, "JSON-LD block has no @type"));
        }

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in item.EnumerateObject())
        {
            // Clone so the value survives the JsonDocument disposal: semantic
            // validation runs in Finalize, after the parse using-block exits.
            properties[property.Name] = property.Value.Clone();
        }

        return new ParsedItem("json-ld", type, properties, syntaxErrors, CollectJsonLdNestedTypes(properties));
    }

    private static ParsedItem ParseMicrodataItem(DomNode item)
    {
        var syntaxErrors = new List<string>();
        var type = item.Attributes.TryGetValue("itemtype", out var itemType)
            ? itemType.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(type))
        {
            syntaxErrors.Add(StructuredDataErrorCodes.Format(StructuredDataErrorCodes.MissingType, "Microdata item has no itemtype"));
            type = null;
        }

        var properties = CollectMicrodataProperties(item);
        return new ParsedItem("microdata", type, properties, syntaxErrors, CollectMicrodataNestedTypes(item));
    }

    private static ParsedItem MalformedItem(string format, string detail) =>
        new(format, null, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            [StructuredDataErrorCodes.Format(StructuredDataErrorCodes.MalformedBlock, detail)], []);

    private static StructuredDataBlockDetail Finalize(ParsedItem item, int ordinal)
    {
        var errors = new List<string>(item.SyntaxErrors);
        var missingRequired = new List<string>();
        var malformed = item.SyntaxErrors.Exists(e =>
            e.StartsWith(StructuredDataErrorCodes.MalformedBlock + ":", StringComparison.Ordinal));

        if (!malformed)
        {
            ValidateSemantics(item.Type, item.Properties, errors, missingRequired);
        }

        var missingRecommended = malformed ? [] : FindMissingRecommended(item.Type, item.Properties);

        // CR-030/D-021: retired/restricted rich-result types are flagged from
        // the bundled versioned list (never fetched). A retired type is never
        // rich-result eligible; a restricted type keeps its vocabulary-driven
        // judgment (site authority is not observable offline) and is reported
        // through the deprecated-term finding.
        var deprecatedEntry = DeprecatedRichResultList.Find(item.Type);
        var eligible = errors.Count == 0
            && StructuredDataVocabulary.IsRichResultFeature(item.Type)
            && deprecatedEntry?.IsRetired != true;
        var block = new StructuredDataBlock(item.Format, item.Type, errors.Count == 0, errors, eligible);
        return new StructuredDataBlockDetail(block, ordinal, missingRequired, missingRecommended, item.NestedEntityTypes, deprecatedEntry);
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
                // CR-030/D-021: Microdata itemprop URL and prefixed terms resolve
                // to their local segment against the bundled vocabulary, exactly
                // as RDFa property terms do (itemprop="https://schema.org/name"
                // or "schema:name" → name).
                AddProperty(properties, StructuredDataVocabulary.LocalName(name), value);
            }
        }

        return properties;
    }

    private static List<string> CollectMicrodataNestedTypes(DomNode item)
    {
        var types = new List<string>();
        foreach (var child in WalkPropertyScope(item))
        {
            if (HasAttribute(child, "itemprop") && HasAttribute(child, "itemscope") &&
                child.Attributes.TryGetValue("itemtype", out var itemType) &&
                itemType.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } token)
            {
                types.Add(token);
            }
        }

        return types;
    }

    private static List<string> CollectJsonLdNestedTypes(Dictionary<string, object?> properties)
    {
        var types = new List<string>();
        foreach (var value in properties.Values)
        {
            if (value is not JsonElement element)
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                if (ReadType(element) is { } type)
                {
                    types.Add(type);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var member in element.EnumerateArray())
                {
                    if (member.ValueKind == JsonValueKind.Object && ReadType(member) is { } memberType)
                    {
                        types.Add(memberType);
                    }
                }
            }
        }

        return types;
    }

    private static void AddProperty(Dictionary<string, object?> properties, string name, object? value)
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

    private static object? ReadMicrodataValue(DomNode node) => node.TagName switch
    {
        "meta" => node.Attributes.TryGetValue("content", out var content) ? content : string.Empty,
        "a" or "area" or "link" => node.Attributes.TryGetValue("href", out var href) ? href : string.Empty,
        "img" or "source" or "embed" or "iframe" => node.Attributes.TryGetValue("src", out var src) ? src : string.Empty,
        "object" => node.Attributes.TryGetValue("data", out var data) ? data : string.Empty,
        "time" => node.Attributes.TryGetValue("datetime", out var datetime) ? datetime : AggregateText(node),
        _ => AggregateText(node),
    };

    private static void ValidateSemantics(
        string? type,
        IReadOnlyDictionary<string, object?> properties,
        List<string> errors,
        List<string> missingRequired)
    {
        var rule = StructuredDataVocabulary.Resolve(type);
        if (rule is null)
        {
            return;
        }

        foreach (var required in rule.Required)
        {
            if (!properties.TryGetValue(required, out var value) || IsEmptyValue(value))
            {
                missingRequired.Add(required);
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

        if (rule.RichResultFeature && missingRequired.Count > 0)
        {
            errors.Add(StructuredDataErrorCodes.Format(
                StructuredDataErrorCodes.RichResultMissingRequired,
                $"{type} rich result needs: {string.Join(", ", missingRequired)}"));
        }
    }

    private static List<string> FindMissingRecommended(string? type, IReadOnlyDictionary<string, object?> properties)
    {
        var rule = StructuredDataVocabulary.Resolve(type);
        if (rule is null)
        {
            return [];
        }

        var missing = new List<string>();
        foreach (var recommended in rule.Recommended)
        {
            if (!properties.TryGetValue(recommended, out var value) || IsEmptyValue(value))
            {
                missing.Add(recommended);
            }
        }

        return missing;
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

    /// <summary>
    /// One parsed block before semantic validation: format, raw type, normalized
    /// property graph, syntax-level taxonomy errors, and direct nested entity
    /// types. All three markup formats project onto this shape so validation,
    /// eligibility, and the error taxonomy are format-independent.
    /// </summary>
    private sealed record ParsedItem(
        string Format,
        string? Type,
        IReadOnlyDictionary<string, object?> Properties,
        List<string> SyntaxErrors,
        IReadOnlyList<string> NestedEntityTypes);
}
