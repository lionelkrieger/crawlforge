using System.Text.Json;
using CrawlForge.Contracts.Api;
using CrawlForge.ContractConformance.Tests.Harness;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Crawler.Scheduling;
using CrawlForge.Host.Execution;
using Xunit;

namespace CrawlForge.ContractConformance.Tests;

/// <summary>
/// Hub event wire conformance (FS-CRAWL-001 §10.3, FS-CRAWL-002 §7.2 +
/// Appendix C) against the LIVE backend: the fixture audit, schedule run-now,
/// export delivery, log import, and matrix validation emit real events that a
/// real SignalR JSON-protocol client captures over the loopback WebSocket.
/// Producer-side locks use the real producer adapters (HubHostThrottleSink,
/// ScheduleEventPublisher) feeding a capturing IRealtimeHub. Documented
/// producer→consumer drifts are recorded in GAPS.md and skipped with reasons;
/// only the REST counter-key test (WP-09-06) is allowed to fail.
/// </summary>
[Collection(LiveBackendCollection.Name)]
public sealed class HubEventWireConformanceTests
{
    private readonly LiveBackendFixture _fixture;

    /// <summary>Creates the suite over the shared live backend.</summary>
    /// <param name="fixture">Shared live-backend fixture.</param>
    public HubEventWireConformanceTests(LiveBackendFixture fixture) => _fixture = fixture;

    /// <summary>
    /// crawl-progress live payload: spec discipline that survives the
    /// WP-09-06/WP-09-11 reconciliation — an auditId dimension, a numeric
    /// counters object including the shared "fetched" dimension, and the ≤1/s
    /// throttled cadence (§5.7). The producer-only key set and numeric id type
    /// are drift (G-PROGRESS/G-G): recorded in GAPS.md + captured payload, and
    /// asserted by the skipped frontend-contract test — never locked as
    /// expected behavior (lead ownership steer).
    /// </summary>
    [Fact]
    public async Task CrawlProgress_LivePayload_ProducerShape()
    {
        var events = _fixture.Hub.EventsNamed("crawl-progress")
            .Where(e => MatchesId(e.Payload, "auditId", _fixture.AuditId))
            .ToArray();
        Assert.True(events.Length > 0, "no crawl-progress events captured for the fixture audit");
        await _fixture.SaveCaptureAsync("hub-crawl-progress.json", JsonSerializer.SerializeToElement(
            events.Select(e => new { receivedAt = e.ReceivedAt, payload = e.Payload })));

        foreach (var captured in events)
        {
            var payload = captured.Payload;
            Assert.True(payload.TryGetProperty("auditId", out _), "crawl-progress missing auditId");
            var counters = payload.GetProperty("counters");
            Assert.Equal(JsonValueKind.Object, counters.ValueKind);
            Assert.True(counters.TryGetProperty("fetched", out var fetched), "counters missing the shared 'fetched' dimension");
            Assert.Equal(JsonValueKind.Number, fetched.ValueKind);
            foreach (var property in counters.EnumerateObject())
            {
                Assert.Equal(JsonValueKind.Number, property.Value.ValueKind);
            }
        }

        // Cadence discipline (§5.7): the producer locks ThrottleKey +
        // ThrottleInterval=1s (AuditExecutionEngine.cs:1681-1690) and the hub
        // coalesces latest-wins under a lock (RealtimeHubService.cs:100-137,
        // flush 225-255). Per-gap received-at measurement is NOT a reliable
        // emission clock: TCP Nagle/delayed-ACK can compress arrival gaps
        // (observed 202-489 ms while the average rate stayed ≤1/s). The honest
        // live assertion is the average emission rate over the capture window:
        // at most ~1 event/second (one boundary event of slack).
        var windowSeconds = (events[^1].ReceivedAt - events[0].ReceivedAt).TotalSeconds;
        Assert.True(events.Length <= windowSeconds + 2,
            $"crawl-progress average emission rate must be ≤1/s: {events.Length} events over {windowSeconds:F1} s");
    }

    /// <summary>
    /// G-PROGRESS (WP-09-06 counter keys + WP-09-11 missing fields): the frozen
    /// frontend CrawlProgressPayload (events.ts:44-61) requires string auditId,
    /// consumer counter keys, throughputPerSecond, elapsedSeconds,
    /// estimatedRemainderSeconds, phase — the live payload carries none of the
    /// throughput/phase fields and uses engine counter keys + numeric auditId.
    /// </summary>
    [Fact(Skip = "G-PROGRESS: WP-09-06 counter keys + WP-09-11 throughput/phase fields + G-G numeric auditId — see GAPS.md")]
    public void CrawlProgress_FrontendContractShape()
    {
        var payload = _fixture.Hub.EventsNamed("crawl-progress").First().Payload;
        Assert.Equal(JsonValueKind.String, payload.GetProperty("auditId").ValueKind);
        foreach (var field in new[] { "throughputPerSecond", "elapsedSeconds", "estimatedRemainderSeconds", "phase" })
        {
            Assert.True(payload.TryGetProperty(field, out _), $"crawl-progress missing frontend field '{field}'");
        }

        var counters = payload.GetProperty("counters");
        foreach (var key in new[] { "queued", "fetched", "error", "blocked" })
        {
            Assert.True(counters.TryGetProperty(key, out _), $"counters missing consumer key '{key}'");
        }
    }

    /// <summary>stage-changed live payload: lowercase lifecycle states (§10.3).</summary>
    [Fact]
    public async Task StageChanged_LivePayload_SpecShape()
    {
        var events = _fixture.Hub.EventsNamed("stage-changed")
            .Where(e => MatchesId(e.Payload, "auditId", _fixture.AuditId))
            .ToArray();
        Assert.True(events.Length > 0, "no stage-changed events captured for the fixture audit");
        await _fixture.SaveCaptureAsync("hub-stage-changed.json", JsonSerializer.SerializeToElement(
            events.Select(e => new { receivedAt = e.ReceivedAt, payload = e.Payload })));

        var states = new HashSet<string>(StringComparer.Ordinal)
        {
            "queued", "running", "paused", "completed", "cancelled", "failed"
        };
        foreach (var captured in events)
        {
            var payload = captured.Payload;
            Assert.True(payload.TryGetProperty("previousState", out var previous), "stage-changed missing previousState");
            Assert.True(payload.TryGetProperty("newState", out var next), "stage-changed missing newState");
            Assert.Contains(previous.GetString()!, states);
            Assert.Contains(next.GetString()!, states);
            Assert.Equal(previous.GetString(), previous.GetString()!.ToLowerInvariant());
            Assert.Equal(next.GetString(), next.GetString()!.ToLowerInvariant());
        }

        // The final transition must land on a terminal state.
        Assert.Contains(events[^1].Payload.GetProperty("newState").GetString(), new[] { "completed", "cancelled", "failed" });
    }

    /// <summary>audit-completed live payload: once, with finalCounters + reportReady (§10.3).</summary>
    [Fact]
    public async Task AuditCompleted_LivePayload_SpecShape()
    {
        var events = _fixture.Hub.EventsNamed("audit-completed")
            .Where(e => MatchesId(e.Payload, "auditId", _fixture.AuditId))
            .ToArray();
        var captured = Assert.Single(events);
        await _fixture.SaveCaptureAsync("hub-audit-completed.json", captured.Payload);

        var payload = captured.Payload;
        Assert.True(payload.TryGetProperty("finalCounters", out var counters), "audit-completed missing finalCounters");
        Assert.Equal(JsonValueKind.Object, counters.ValueKind);
        Assert.True(payload.GetProperty("reportReady").GetBoolean(), "fixture audit must finalize report-ready");
    }

    /// <summary>
    /// G-COMPLETED-COUNTERS (WP-09-06 family): audit-completed.finalCounters
    /// must carry the consumer AuditCounters keys. UNSKIPPED by lead (WP-09-11):
    /// WP-09-06's HubPayloadNormalizer maps finalCounters through the same
    /// AuditWireMapper.ParseCounters as REST (REST==hub by construction).
    /// </summary>
    [Fact]
    public void AuditCompleted_FrontendCountersKeys()
    {
        var payload = _fixture.Hub.EventsNamed("audit-completed").First().Payload;
        var counters = payload.GetProperty("finalCounters");
        foreach (var key in new[] { "queued", "fetched", "error", "blocked" })
        {
            Assert.True(counters.TryGetProperty(key, out _), $"finalCounters missing consumer key '{key}'");
        }
    }

    /// <summary>
    /// audit-failed contract shape (spec §10.3: auditId, errorCode, message).
    /// Live capture is skipped: the fixture has no deterministic failure
    /// trigger; the producer path is covered by Host.Tests
    /// FailedAuditVisibilityTests against the real engine.
    /// </summary>
    [Fact(Skip = "G05-adjacent: no deterministic failure trigger in the fixture; producer covered by CrawlForge.Host.Tests FailedAuditVisibilityTests")]
    public void AuditFailed_ContractShape()
    {
        var entry = PayloadInventory.Load().Hub.Events.Single(e => e.Name == "audit-failed");
        Assert.True(entry.InSpec);
        Assert.Contains("FinalizeAsync", entry.Producer, StringComparison.Ordinal);
    }

    /// <summary>system-health live heartbeat: 2 s cadence while connected (§7.2).</summary>
    [Fact]
    public async Task SystemHealth_LivePayload_Heartbeat()
    {
        var events = _fixture.Hub.EventsNamed("system-health");
        Assert.True(events.Count >= 2, "system-health heartbeat must emit while the shell is connected");
        await _fixture.SaveCaptureAsync("hub-system-health.json", JsonSerializer.SerializeToElement(
            events.Take(5).Select(e => new { receivedAt = e.ReceivedAt, payload = e.Payload })));

        foreach (var captured in events.Take(5))
        {
            var payload = captured.Payload;
            foreach (var field in new[] { "timestamp", "backendWorkingSetBytes", "managedHeapBytes" })
            {
                Assert.True(payload.TryGetProperty(field, out _), $"system-health missing producer field '{field}'");
            }

            // G-S notation divergence: accept either the locked singular
            // (current code) or the spec-table plural; the skipped
            // SystemHealth_FrontendGcCounterNames test documents the spec side.
            foreach (var gen in new[] { "0", "1", "2" })
            {
                Assert.True(
                    payload.TryGetProperty("gcGen" + gen + "Count", out _) || payload.TryGetProperty("gcGen" + gen + "Counts", out _),
                    $"system-health missing gcGen{gen} counter (either notation)");
            }

            Assert.True(payload.TryGetProperty("sqliteWalBytes", out _), "system-health missing sqliteWalBytes (nullable)");
        }

        var gap = events[1].ReceivedAt - events[0].ReceivedAt;
        Assert.True(gap >= TimeSpan.FromMilliseconds(1500) && gap <= TimeSpan.FromSeconds(4),
            $"system-health heartbeat cadence off: {gap.TotalMilliseconds:F0} ms (expected ~2000 ms)");
    }

    /// <summary>
    /// G-S (spec-vs-code naming divergence, CORRECTED): the spec table
    /// (02-realtime-websocket-hub.md:47) writes gcGen0/1/2Counts (plural), but
    /// the implementation is consistently SINGULAR on both sides — backend
    /// SystemHealthSampler.cs:21-29, frontend contracts/api/system.ts:194-196
    /// (SystemHealthPayload aliases SystemHealthSnapshot, events.ts:177),
    /// guards.ts:126, and StatusBar.tsx:122 all use gcGen*Count. No functional
    /// defect; the spec notation needs a CR or the code a rename (owner:
    /// orchestrator/lead — spec clarification, NOT a phase-12 defect).
    /// </summary>
    [Fact(Skip = "G-S: spec §7.2 table writes plural gcGen*Counts; backend+frontend+guard+StatusBar are consistently singular gcGen*Count — spec-vs-code notation divergence, see GAPS.md")]
    public void SystemHealth_FrontendGcCounterNames()
    {
        var payload = _fixture.Hub.EventsNamed("system-health").First().Payload;
        foreach (var field in new[] { "gcGen0Counts", "gcGen1Counts", "gcGen2Counts" })
        {
            Assert.True(payload.TryGetProperty(field, out _), $"system-health missing spec-table field '{field}'");
        }
    }

    /// <summary>
    /// host-throttle live payload: the HOTFIX-01 locked field names
    /// (rateBefore/rateAfter/concurrencyBefore/concurrencyAfter/ttfbSampleMs/
    /// rollingBaselineRate) on real AIMD adjustments from the fixture crawl.
    /// </summary>
    [Fact]
    public async Task HostThrottle_LivePayload_LockedNames()
    {
        var events = _fixture.Hub.EventsNamed("host-throttle");
        Assert.True(events.Count > 0, "no host-throttle events captured during the 220-URL fixture crawl (AIMD additive increases must emit)");
        await _fixture.SaveCaptureAsync("hub-host-throttle.json", JsonSerializer.SerializeToElement(
            events.Take(10).Select(e => new { receivedAt = e.ReceivedAt, payload = e.Payload })));

        foreach (var captured in events)
        {
            AssertLockedHostThrottleNames(captured.Payload);
        }
    }

    /// <summary>
    /// host-throttle producer-side lock: the real HubHostThrottleSink adapter
    /// feeding a capturing IRealtimeHub (same technique as Host.Tests
    /// WireShapeContractTests, extended with the trigger + audit dimensions).
    /// </summary>
    [Fact]
    public void HostThrottle_ProducerSide_LockedNames()
    {
        var hub = new CapturingRealtimeHub();
        var sink = new HubHostThrottleSink(hub);
        sink.Emit(new HostThrottleAdjustment(
            AuditId: 42,
            Host: "fixture.test",
            Trigger: "additive-increase",
            PreviousRate: 2.0,
            NewRate: 2.5,
            PreviousConcurrency: 1,
            NewConcurrency: 2,
            TtfbSampleMilliseconds: 15.5,
            RollingBaselineMilliseconds: 12.25,
            Timestamp: DateTimeOffset.UtcNow));

        var emitted = Assert.Single(hub.Events);
        Assert.Equal("host-throttle", emitted.Name);
        using var document = JsonDocument.Parse(emitted.PayloadJson);
        AssertLockedHostThrottleNames(document.RootElement);
        Assert.Equal("additive-increase", document.RootElement.GetProperty("trigger").GetString());
    }

    /// <summary>schedule-fired live payload: dimensions both contracts share
    /// (auditId, triggerType) from a real run-now. The producer-only field
    /// names (scheduleDefinitionId/startedAt) are G-D drift: captured as
    /// evidence, asserted only in the skipped frontend-contract test.</summary>
    [Fact]
    public async Task ScheduleFired_LivePayload_ProducerShape()
    {
        var events = await TriggerScheduleRunAsync();
        var fired = events.Single(e => e.Name == "schedule-fired");
        await _fixture.SaveCaptureAsync("hub-schedule-fired.json", fired.Payload);

        var payload = fired.Payload;
        Assert.True(payload.TryGetProperty("auditId", out _), "schedule-fired missing auditId");
        Assert.Equal("manual", payload.GetProperty("triggerType").GetString());
    }

    /// <summary>
    /// G-D: schedule-fired emits scheduleDefinitionId + numeric ids + startedAt
    /// while the frozen frontend ScheduleFiredPayload (events.ts:118,
    /// guards.ts:220) requires scheduleId + string auditId. The guard would
    /// reject the live payload.
    /// </summary>
    [Fact(Skip = "G-D: scheduleDefinitionId vs scheduleId + numeric ids (events.ts:118/guards.ts:220); suggested owner phase-14 WP-14-01 — see GAPS.md")]
    public async Task ScheduleFired_FrontendContractShape()
    {
        var events = await TriggerScheduleRunAsync();
        var payload = events.Single(e => e.Name == "schedule-fired").Payload;
        Assert.Equal(JsonValueKind.String, payload.GetProperty("scheduleId").ValueKind);
        Assert.Equal(JsonValueKind.String, payload.GetProperty("auditId").ValueKind);
    }

    /// <summary>schedule-run-completed live payload: dimensions both contracts
    /// share (auditId, triggerType, outcome). Producer-only field names
    /// (scheduleDefinitionId/completionActions/startedAt/finishedAt) are G-E
    /// drift: captured as evidence, never locked as expected behavior.</summary>
    [Fact]
    public async Task ScheduleRunCompleted_LivePayload_ProducerShape()
    {
        var events = await TriggerScheduleRunAsync();
        var completed = events.Single(e => e.Name == "schedule-run-completed");
        await _fixture.SaveCaptureAsync("hub-schedule-run-completed.json", completed.Payload);

        var payload = completed.Payload;
        Assert.True(payload.TryGetProperty("auditId", out _), "schedule-run-completed missing auditId");
        Assert.Equal("manual", payload.GetProperty("triggerType").GetString());
        Assert.Equal("success", payload.GetProperty("outcome").GetString());
    }

    /// <summary>
    /// G-E: schedule-run-completed emits scheduleDefinitionId + completionActions
    /// while the frontend (events.ts:125, guards.ts:230) requires scheduleId +
    /// completionActionResults with string ids.
    /// </summary>
    [Fact(Skip = "G-E: completionActions vs completionActionResults + scheduleDefinitionId vs scheduleId (events.ts:125/guards.ts:230); suggested owner phase-14 WP-14-01 — see GAPS.md")]
    public async Task ScheduleRunCompleted_FrontendContractShape()
    {
        var events = await TriggerScheduleRunAsync();
        var payload = events.Single(e => e.Name == "schedule-run-completed").Payload;
        Assert.Equal(JsonValueKind.String, payload.GetProperty("scheduleId").ValueKind);
        Assert.True(payload.TryGetProperty("completionActionResults", out _));
    }

    /// <summary>export-delivery live payload: the delivered state transition
    /// with both id dimensions present. Producer-only names/types
    /// (exportJobId, numeric ids, attempt/timestamp) are G-F drift: captured
    /// as evidence, asserted only in the skipped frontend-contract test.</summary>
    [Fact]
    public async Task ExportDelivery_LivePayload_ProducerShape()
    {
        var events = _fixture.Hub.EventsNamed("export-delivery")
            .Where(e => MatchesId(e.Payload, "exportJobId", _fixture.ExportId) || MatchesId(e.Payload, "exportId", _fixture.ExportId))
            .ToArray();
        Assert.True(events.Length > 0, "no export-delivery events captured for the fixture export");
        await _fixture.SaveCaptureAsync("hub-export-delivery.json", JsonSerializer.SerializeToElement(
            events.Select(e => new { receivedAt = e.ReceivedAt, payload = e.Payload })));

        var delivered = events.Single(e => e.Payload.GetProperty("state").GetString() == "delivered");
        var payload = delivered.Payload;
        Assert.True(
            payload.TryGetProperty("exportJobId", out _) || payload.TryGetProperty("exportId", out _),
            "export-delivery missing the export id dimension");
        Assert.True(payload.TryGetProperty("destinationId", out _), "export-delivery missing destinationId");
    }

    /// <summary>
    /// G-F: export-delivery must match the frozen frontend ExportDeliveryPayload
    /// (events.ts, guards.ts isExportDeliveryPayload): exportId + destinationId
    /// as strings. UNSKIPPED by lead (WP-09-11): WP-09-10 fixed the producer to
    /// the exact frozen shape; the hub normalizer guarantees string ids.
    /// </summary>
    [Fact]
    public void ExportDelivery_FrontendContractShape()
    {
        var payload = _fixture.Hub.EventsNamed("export-delivery").First().Payload;
        Assert.Equal(JsonValueKind.String, payload.GetProperty("exportId").ValueKind);
        Assert.Equal(JsonValueKind.String, payload.GetProperty("destinationId").ValueKind);
    }

    /// <summary>
    /// G-I: export-progress IS emitted on the wire by the export engine.
    /// Live coverage locks the emission + payload dimensions (exportJobId,
    /// state) that any cataloged contract would keep. The catalog/frontend
    /// ABSENCE is drift — recorded in GAPS.md and the inventory, and asserted
    /// only by the skipped ExportProgress_Uncataloged test (a passing
    /// absence-lock would fail once the event is properly cataloged).
    /// </summary>
    [Fact]
    public async Task ExportProgress_DriftRecorded()
    {
        var events = _fixture.Hub.EventsNamed("export-progress");
        Assert.True(events.Count > 0, "export engine must emit export-progress during the fixture delivery");
        await _fixture.SaveCaptureAsync("hub-export-progress-drift.json", JsonSerializer.SerializeToElement(
            events.Select(e => new { receivedAt = e.ReceivedAt, payload = e.Payload })));

        foreach (var captured in events)
        {
            var payload = captured.Payload;
            Assert.True(
                payload.TryGetProperty("exportJobId", out _) || payload.TryGetProperty("exportId", out _),
                "export-progress missing the export id dimension");
            // Producer field is "status" (ExportEngine); tolerate "state" for
            // forward compatibility with a cataloged contract.
            Assert.True(
                payload.TryGetProperty("status", out _) || payload.TryGetProperty("state", out _),
                "export-progress missing the job status dimension");
        }
    }

    /// <summary>G-I CLOSED (WP-09-10 producer + WP-09-11 catalog): export-progress
    /// is cataloged backend-side (HubEventCatalog.ExportProgress) AND in the
    /// frontend contract (events.ts additive entry), and the live payload matches
    /// the frozen shape: string exportId/auditId, format wire key, state from the
    /// frozen ExportJobState vocabulary. Replaces the former absence-lock.</summary>
    [Fact]
    public void ExportProgress_CatalogedContractShape()
    {
        var catalog = typeof(CrawlForge.Api.Hubs.HubEventCatalog)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);
        Assert.Contains("export-progress", catalog);
        var frontendTs = File.ReadAllText(RepoPaths.FrontendHubEventsTs);
        Assert.Contains("'export-progress'", frontendTs, StringComparison.Ordinal);

        var payload = _fixture.Hub.EventsNamed("export-progress").First().Payload;
        Assert.Equal(JsonValueKind.String, payload.GetProperty("exportId").ValueKind);
        Assert.Equal(JsonValueKind.String, payload.GetProperty("auditId").ValueKind);
        Assert.Equal(JsonValueKind.String, payload.GetProperty("format").ValueKind);
        Assert.Contains(payload.GetProperty("state").GetString(), new[] { "queued", "running", "completed", "failed" });
    }

    /// <summary>matrix-row-validated live payload: batched rows with the
    /// producer dimensions (urlId, sourceUrl, verdict) present (§10.8). The
    /// verdict/urlId TYPES are drift (G-MATRIX-VERDICT + G-G): the hub
    /// serializes the enum as a number and ids as numbers while the frozen
    /// frontend entry requires strings — captured as evidence, asserted only
    /// by the skipped frontend-contract test.</summary>
    [Fact]
    public async Task MatrixRowValidated_LivePayload_SpecShape()
    {
        var events = _fixture.Hub.EventsNamed("matrix-row-validated")
            .Where(e => MatchesId(e.Payload, "auditId", _fixture.MatrixAuditId))
            .ToArray();
        Assert.True(events.Length > 0, "no matrix-row-validated events captured for the results-endpoint matrix validation");
        await _fixture.SaveCaptureAsync("hub-matrix-row-validated.json", JsonSerializer.SerializeToElement(
            events.Select(e => new { receivedAt = e.ReceivedAt, payload = e.Payload })));

        foreach (var captured in events)
        {
            var payload = captured.Payload;
            var rows = payload.GetProperty("rows");
            Assert.Equal(JsonValueKind.Array, rows.ValueKind);
            Assert.True(rows.GetArrayLength() > 0, "matrix-row-validated batch must carry rows");
            foreach (var row in rows.EnumerateArray())
            {
                // Producer dimensions: MatrixRowValidatedItem (ListModeModels.cs:213).
                Assert.True(row.TryGetProperty("urlId", out _));
                Assert.True(row.TryGetProperty("sourceUrl", out var source));
                Assert.Equal(JsonValueKind.String, source.ValueKind);
                Assert.True(row.TryGetProperty("verdict", out _));
            }
        }
    }

    /// <summary>G-MATRIX-VERDICT + G-G CLOSED: the frozen frontend entry requires
    /// string verdict + string EntityId urlId/auditId. UNSKIPPED by lead (WP-09-11):
    /// WP-09-03's MatrixVerdictNames vocabulary + WP-09-06's hub normalizer
    /// (string ids) make the live payload conformant.</summary>
    [Fact]
    public void MatrixRowValidated_FrontendContractShape()
    {
        var payload = _fixture.Hub.EventsNamed("matrix-row-validated").First().Payload;
        Assert.Equal(JsonValueKind.String, payload.GetProperty("auditId").ValueKind);
        foreach (var row in payload.GetProperty("rows").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, row.GetProperty("urlId").ValueKind);
            Assert.Equal(JsonValueKind.String, row.GetProperty("verdict").ValueKind);
        }
    }

    /// <summary>log-import-progress live payload: nested counters producer shape.</summary>
    [Fact]
    public async Task LogImportProgress_LivePayload_ProducerShape()
    {
        var events = _fixture.Hub.EventsNamed("log-import-progress")
            .Where(e => MatchesId(e.Payload, "importId", _fixture.LogImportId))
            .ToArray();
        Assert.True(events.Length > 0, "no log-import-progress events captured for the fixture import");
        await _fixture.SaveCaptureAsync("hub-log-import-progress.json", JsonSerializer.SerializeToElement(
            events.Select(e => new { receivedAt = e.ReceivedAt, payload = e.Payload })));

        foreach (var captured in events)
        {
            var payload = captured.Payload;
            // Producer lock: LogImportService.PublishProgressAsync (LogImportService.cs:425).
            var counters = payload.GetProperty("counters");
            foreach (var key in new[] { "linesParsed", "linesAccepted", "linesRejected" })
            {
                Assert.True(counters.TryGetProperty(key, out var value));
                Assert.Equal(JsonValueKind.Number, value.ValueKind);
            }

            Assert.Equal(JsonValueKind.String, payload.GetProperty("phase").ValueKind);
        }
    }

    /// <summary>
    /// G-G (log-import id type, CORRECTED): the payload SHAPE conforms — the
    /// frozen interface LogImportProgressPayload (events.ts:193-197) declares
    /// NESTED counters exactly as the backend emits (LogImportService.cs:425)
    /// and the guard requires (guards.ts:297-304). The only drift is the
    /// numeric importId vs the string EntityId the guard requires
    /// (guards.ts:300) — part of the systemic G-G id-type gap.
    /// </summary>
    [Fact(Skip = "G-G: numeric importId vs string EntityId (guards.ts:300); payload shape otherwise conforms (events.ts:193-197 nested counters) — see GAPS.md")]
    public void LogImportProgress_FrontendInterfaceShape()
    {
        var payload = _fixture.Hub.EventsNamed("log-import-progress").First().Payload;
        Assert.Equal(JsonValueKind.String, payload.GetProperty("importId").ValueKind);
    }

    /// <summary>log-import-completed live payload: finalCounters + correlation (§10.3 additive).</summary>
    [Fact]
    public async Task LogImportCompleted_LivePayload_SpecShape()
    {
        var events = _fixture.Hub.EventsNamed("log-import-completed")
            .Where(e => MatchesId(e.Payload, "importId", _fixture.LogImportId))
            .ToArray();
        var captured = Assert.Single(events);
        await _fixture.SaveCaptureAsync("hub-log-import-completed.json", captured.Payload);

        var payload = captured.Payload;
        var finalCounters = payload.GetProperty("finalCounters");
        foreach (var key in new[] { "linesParsed", "linesAccepted", "linesRejected" })
        {
            Assert.True(finalCounters.TryGetProperty(key, out _));
        }

        var correlation = payload.GetProperty("correlation");
        foreach (var key in new[] { "orphanCount", "budgetWasteCount", "uncrawledIndexableCount" })
        {
            Assert.True(correlation.TryGetProperty(key, out _), $"correlation missing '{key}'");
        }
    }

    /// <summary>G05: url-batch has no producer (CODEMAP §6); contract-side inventory only.</summary>
    [Fact(Skip = "G05: url-batch has no producer in the backend (CODEMAP §6); contract-side coverage only — see GAPS.md")]
    public void UrlBatch_NoProducer_ContractSide()
    {
        AssertNoProducer("url-batch");
    }

    /// <summary>G05: findings-updated has no producer (CODEMAP §6); contract-side inventory only.</summary>
    [Fact(Skip = "G05: findings-updated has no producer in the backend (CODEMAP §6); contract-side coverage only — see GAPS.md")]
    public void FindingsUpdated_NoProducer_ContractSide()
    {
        AssertNoProducer("findings-updated");
    }

    /// <summary>G05: backend-lifecycle has no producer (CODEMAP §6); contract-side inventory only.</summary>
    [Fact(Skip = "G05: backend-lifecycle has no producer in the backend (CODEMAP §6); contract-side coverage only — see GAPS.md")]
    public void BackendLifecycle_NoProducer_ContractSide()
    {
        AssertNoProducer("backend-lifecycle");
    }

    /// <summary>G05: waf-blocks has no producer (CODEMAP §6); contract-side inventory only.</summary>
    [Fact(Skip = "G05: waf-blocks has no producer in the backend (CODEMAP §6); contract-side coverage only — see GAPS.md")]
    public void WafBlocks_NoProducer_ContractSide()
    {
        AssertNoProducer("waf-blocks");
    }

    /// <summary>G05: embedding-progress has no producer (CODEMAP §6); contract-side inventory only.</summary>
    [Fact(Skip = "G05: embedding-progress has no producer in the backend (CODEMAP §6); contract-side coverage only — see GAPS.md")]
    public void EmbeddingProgress_NoProducer_ContractSide()
    {
        AssertNoProducer("embedding-progress");
    }

    /// <summary>G05: embedding-completed has no producer (CODEMAP §6); contract-side inventory only.</summary>
    [Fact(Skip = "G05: embedding-completed has no producer in the backend (CODEMAP §6); contract-side coverage only — see GAPS.md")]
    public void EmbeddingCompleted_NoProducer_ContractSide()
    {
        AssertNoProducer("embedding-completed");
    }

    /// <summary>
    /// G-N: local-notification has a real producer (HubLocalNotificationSink).
    /// Producer-side shape lock over the REAL adapter feeding a capturing
    /// IRealtimeHub: {title, message} is the sink's own contract and survives
    /// catalog registration. The frontend/spec ABSENCE is drift — recorded in
    /// GAPS.md and asserted only by the skipped LocalNotification_NotInFrontendContract
    /// test (a passing absence-lock would fail once the event is registered).
    /// </summary>
    [Fact]
    public async Task LocalNotification_CatalogOnlyDrift()
    {
        var hub = new CapturingRealtimeHub();
        var publisher = new ScheduleEventPublisher(hub);
        var sink = new HubLocalNotificationSink(publisher);
        await sink.NotifyAsync("Conformance", "local notification shape lock");

        var emitted = Assert.Single(hub.Events);
        Assert.Equal("local-notification", emitted.Name);
        using var document = JsonDocument.Parse(emitted.PayloadJson);
        Assert.Equal("Conformance", document.RootElement.GetProperty("title").GetString());
        Assert.Equal("local notification shape lock", document.RootElement.GetProperty("message").GetString());
    }

    /// <summary>G-N CLOSED (CR-027, WP-09-11): local-notification is cataloged
    /// backend-side, produced by HubLocalNotificationSink, AND present in the
    /// frontend contract (events.ts additive entry {title,message}). Replaces
    /// the former absence-lock.</summary>
    [Fact]
    public void LocalNotification_InFrontendContract()
    {
        var frontendTs = File.ReadAllText(RepoPaths.FrontendHubEventsTs);
        Assert.Contains("'local-notification'", frontendTs, StringComparison.Ordinal);
        Assert.Equal("local-notification", CrawlForge.Api.Hubs.HubEventCatalog.LocalNotification);
    }

    /// <summary>
    /// subscribe/unsubscribe channel contract: string[] audit ids, completion
    /// without error, and audit-scoped routing (events for the subscribed audit
    /// arrive; the fixture capture proves delivery over the real WebSocket).
    /// </summary>
    [Fact]
    public async Task SubscribeUnsubscribe_ChannelContract()
    {
        var auditId = _fixture.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _fixture.Hub.InvokeChannelAsync("subscribe", new[] { auditId });
        await _fixture.Hub.InvokeChannelAsync("unsubscribe", new[] { auditId });
        await _fixture.Hub.InvokeChannelAsync("subscribe", new[] { auditId });

        // The fixture audit's events arrived over this connection (routing proof).
        Assert.True(_fixture.Hub.EventsNamed("crawl-progress").Count > 0);
        Assert.True(_fixture.Hub.EventsNamed("audit-completed").Count > 0);
    }

    /// <summary>
    /// G-G systemic id-type gap — contract side + evidence. The frozen TS
    /// guards require isString(auditId) on every audit-scoped payload
    /// (EntityId is string); this source-level contract check passes
    /// regardless of the producer's current type. The live id kinds are
    /// CAPTURED as evidence (hub-numeric-auditid-drift.json) without asserting
    /// the broken type as expected — the skipped
    /// AuditScopedEvents_CarryStringAuditId_FrontendContract test asserts the
    /// conformant shape and turns meaningful once WP-09-11 fixes the ids.
    /// </summary>
    [Fact]
    public async Task AuditScopedEvents_CarryNumericAuditId_SystemicDrift()
    {
        // Contract side: the frozen guards lock string ids (stable truth).
        var guardsTs = File.ReadAllText(RepoPaths.FrontendHubGuardsTs);
        Assert.Contains("isString(value.auditId)", guardsTs, StringComparison.Ordinal);

        // Evidence: record the live id kinds without locking the drift.
        var auditScoped = new[]
        {
            "crawl-progress", "stage-changed", "audit-completed", "host-throttle", "matrix-row-validated"
        };
        var evidence = new List<object>();
        foreach (var name in auditScoped)
        {
            var events = _fixture.Hub.EventsNamed(name);
            Assert.True(events.Count > 0, $"no live capture for {name}");
            var auditId = events[0].Payload.GetProperty("auditId");
            evidence.Add(new { name, auditIdKind = auditId.ValueKind.ToString(), sample = auditId.ToString() });
        }

        await _fixture.SaveCaptureAsync("hub-numeric-auditid-drift.json", JsonSerializer.SerializeToElement(evidence));
    }

    /// <summary>G-G CLOSED: audit-scoped hub events carry string EntityId
    /// auditIds per the frozen guards. UNSKIPPED by lead (WP-09-11): WP-09-06's
    /// HubPayloadNormalizer stringifies entity ids recursively at the hub
    /// boundary (idempotent, routing tolerant of both kinds).</summary>
    [Fact]
    public void AuditScopedEvents_CarryStringAuditId_FrontendContract()
    {
        foreach (var name in new[] { "crawl-progress", "stage-changed", "audit-completed", "host-throttle", "matrix-row-validated" })
        {
            var payload = _fixture.Hub.EventsNamed(name).First().Payload;
            Assert.Equal(JsonValueKind.String, payload.GetProperty("auditId").ValueKind);
        }
    }

    /// <summary>
    /// Id comparison tolerant of the G-G number-vs-string drift: JsonElement
    /// .ToString() yields the raw token text for both kinds, so filters keep
    /// working when producers switch to string EntityIds.
    /// </summary>
    private static bool MatchesId(JsonElement payload, string property, long expected) =>
        payload.TryGetProperty(property, out var id)
            && id.ToString() == expected.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Schedule id filter tolerant of the G-D/G-E rename drift
    /// (scheduleDefinitionId today, scheduleId in the frozen contract).</summary>
    private bool MatchesScheduleId(JsonElement payload) =>
        MatchesId(payload, "scheduleDefinitionId", _fixture.ScheduleId) || MatchesId(payload, "scheduleId", _fixture.ScheduleId);

    private static void AssertNoProducer(string name)
    {
        var entry = PayloadInventory.Load().Hub.Events.Single(e => e.Name == name);
        Assert.True(entry.InSpec);
        Assert.StartsWith("NONE", entry.Producer, StringComparison.Ordinal);
        Assert.False(entry.LiveCapture);
    }

    private static void AssertLockedHostThrottleNames(JsonElement payload)
    {
        Assert.True(payload.TryGetProperty("auditId", out _));
        Assert.True(payload.TryGetProperty("host", out _));
        Assert.True(payload.TryGetProperty("trigger", out _));
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("rateBefore").ValueKind);
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("rateAfter").ValueKind);
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("concurrencyBefore").ValueKind);
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("concurrencyAfter").ValueKind);
        Assert.True(payload.TryGetProperty("ttfbSampleMs", out _));
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("rollingBaselineRate").ValueKind);

        // Obsolete HOTFIX-01 names must be gone.
        Assert.False(payload.TryGetProperty("previousRate", out _));
        Assert.False(payload.TryGetProperty("newRate", out _));
        Assert.False(payload.TryGetProperty("ttfbSampleMilliseconds", out _));
        Assert.False(payload.TryGetProperty("rollingBaselineMilliseconds", out _));
    }

    private async Task<IReadOnlyList<CapturedHubEvent>> TriggerScheduleRunAsync()
    {
        // One shared run-now per suite instance is enough; cache the captured
        // pair so the fired/completed tests observe the same run. The REST
        // sweep / MCP tests may fire run-now first; the scheduler's documented
        // no-overlap guard then 409s until that bounded run finishes — retry
        // instead of locking the conflict as expected behavior.
        if (_scheduleRunEvents is null)
        {
            var before = _fixture.Hub.Events.Count;
            var startDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
            long runAuditId;
            while (true)
            {
                var exchange = await Rest.SendAsync(_fixture.Http, "POST", $"/api/schedules/{_fixture.ScheduleId}/run-now");
                if (exchange.Status == 409 && DateTimeOffset.UtcNow < startDeadline)
                {
                    await Task.Delay(1000);
                    continue;
                }

                Rest.AssertSuccessEnvelope(exchange);
                runAuditId = long.Parse(
                    exchange.Root.GetProperty("data").GetProperty("auditId").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture);
                break;
            }

            // Pair events to OUR run by auditId: other drivers' skipped-overlap
            // firings carry no auditId (outcome "skipped") and must not be
            // mistaken for this run's fired/completed pair.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var fresh = _fixture.Hub.Events.Skip(before).ToArray();
                var fired = fresh.Where(e => e.Name == "schedule-fired"
                    && MatchesScheduleId(e.Payload) && MatchesId(e.Payload, "auditId", runAuditId)).ToArray();
                var completed = fresh.Where(e => e.Name == "schedule-run-completed"
                    && MatchesScheduleId(e.Payload) && MatchesId(e.Payload, "auditId", runAuditId)).ToArray();
                if (fired.Length > 0 && completed.Length > 0)
                {
                    _scheduleRunEvents = new[] { fired[0], completed[0] };
                    break;
                }

                await Task.Delay(200);
            }

            if (_scheduleRunEvents is null)
            {
                throw new TimeoutException("schedule-fired / schedule-run-completed not captured within 120 s of run-now");
            }
        }

        return _scheduleRunEvents;
    }

    private IReadOnlyList<CapturedHubEvent>? _scheduleRunEvents;

    private sealed class CapturingRealtimeHub : IRealtimeHub
    {
        public List<HubEvent> Events { get; } = [];

        public Task PublishAsync(HubEvent hubEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(hubEvent);
            return Task.CompletedTask;
        }
    }
}
