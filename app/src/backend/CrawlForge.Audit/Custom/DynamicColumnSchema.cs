using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Custom;

/// <summary>
/// One dynamic column descriptor consumed by the phase-05 query builder and
/// export engine (FS-CRAWL-001 §7.9/§7.17, §8.2/§8.4/§8.7).
/// </summary>
/// <param name="Key">Stable column key (namespaced; see <see cref="DynamicColumnSchema"/>).</param>
/// <param name="Label">User-facing column label (rule name or snippet column name).</param>
/// <param name="ValueType">Column value type: text, number, boolean, datetime, string.</param>
/// <param name="Source">Originating rule name or snippet key.</param>
/// <param name="Ordinal">Expansion ordinal for multi-valued rules (0-based).</param>
public sealed record DynamicColumnDescriptor(string Key, string Label, string ValueType, string Source, int Ordinal);

/// <summary>
/// The dynamic-column schema contract for custom extraction and snippet
/// datasets (FS-CRAWL-001 §7.9/§7.17). Phase-05's query builder and export
/// engine consume these descriptors to present one column per rule — with
/// multi-valued rules expanding to as many columns as the busiest page — and
/// one column per declared snippet result column.
/// </summary>
/// <remarks>
/// <para><b>Naming.</b> Extraction columns: <c>custom.{ruleName}</c>, expanding
/// to <c>custom.{ruleName}.{ordinal}</c> when a rule captures multiple values
/// on any page. Snippet columns: <c>snippet.{snippetKey}.{columnKey}</c>.
/// Keys are case-sensitive and ordinal-stable; labels preserve user casing.</para>
/// <para><b>Types.</b> Extraction columns carry the rule's configured
/// <see cref="ExtractedValueType"/> (text/number/boolean/datetime). Snippet
/// columns carry the inferred type from submitted values (number/boolean/string).
/// Invalid values persist as null payloads with the invalid flag — columns
/// remain present and typed.</para>
/// <para><b>Caps.</b> Extraction: per-rule per-page value cap and per-value
/// length cap (see <see cref="CustomExtractionOptions"/>); truncation and
/// discard counts surface as diagnostics. Snippets: per-snippet row bound
/// (<c>JsSnippetDefinition.MaxRows</c> clamped by
/// <see cref="JsSnippetOptions.MaxRowsCeiling"/>); excess rows are discarded
/// and flagged. Consumers must treat caps as schema-level bounds, not data
/// errors.</para>
/// </remarks>
public static class DynamicColumnSchema
{
    /// <summary>Namespace prefix for extraction columns.</summary>
    public const string ExtractionColumnPrefix = "custom.";

    /// <summary>Namespace prefix for snippet columns.</summary>
    public const string SnippetColumnPrefix = "snippet.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds extraction column descriptors from rules and the values captured
    /// on the observed pages. Multi-valued rules expand to the busiest page's
    /// ordinal count; single-valued rules produce exactly one column.
    /// </summary>
    /// <param name="rules">Configured extraction rules (column order follows rule order).</param>
    /// <param name="values">Captured values across the observed pages.</param>
    /// <returns>Ordered, stable column descriptors.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<DynamicColumnDescriptor> BuildExtractionColumns(
        IReadOnlyList<CustomExtractionRule> rules,
        IReadOnlyList<ExtractedValueDraft> values)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(values);

        var descriptors = new List<DynamicColumnDescriptor>();
        foreach (CustomExtractionRule rule in rules)
        {
            int expansion = Math.Max(1, values
                .Where(v => string.Equals(v.RuleName, rule.Name, StringComparison.Ordinal))
                .Select(v => v.Ordinal + 1)
                .DefaultIfEmpty(1)
                .Max());

            string valueType = MapValueType(rule.ValueType);
            for (int ordinal = 0; ordinal < expansion; ordinal++)
            {
                string key = expansion == 1
                    ? ExtractionColumnPrefix + rule.Name
                    : ExtractionColumnPrefix + rule.Name + "." + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                descriptors.Add(new DynamicColumnDescriptor(key, rule.Name, valueType, rule.Name, ordinal));
            }
        }

        return descriptors;
    }

    /// <summary>
    /// Builds snippet column descriptors from persisted snippet result drafts
    /// (union of declared columns across pages, first-seen order).
    /// </summary>
    /// <param name="results">Snippet result drafts.</param>
    /// <returns>Ordered, stable column descriptors.</returns>
    /// <exception cref="ArgumentNullException">Results is null.</exception>
    public static IReadOnlyList<DynamicColumnDescriptor> BuildSnippetColumns(IReadOnlyList<SnippetResultDraft> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var descriptors = new List<DynamicColumnDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SnippetResultDraft result in results)
        {
            using JsonDocument columns = JsonDocument.Parse(result.ColumnsJson);
            if (columns.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement column in columns.RootElement.EnumerateArray())
            {
                string columnKey = column.TryGetProperty("key", out JsonElement keyElement) ? keyElement.GetString() ?? string.Empty : string.Empty;
                if (columnKey.Length == 0)
                {
                    continue;
                }

                string fullKey = SnippetColumnPrefix + result.SnippetKey + "." + columnKey;
                if (!seen.Add(fullKey))
                {
                    continue;
                }

                string valueType = column.TryGetProperty("valueType", out JsonElement typeElement) ? typeElement.GetString() ?? "string" : "string";
                string label = column.TryGetProperty("label", out JsonElement labelElement) ? labelElement.GetString() ?? columnKey : columnKey;
                descriptors.Add(new DynamicColumnDescriptor(fullKey, label, valueType, result.SnippetKey, descriptors.Count));
            }
        }

        return descriptors;
    }

    /// <summary>Maps a configured extraction value type to its column type name.</summary>
    /// <param name="valueType">Configured value type.</param>
    /// <returns>Stable column type name.</returns>
    public static string MapValueType(ExtractedValueType valueType) => valueType switch
    {
        ExtractedValueType.Number => "number",
        ExtractedValueType.Boolean => "boolean",
        ExtractedValueType.DateTime => "datetime",
        _ => "text",
    };
}