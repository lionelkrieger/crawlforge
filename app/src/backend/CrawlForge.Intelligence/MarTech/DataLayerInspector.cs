using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// dataLayer inspector (FS-CRAWL-002 §14.2/§14.3, locked
/// <see cref="IDataLayerInspector"/>): consumes the keystone
/// datalayer_snapshot element group, extracts bounded event observations,
/// validates payloads against the optional user-supplied JSON schema, and
/// raises analytics.datalayer-malformed findings. Requires rendering mode
/// for the post-render pass; without it the pass is marked unavailable with
/// an explicit diagnostic notice (§14.2). Offline and bounded throughout.
/// </summary>
public sealed class DataLayerInspector : IDataLayerInspector
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IElementInventoryExtractor _inventoryExtractor;
    private readonly MarTechOptions _options;

    /// <summary>Creates the inspector.</summary>
    /// <param name="inventoryExtractor">Keystone element inventory extractor (composition).</param>
    /// <param name="options">Local options; null uses enabled defaults.</param>
    public DataLayerInspector(
        IElementInventoryExtractor inventoryExtractor,
        MarTechOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inventoryExtractor);
        _inventoryExtractor = inventoryExtractor;
        _options = options ?? new MarTechOptions();
    }

    /// <inheritdoc />
    public async Task<DataLayerInspectionResult> InspectAsync(
        PageAuditContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = _options.WithConfiguration(context.Configuration);
        if (!options.Enabled)
        {
            return new DataLayerInspectionResult(null, true, [], []);
        }

        if (context.Configuration.RenderingMode == RenderMode.None)
        {
            return new DataLayerInspectionResult(
                null,
                true,
                [],
                [MarTechFindingFactory.RequiresRenderingNotice(
                    context.Url.Id, "datalayer-inspection", "rendering mode is disabled for this audit.")]);
        }

        var document = context.RenderedDocument ?? context.Document;
        if (document is null)
        {
            return new DataLayerInspectionResult(null, true, [], []);
        }

        var inventory = await _inventoryExtractor.ExtractAsync(document, cancellationToken).ConfigureAwait(false);
        var snapshotItem = inventory.FirstOrDefault(item => item.Group == ElementGroups.DataLayerSnapshot);
        if (snapshotItem is null)
        {
            return new DataLayerInspectionResult(null, true, [], []);
        }

        int pushCount;
        int totalBytes;
        List<string> snippets;
        try
        {
            using var payload = JsonDocument.Parse(snapshotItem.PayloadJson);
            var root = payload.RootElement;
            pushCount = root.TryGetProperty("pushCount", out var pushElement) ? pushElement.GetInt32() : 0;
            totalBytes = root.TryGetProperty("totalBytes", out var bytesElement) ? bytesElement.GetInt32() : 0;
            snippets = root.TryGetProperty("snippets", out var snippetsElement)
                && snippetsElement.ValueKind == JsonValueKind.Array
                ? snippetsElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return new DataLayerInspectionResult(null, true, [], []);
        }

        // Keystone snippets overlap (each starts at a dataLayer occurrence
        // and includes all later pushes), so event extraction parses the
        // full inline script texts for exact, non-duplicated push positions;
        // the group remains the authoritative pushCount/totalBytes source.
        var scriptTexts = EnumerateInlineScripts(document.Root)
            .Select(script => script.TextContent)
            .Where(text => text.Contains("dataLayer", StringComparison.Ordinal))
            .ToList();
        var parsed = DataLayerParser.Parse(scriptTexts, options.DataLayerEventBound, options.EvidenceExcerptBound);
        var schemaIssues = DataLayerParser.ValidateSchema(options.DataLayerSchemaJson, parsed.Events);
        var issues = parsed.SyntaxIssues.Concat(schemaIssues).ToList();
        var wellFormed = issues.Count == 0;

        var snapshotJson = JsonSerializer.Serialize(new
        {
            source = context.RenderedDocument is not null ? "rendered" : "static",
            pushCount,
            eventCount = parsed.PushCount,
            events = parsed.Events.Select(dataLayerEvent => new
            {
                sequence = dataLayerEvent.Sequence,
                @event = dataLayerEvent.EventName,
                excerpt = dataLayerEvent.PayloadExcerpt,
                wellFormed = dataLayerEvent.WellFormed,
            }),
            truncated = parsed.Truncated,
            snippetCount = snippets.Count,
            totalBytes,
            catalogueVersion = MarTechCatalogue.Version,
        }, PayloadOptions);

        var findings = new List<FindingDraft>();
        if (!wellFormed)
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["issueCount"] = issues.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["schemaConfigured"] = string.IsNullOrWhiteSpace(options.DataLayerSchemaJson) ? "false" : "true",
            };
            for (var i = 0; i < Math.Min(issues.Count, 10); i++)
            {
                details[$"issue[{i}]"] = issues[i];
            }

            findings.Add(MarTechFindingFactory.Create(
                MarTechRuleKeys.DataLayerMalformed,
                FindingSeverity.Warning,
                context.Url.Id,
                new MarTechEvidence(
                    MarTechRuleKeys.DataLayerMalformed,
                    Vendor: null,
                    ContainerId: null,
                    $"dataLayer payloads are malformed or violate the configured schema ({issues.Count} issue(s)).",
                    details)));
        }

        return new DataLayerInspectionResult(snapshotJson, wellFormed, issues, findings);
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
