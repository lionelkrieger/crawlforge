using System.Text.Json;
using CrawlForge.Api.Hubs;
using Xunit;

namespace CrawlForge.Api.Tests.Hubs;

/// <summary>
/// WP-09-06 (G06/G-G/G-HUB-COUNTERS): hub boundary normalization unit tests.
/// Producer payloads are the engine's storage shape (AuditRunCounters.ToPayload
/// keys, numeric entity ids); the consumer contract is the frozen frontend
/// events.ts (string EntityId, AuditCounters wire keys). These tests feed the
/// real producer shape into the real normalizer the hub dispatch uses.
/// </summary>
public sealed class HubPayloadNormalizerTests
{
    [Fact]
    public void CrawlProgress_EngineCounters_MapToRestWireKeys()
    {
        // Exact AuditRunCounters.ToPayload() producer shape + engine extras.
        var producer = """
            {"auditId":42,"counters":{"discovered":100,"fetched":50,"analyzed":48,"failed":2},"queued":7,"phase":"crawling"}
            """;

        var normalized = HubPayloadNormalizer.Normalize(HubEventCatalog.CrawlProgress, producer);

        using var document = JsonDocument.Parse(normalized);
        var root = document.RootElement;
        Assert.Equal("42", root.GetProperty("auditId").GetString());
        var counters = root.GetProperty("counters");
        Assert.Equal(100, counters.GetProperty("queued").GetInt64());   // discovered → queued
        Assert.Equal(50, counters.GetProperty("fetched").GetInt64());   // fetched → fetched
        Assert.Equal(2, counters.GetProperty("error").GetInt64());      // failed → error
        Assert.Equal(0, counters.GetProperty("blocked").GetInt64());    // absent → 0
        Assert.False(counters.TryGetProperty("wafBlocked", out _));     // absent → omitted (never null)
        Assert.False(counters.TryGetProperty("discovered", out _));     // producer keys replaced
        Assert.False(counters.TryGetProperty("analyzed", out _));       // engine-internal, no wire field
        Assert.Equal("crawling", root.GetProperty("phase").GetString()); // extras pass through
        Assert.Equal(7, root.GetProperty("queued").GetInt32());          // top-level frontier count untouched
    }

    [Fact]
    public void CrawlProgress_WafBlocked_PassesThroughWhenPresent()
    {
        var producer = """
            {"auditId":42,"counters":{"discovered":10,"fetched":8,"analyzed":8,"failed":1,"blocked":3,"wafBlocked":2}}
            """;

        var normalized = HubPayloadNormalizer.Normalize(HubEventCatalog.CrawlProgress, producer);

        using var document = JsonDocument.Parse(normalized);
        var counters = document.RootElement.GetProperty("counters");
        Assert.Equal(3, counters.GetProperty("blocked").GetInt64());
        Assert.Equal(2, counters.GetProperty("wafBlocked").GetInt64());
    }

    [Fact]
    public void CrawlProgress_Normalization_IsIdempotent()
    {
        var producer = """{"auditId":42,"counters":{"discovered":100,"fetched":50,"analyzed":48,"failed":2}}""";

        var once = HubPayloadNormalizer.Normalize(HubEventCatalog.CrawlProgress, producer);
        var twice = HubPayloadNormalizer.Normalize(HubEventCatalog.CrawlProgress, once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void CrawlProgress_LegacyWireCounters_StillParse()
    {
        // Pre-fix rows / producers already emitting wire keys keep working.
        var legacy = """{"auditId":"42","counters":{"queued":9,"fetched":4,"error":1,"blocked":2,"wafBlocked":1}}""";

        var normalized = HubPayloadNormalizer.Normalize(HubEventCatalog.CrawlProgress, legacy);

        using var document = JsonDocument.Parse(normalized);
        var counters = document.RootElement.GetProperty("counters");
        Assert.Equal(9, counters.GetProperty("queued").GetInt64());
        Assert.Equal(4, counters.GetProperty("fetched").GetInt64());
        Assert.Equal(1, counters.GetProperty("error").GetInt64());
        Assert.Equal(2, counters.GetProperty("blocked").GetInt64());
        Assert.Equal(1, counters.GetProperty("wafBlocked").GetInt64());
        Assert.Equal("42", document.RootElement.GetProperty("auditId").GetString());
    }

    [Fact]
    public void AuditCompleted_FinalCounters_MapToRestWireKeys()
    {
        var producer = """{"auditId":42,"finalCounters":{"discovered":10,"fetched":10,"analyzed":10,"failed":0},"reportReady":true}""";

        var normalized = HubPayloadNormalizer.Normalize(HubEventCatalog.AuditCompleted, producer);

        using var document = JsonDocument.Parse(normalized);
        var counters = document.RootElement.GetProperty("finalCounters");
        Assert.Equal(10, counters.GetProperty("queued").GetInt64());
        Assert.Equal(10, counters.GetProperty("fetched").GetInt64());
        Assert.Equal(0, counters.GetProperty("error").GetInt64());
        Assert.True(document.RootElement.GetProperty("reportReady").GetBoolean());
    }

    [Fact]
    public void UrlBatch_NestedUrlIds_AreStringified()
    {
        var producer = """
            {"auditId":42,"urls":[{"urlId":7,"canonicalUrl":"https://example.com/a","statusClass":"2xx","blockReason":null,"wafProvider":null},{"urlId":8,"canonicalUrl":"https://example.com/b","statusClass":"4xx"}]}
            """;

        var normalized = HubPayloadNormalizer.Normalize(HubEventCatalog.UrlBatch, producer);

        using var document = JsonDocument.Parse(normalized);
        var root = document.RootElement;
        Assert.Equal("42", root.GetProperty("auditId").GetString());
        var urls = root.GetProperty("urls");
        Assert.Equal("7", urls[0].GetProperty("urlId").GetString());
        Assert.Equal("8", urls[1].GetProperty("urlId").GetString());
        Assert.Equal("https://example.com/a", urls[0].GetProperty("canonicalUrl").GetString());
        Assert.Equal(JsonValueKind.Null, urls[0].GetProperty("blockReason").ValueKind);
    }

    [Fact]
    public void AllEntityIdFields_AcrossEvents_AreStringified()
    {
        var cases = new (string Event, string Payload, string[] IdFields)[]
        {
            (HubEventCatalog.StageChanged, """{"auditId":1,"previousState":"running","newState":"paused"}""", ["auditId"]),
            (HubEventCatalog.FindingsUpdated, """{"auditId":1,"severityDeltas":{"error":2},"categoryDeltas":{"content":2}}""", ["auditId"]),
            (HubEventCatalog.AuditFailed, """{"auditId":1,"errorCode":"engine-fault","message":"boom"}""", ["auditId"]),
            (HubEventCatalog.ScheduleFired, """{"scheduleId":3,"auditId":1,"triggerType":"manual"}""", ["scheduleId", "auditId"]),
            (HubEventCatalog.ScheduleRunCompleted, """{"scheduleId":3,"auditId":1,"outcome":"completed","completionActionResults":[]}""", ["scheduleId", "auditId"]),
            (HubEventCatalog.ExportDelivery, """{"exportId":5,"destinationId":6,"state":"delivered"}""", ["exportId", "destinationId"]),
            (HubEventCatalog.WafBlocks, """{"auditId":1,"blocks":[{"urlId":9,"canonicalUrl":"https://example.com/","provider":"cloudflare","challengeClass":"botblock","confidence":0.9}]}""", ["auditId"]),
            (HubEventCatalog.HostThrottle, """{"auditId":1,"host":"example.com","trigger":"waf-soft-block","rateBefore":2.0,"rateAfter":1.0,"concurrencyBefore":2,"concurrencyAfter":1,"ttfbSampleMs":120.5,"rollingBaselineRate":1.8}""", ["auditId"]),
            (HubEventCatalog.MatrixRowValidated, """{"auditId":1,"rows":[{"urlId":9,"sourceUrl":"https://old.example.com/","verdict":"pass"}]}""", ["auditId"]),
            (HubEventCatalog.LogImportProgress, """{"importId":4,"counters":{"linesParsed":10},"phase":"parsing"}""", ["importId"]),
            (HubEventCatalog.LogImportCompleted, """{"importId":4,"finalCounters":{"linesParsed":10},"correlation":{}}""", ["importId"]),
            (HubEventCatalog.EmbeddingProgress, """{"auditId":1,"embeddedCount":3,"totalEligible":10,"provider":"local","phase":"embedding"}""", ["auditId"]),
            (HubEventCatalog.EmbeddingCompleted, """{"auditId":1,"finalCounts":{"embedded":10,"totalEligible":10,"skipped":0},"passes":{"clusters":2,"linkOpportunities":1,"lowRelevanceLinks":0}}""", ["auditId"]),
        };

        foreach (var (eventName, payload, idFields) in cases)
        {
            var normalized = HubPayloadNormalizer.Normalize(eventName, payload);
            using var document = JsonDocument.Parse(normalized);
            foreach (var field in idFields)
            {
                Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty(field).ValueKind);
            }
        }
    }

    [Fact]
    public void HostThrottle_LockedShape_NumericFieldsUnchanged()
    {
        // HOTFIX-01 lock: rateBefore/rateAfter/concurrencyBefore/concurrencyAfter
        // stay numeric; only the entity id is stringified.
        var producer = """
            {"auditId":1,"host":"example.com","trigger":"waf-soft-block","rateBefore":2.0,"rateAfter":1.0,"concurrencyBefore":2,"concurrencyAfter":1,"ttfbSampleMs":null,"rollingBaselineRate":1.8}
            """;

        var normalized = HubPayloadNormalizer.Normalize(HubEventCatalog.HostThrottle, producer);

        using var document = JsonDocument.Parse(normalized);
        var root = document.RootElement;
        Assert.Equal(2.0, root.GetProperty("rateBefore").GetDouble());
        Assert.Equal(1.0, root.GetProperty("rateAfter").GetDouble());
        Assert.Equal(2, root.GetProperty("concurrencyBefore").GetInt32());
        Assert.Equal(1, root.GetProperty("concurrencyAfter").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ttfbSampleMs").ValueKind);
        Assert.Equal("1", root.GetProperty("auditId").GetString());
    }

    [Fact]
    public void WafBlocks_NestedUrlId_Stringified_ConfidenceUnchanged()
    {
        var producer = """{"auditId":1,"blocks":[{"urlId":9,"canonicalUrl":"https://example.com/","provider":"cloudflare","challengeClass":"botblock","confidence":0.9}]}""";

        var normalized = HubPayloadNormalizer.Normalize(HubEventCatalog.WafBlocks, producer);

        using var document = JsonDocument.Parse(normalized);
        var block = document.RootElement.GetProperty("blocks")[0];
        Assert.Equal("9", block.GetProperty("urlId").GetString());
        Assert.Equal(0.9, block.GetProperty("confidence").GetDouble());
    }

    [Fact]
    public void IdArrays_AreStringified_Elementwise()
    {
        var producer = """{"auditIds":[1,2,3],"mixedIds":[4,"5",null]}""";

        var normalized = HubPayloadNormalizer.Normalize("custom-event", producer);

        using var document = JsonDocument.Parse(normalized);
        var auditIds = document.RootElement.GetProperty("auditIds");
        Assert.Equal("1", auditIds[0].GetString());
        Assert.Equal("2", auditIds[1].GetString());
        Assert.Equal("3", auditIds[2].GetString());
        var mixed = document.RootElement.GetProperty("mixedIds");
        Assert.Equal("4", mixed[0].GetString());
        Assert.Equal("5", mixed[1].GetString());
        Assert.Equal(JsonValueKind.Null, mixed[2].ValueKind);
    }

    [Fact]
    public void NonIdNumericFields_AndNullIds_AreUntouched()
    {
        var producer = """{"auditId":null,"count":12,"grid":3,"valid":true,"name":"id"}""";

        var normalized = HubPayloadNormalizer.Normalize("custom-event", producer);

        using var document = JsonDocument.Parse(normalized);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("auditId").ValueKind);
        Assert.Equal(12, root.GetProperty("count").GetInt32());
        Assert.Equal(3, root.GetProperty("grid").GetInt32());
        Assert.True(root.GetProperty("valid").GetBoolean());
        Assert.Equal("id", root.GetProperty("name").GetString());
    }

    [Fact]
    public void MalformedOrNonObjectPayload_PassesThroughUnchanged()
    {
        Assert.Equal("{not json", HubPayloadNormalizer.Normalize(HubEventCatalog.CrawlProgress, "{not json"));
        Assert.Equal("[1,2]", HubPayloadNormalizer.Normalize(HubEventCatalog.CrawlProgress, "[1,2]"));
        Assert.Equal("null", HubPayloadNormalizer.Normalize(HubEventCatalog.CrawlProgress, "null"));
    }

    [Fact]
    public void UnknownEvent_PayloadPreservedExceptIds()
    {
        var producer = """{"auditId":1,"custom":{"nestedId":2,"value":"keep"}}""";

        var normalized = HubPayloadNormalizer.Normalize("future-event", producer);

        using var document = JsonDocument.Parse(normalized);
        var root = document.RootElement;
        Assert.Equal("1", root.GetProperty("auditId").GetString());
        Assert.Equal("2", root.GetProperty("custom").GetProperty("nestedId").GetString());
        Assert.Equal("keep", root.GetProperty("custom").GetProperty("value").GetString());
    }
}
