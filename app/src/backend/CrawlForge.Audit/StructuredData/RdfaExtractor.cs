using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// One normalized RDFa item extracted from the DOM (CAP-B2-071): the declared
/// type (raw <c>typeof</c> token, possibly namespace-qualified or prefixed),
/// its property graph, and the raw types of direct nested entities.
/// </summary>
/// <param name="Type">First <c>typeof</c> token, or null for an untyped property set.</param>
/// <param name="Properties">Property values keyed by property name (case-insensitive).</param>
/// <param name="NestedEntityTypes">Raw <c>typeof</c> tokens of direct nested entities.</param>
public sealed record RdfaItem(
    string? Type,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyList<string> NestedEntityTypes);

/// <summary>
/// RDFa extraction (FS-CRAWL-001 §7.13 as amended by CR-017/D-017; CAP-B2-071).
/// Implements the RDFa Lite/core attribute subset over the contract DOM:
/// <c>vocab</c>, <c>prefix</c>, <c>typeof</c>, <c>property</c>, <c>resource</c>,
/// <c>content</c>, <c>datatype</c> (literal override), plus the URL-valued
/// <c>href</c>/<c>src</c> and <c>time datetime</c> conventions. Extraction is
/// fully offline and never throws on malformed markup.
/// </summary>
/// <remarks>
/// <para>
/// Block boundaries mirror the Microdata extractor: a <c>typeof</c> element
/// without a <c>property</c> attribute is a top-level entity; a <c>typeof</c>
/// element carrying <c>property</c> is a nested entity (relationship) collected
/// into its parent's property graph. Property scope walks descendants in
/// document order and stops at nested <c>typeof</c> boundaries.
/// </para>
/// <para>
/// Value precedence follows RDFa literal/URL resolution: <c>content</c>
/// overrides everything; then <c>resource</c>, <c>href</c>, <c>src</c> URL
/// values; then <c>time datetime</c>; otherwise the aggregated element text.
/// Prefixed or absolute type tokens are kept raw — the bundled vocabulary
/// resolves them by local name (<see cref="StructuredDataVocabulary.LocalName"/>).
/// </para>
/// <para>
/// Open Graph <c>meta property="og:…"</c> tags are NOT RDFa items unless a
/// <c>vocab</c>/<c>prefix</c> context declares them: an untyped property set is
/// only extracted when at least one ancestor (or the element itself) carries
/// <c>vocab</c> or <c>prefix</c>, and no <c>typeof</c> exists anywhere in the
/// document. Such a set describes the document subject with no declared type
/// and is reported through the missing-type taxonomy.
/// </para>
/// </remarks>
public static class RdfaExtractor
{
    /// <summary>Extracts one top-level RDFa entity from a <c>typeof</c> element.</summary>
    /// <param name="item">The element carrying <c>typeof</c> (and no <c>property</c>).</param>
    /// <returns>The normalized item with its property graph and nested entity types.</returns>
    public static RdfaItem ExtractItem(DomNode item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var type = FirstToken(item.Attributes.TryGetValue("typeof", out var typeofValue) ? typeofValue : null);
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var nestedTypes = new List<string>();
        CollectProperties(item, properties, nestedTypes);
        return new RdfaItem(type, properties, nestedTypes);
    }

    /// <summary>
    /// Extracts the untyped document-subject property set when the document
    /// declares a vocabulary context (<c>vocab</c>/<c>prefix</c>) but no
    /// <c>typeof</c> anywhere. Returns null when any <c>typeof</c> exists or no
    /// vocabulary-scoped <c>property</c> is present (plain Open Graph meta tags
    /// without a declared context are not RDFa items).
    /// </summary>
    /// <param name="root">Document root node.</param>
    /// <returns>The untyped item (Type null), or null when not applicable.</returns>
    public static RdfaItem? ExtractUntypedItem(DomNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (Walk(root).Any(node => HasAttribute(node, "typeof")))
        {
            return null;
        }

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var found = false;
        CollectUntypedProperties(root, inVocabScope: false, properties, ref found);
        return found ? new RdfaItem(null, properties, []) : null;
    }

    private static void CollectProperties(DomNode item, Dictionary<string, object?> properties, List<string> nestedTypes)
    {
        foreach (var child in WalkPropertyScope(item))
        {
            if (!child.Attributes.TryGetValue("property", out var names))
            {
                continue;
            }

            object? value;
            if (HasAttribute(child, "typeof"))
            {
                var nested = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                // Grandchild entity types belong to the nested entity's own graph;
                // only the direct child's type is a relationship of this item.
                CollectProperties(child, nested, []);
                if (FirstToken(child.Attributes.TryGetValue("typeof", out var nestedType) ? nestedType : null) is { } token)
                {
                    nestedTypes.Add(token);
                }

                value = nested;
            }
            else
            {
                value = ReadValue(child);
            }

            foreach (var name in SplitTokens(names))
            {
                // Normalized graph: prefixed/absolute property terms resolve to
                // their local segment against the bundled vocabulary, exactly
                // as type terms do (schema:name → name).
                AddProperty(properties, StructuredDataVocabulary.LocalName(name), value);
            }
        }
    }

    private static void CollectUntypedProperties(DomNode node, bool inVocabScope, Dictionary<string, object?> properties, ref bool found)
    {
        var scope = inVocabScope || HasAttribute(node, "vocab") || HasAttribute(node, "prefix");
        if (scope && node.Attributes.TryGetValue("property", out var names))
        {
            found = true;
            var value = ReadValue(node);
            foreach (var name in SplitTokens(names))
            {
                AddProperty(properties, StructuredDataVocabulary.LocalName(name), value);
            }
        }

        foreach (var child in node.Children)
        {
            CollectUntypedProperties(child, scope, properties, ref found);
        }
    }

    private static object? ReadValue(DomNode node)
    {
        if (node.Attributes.TryGetValue("content", out var content))
        {
            return content;
        }

        if (node.Attributes.TryGetValue("resource", out var resource))
        {
            return resource;
        }

        if (node.Attributes.TryGetValue("href", out var href))
        {
            return href;
        }

        if (node.Attributes.TryGetValue("src", out var src))
        {
            return src;
        }

        if (node.Attributes.TryGetValue("datetime", out var datetime))
        {
            return datetime;
        }

        return AggregateText(node);
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

    private static string? FirstToken(string? raw) =>
        SplitTokens(raw).FirstOrDefault();

    private static IEnumerable<string> SplitTokens(string? raw) =>
        (raw ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool HasAttribute(DomNode node, string name) => node.Attributes.ContainsKey(name);

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
    /// Walks the property scope of an RDFa entity: descendants in document
    /// order, stopping at nested <c>typeof</c> boundaries (nested entities
    /// belong to their own recursion, never to the parent's property list).
    /// </summary>
    private static IEnumerable<DomNode> WalkPropertyScope(DomNode item)
    {
        foreach (var child in item.Children)
        {
            yield return child;
            if (!HasAttribute(child, "typeof"))
            {
                foreach (var descendant in WalkPropertyScope(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
