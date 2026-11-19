using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Api.Controllers;

namespace CrawlForge.Api.Hubs;

/// <summary>
/// WP-09-06 wire normalization at the hub boundary (G06/G-G/G-HUB-COUNTERS).
/// The engine emits storage-shaped payloads (numeric entity ids, counters in
/// <c>AuditRunCounters.ToPayload()</c> keys discovered/fetched/analyzed/failed);
/// the frozen frontend contract (<c>contracts/hub/events.ts</c>) requires
/// string <c>EntityId</c> values and the REST <c>AuditCounters</c> keys
/// queued/fetched/error/blocked(+wafBlocked). This normalizer reconciles the
/// two at the Api hub boundary so the live payload matches the REST snapshot
/// key-for-key without touching the locked engine producer:
/// <list type="bullet">
/// <item>crawl-progress <c>counters</c> and audit-completed <c>finalCounters</c>
/// are projected through the SAME <see cref="AuditWireMapper.ParseCounters"/>
/// mapping the REST surface uses (producer keys first, legacy wire keys
/// accepted — idempotent).</item>
/// <item>Every entity identifier field (any property whose name ends in
/// <c>Id</c>, or <c>Ids</c> arrays) is stringified recursively, matching
/// <c>EntityId = string</c> (G-G).</item>
/// <item>All other fields pass through unchanged; unknown events keep their
/// payloads except for id stringification. Malformed JSON passes through
/// untouched (the hub never drops an event over payload shape).</item>
/// </list>
/// The engine-side emission recipe (handoff to lead) may later emit the wire
/// shape directly; normalization is idempotent so both producers coexist.
/// </summary>
public static class HubPayloadNormalizer
{
    /// <summary>
    /// Normalizes one server-event payload to the frozen wire shape.
    /// </summary>
    /// <param name="eventName">Hub event name (wire discriminant).</param>
    /// <param name="payloadJson">Producer payload JSON.</param>
    /// <returns>The normalized payload JSON; the input when not a JSON object.</returns>
    public static string Normalize(string eventName, string payloadJson)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return payloadJson;
        }

        if (node is not JsonObject root)
        {
            return payloadJson;
        }

        if (string.Equals(eventName, HubEventCatalog.CrawlProgress, StringComparison.Ordinal)
            && root["counters"] is JsonObject progressCounters)
        {
            root["counters"] = ToWireCounters(progressCounters);
        }
        else if (string.Equals(eventName, HubEventCatalog.AuditCompleted, StringComparison.Ordinal)
            && root["finalCounters"] is JsonObject finalCounters)
        {
            root["finalCounters"] = ToWireCounters(finalCounters);
        }

        StringifyEntityIds(root);
        return root.ToJsonString();
    }

    /// <summary>
    /// Projects a counters object through the shared REST mapping. The four
    /// baseline keys are always numeric; <c>wafBlocked</c> is written only
    /// when the producer supplies it (the conformance invariant requires
    /// every present counters member to be a number, never null).
    /// </summary>
    private static JsonObject ToWireCounters(JsonObject counters)
    {
        var wire = AuditWireMapper.ParseCounters(counters.ToJsonString());
        var result = new JsonObject
        {
            ["queued"] = wire.Queued,
            ["fetched"] = wire.Fetched,
            ["error"] = wire.Error,
            ["blocked"] = wire.Blocked,
        };
        if (wire.WafBlocked is { } wafBlocked)
        {
            result["wafBlocked"] = wafBlocked;
        }

        return result;
    }

    /// <summary>
    /// Recursively converts numeric entity identifiers to strings (G-G):
    /// properties ending in <c>Id</c> with number values, and <c>Ids</c>
    /// arrays with number elements. Strings, nulls, and non-id fields are
    /// untouched, so the transform is idempotent.
    /// </summary>
    private static void StringifyEntityIds(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (property.Value is null)
                    {
                        continue;
                    }

                    if (property.Key.EndsWith("Id", StringComparison.Ordinal)
                        && property.Value.GetValueKind() == JsonValueKind.Number)
                    {
                        obj[property.Key] = property.Value.GetValue<long>().ToString(CultureInfo.InvariantCulture);
                    }
                    else if (property.Key.EndsWith("Ids", StringComparison.Ordinal) && property.Value is JsonArray ids)
                    {
                        for (var i = 0; i < ids.Count; i++)
                        {
                            if (ids[i] is { } item && item.GetValueKind() == JsonValueKind.Number)
                            {
                                ids[i] = item.GetValue<long>().ToString(CultureInfo.InvariantCulture);
                            }
                        }
                    }
                    else
                    {
                        StringifyEntityIds(property.Value);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    StringifyEntityIds(item);
                }

                break;
        }
    }
}
