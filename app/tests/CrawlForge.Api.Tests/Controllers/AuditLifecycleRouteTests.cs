using System.Net;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// WP-09-06 D2: run lifecycle through REST — engine-port delegation,
/// checkpoint persistence, crash-recovery resume, G10 persist-before-start,
/// and audit-failed/stage-changed publication. In-process TestServer over the
/// real SQLite stack; the lifecycle double mirrors the engine contract
/// (pause checkpoints; resume rehydrates from the latest checkpoint when no
/// run survives in memory).
/// </summary>
public sealed class AuditLifecycleRouteTests
{
    [Fact]
    public async Task Pause_DelegatesToEnginePort_PersistsPaused_AndCheckpoints()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Running, AuditPhase.Crawling,
            "{\"discovered\":40,\"fetched\":20,\"analyzed\":18,\"failed\":1}");
        host.Fakes.Lifecycle.TrackRun(audit.Id);

        var response = await host.Client.PostAsync($"/api/audits/{audit.Id}/pause", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("paused", (await response.ReadDataAsync<AuditStateResponse>()).State);
        Assert.Contains((audit.Id, "pause"), host.Fakes.Lifecycle.Calls);
        var stored = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(audit.Id);
        Assert.Equal(AuditStatus.Paused, stored!.Status);

        // Checkpoint persistence: frontier + counters + AIMD state are durable.
        var checkpoints = await host.Get<IReadRepository<CrawlCheckpointDto>>().ListAsync(new ListQuery(
            0, 10, new Dictionary<string, string> { ["auditId"] = audit.Id.ToString() }, "SavedAt", true));
        var checkpoint = Assert.Single(checkpoints.Items);
        Assert.False(string.IsNullOrWhiteSpace(checkpoint.SerializedFrontier));
        Assert.Equal("{\"discovered\":40,\"fetched\":20,\"analyzed\":18,\"failed\":1}", checkpoint.CountersJson);
        Assert.False(string.IsNullOrWhiteSpace(checkpoint.AimdStateJson));
    }

    [Fact]
    public async Task Resume_InMemoryRun_ReleasesGate_WithoutRehydration()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Running, AuditPhase.Crawling);
        host.Fakes.Lifecycle.TrackRun(audit.Id);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync($"/api/audits/{audit.Id}/pause", content: null)).StatusCode);
        var response = await host.Client.PostAsync($"/api/audits/{audit.Id}/resume", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("running", (await response.ReadDataAsync<AuditStateResponse>()).State);
        Assert.Contains((audit.Id, "resume"), host.Fakes.Lifecycle.Calls);
        Assert.Empty(host.Fakes.Lifecycle.Rehydrations); // the live run was released, not rebuilt
        Assert.Equal(AuditStatus.Running, (await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(audit.Id))!.Status);
    }

    [Fact]
    public async Task CrashRecovery_ResumeRehydratesFromLatestCheckpoint()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Running, AuditPhase.Crawling,
            "{\"discovered\":90,\"fetched\":60,\"analyzed\":55,\"failed\":2}");

        // Two checkpoints written during the run; the crash happens after the newer one.
        var checkpoints = host.Get<IWriteRepository<CrawlCheckpointDto>>();
        await checkpoints.CreateAsync(new CrawlCheckpointDto(
            0, audit.Id, "{\"entries\":[{\"url\":\"https://example.com/old\"}]}", null,
            "{\"discovered\":50,\"fetched\":30,\"analyzed\":28,\"failed\":1}", DateTimeOffset.UtcNow.AddMinutes(-10))
        { AimdStateJson = "{\"hosts\":[{\"host\":\"example.com\",\"rate\":1.0}]}" });
        var newer = await checkpoints.CreateAsync(new CrawlCheckpointDto(
            0, audit.Id, "{\"entries\":[{\"url\":\"https://example.com/new\"}]}", "seen-set-ref",
            "{\"discovered\":90,\"fetched\":60,\"analyzed\":55,\"failed\":2}", DateTimeOffset.UtcNow.AddMinutes(-5))
        { AimdStateJson = "{\"hosts\":[{\"host\":\"example.com\",\"rate\":0.5}]}" });

        // Crash: the process dies (no run survives) and startup recovery marks
        // the orphan Paused (engine StartAsync semantics).
        host.Fakes.Lifecycle.SimulateProcessRestart();
        var auditWrites = host.Get<IWriteRepository<AuditDto>>();
        var current = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(audit.Id);
        await auditWrites.UpdateAsync(current! with { Status = AuditStatus.Paused });

        var response = await host.Client.PostAsync($"/api/audits/{audit.Id}/resume", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("running", (await response.ReadDataAsync<AuditStateResponse>()).State);
        var rehydration = Assert.Single(host.Fakes.Lifecycle.Rehydrations);
        Assert.Equal(audit.Id, rehydration.AuditId);
        Assert.Equal(newer.Id, rehydration.CheckpointId);
        Assert.Equal("{\"entries\":[{\"url\":\"https://example.com/new\"}]}", rehydration.Frontier);
        Assert.Equal("{\"discovered\":90,\"fetched\":60,\"analyzed\":55,\"failed\":2}", rehydration.Counters);
        Assert.Equal("{\"hosts\":[{\"host\":\"example.com\",\"rate\":0.5}]}", rehydration.Aimd);
        Assert.Equal(AuditStatus.Running, (await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(audit.Id))!.Status);
    }

    [Fact]
    public async Task Cancel_DelegatesToEnginePort_AndTerminalStatesStayImmutable()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Running, AuditPhase.Crawling);
        host.Fakes.Lifecycle.TrackRun(audit.Id);

        var response = await host.Client.PostAsync($"/api/audits/{audit.Id}/cancel", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cancelled", (await response.ReadDataAsync<AuditStateResponse>()).State);
        Assert.Contains((audit.Id, "cancel"), host.Fakes.Lifecycle.Calls);
        var stored = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(audit.Id);
        Assert.Equal(AuditStatus.Cancelled, stored!.Status);
        Assert.NotNull(stored.FinishedAt);

        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"/api/audits/{audit.Id}/pause", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"/api/audits/{audit.Id}/resume", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"/api/audits/{audit.Id}/cancel", content: null)).StatusCode);
    }

    [Fact]
    public async Task Start_ListMode_PersistsUrlListBeforeEngineObserves()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (_, configuration) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync("/api/audits", new AuditStartRequest(
            configuration.Id.ToString(), null,
            new[] { "https://example.com/list-a", "https://example.com/list-b" }, true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var start = await response.ReadDataAsync<AuditStartResponse>();

        // G10: the port persisted the list ATOMICALLY with row creation; the
        // simulated engine observation (inside the start call, before the
        // controller's post-start update) already saw the full list.
        Assert.Empty(host.Fakes.AuditExecution.Starts); // locked start contract NOT used for list mode
        var listStart = Assert.Single(host.Fakes.AuditExecution.ListStarts);
        Assert.True(listStart.IsListMode);
        Assert.Contains("https://example.com/list-a", listStart.UrlListJson);
        var observation = Assert.Single(host.Fakes.AuditExecution.EngineObservations);
        Assert.Equal(long.Parse(start.AuditId), observation.AuditId);
        Assert.Contains("https://example.com/list-a", observation.ObservedUrlListJson);
        Assert.Contains("https://example.com/list-b", observation.ObservedUrlListJson);
        Assert.True(observation.ObservedIsListMode);

        var stored = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(long.Parse(start.AuditId));
        Assert.True(stored!.IsListMode);
        Assert.Contains("https://example.com/list-b", stored.UrlListJson);
    }

    [Fact]
    public async Task Start_ListModePostCreateFailure_PublishesAuditFailedWithDetail()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (_, configuration) = await host.SeedProjectAsync();
        host.Fakes.AuditExecution.FailListStartAfterCreate = true;

        var response = await host.Client.PostJsonAsync("/api/audits", new AuditStartRequest(
            configuration.Id.ToString(), null, new[] { "https://example.com/x" }, true));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertErrorShape(document.RootElement, "internal");

        // audit-failed carries the created audit id + error detail (events.ts shape).
        var failed = Assert.Single(host.Fakes.Hub.Events, e => e.Name == "audit-failed");
        using var payload = JsonDocument.Parse(failed.PayloadJson);
        var observation = Assert.Single(host.Fakes.AuditExecution.EngineObservations);
        Assert.Equal(observation.AuditId.ToString(), payload.RootElement.GetProperty("auditId").ToString());
        Assert.Equal("start-failed", payload.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("message").GetString()));
        Assert.NotNull(await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(observation.AuditId));
    }

    [Fact]
    public async Task FallbackWithoutPorts_ListStartStillPersists_AfterStart()
    {
        // Pre-wiring behavior (Host has not registered the ports yet): the
        // locked start contract is used and the list payload is persisted
        // post-start. Documents the fallback the G10 port closes.
        await using var host = await ControllerTestHost.CreateAsync(registerEnginePorts: false);
        var (_, configuration) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync("/api/audits", new AuditStartRequest(
            configuration.Id.ToString(), null, new[] { "https://example.com/fallback" }, true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var start = await response.ReadDataAsync<AuditStartResponse>();
        Assert.Single(host.Fakes.AuditExecution.Starts);
        Assert.Empty(host.Fakes.AuditExecution.ListStarts);
        var stored = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(long.Parse(start.AuditId));
        Assert.True(stored!.IsListMode);
        Assert.Contains("https://example.com/fallback", stored.UrlListJson);
    }

    [Fact]
    public async Task FallbackWithoutPorts_TransitionsPublishStageChanged()
    {
        await using var host = await ControllerTestHost.CreateAsync(registerEnginePorts: false);
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Running, AuditPhase.Crawling);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync($"/api/audits/{audit.Id}/pause", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync($"/api/audits/{audit.Id}/resume", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync($"/api/audits/{audit.Id}/cancel", content: null)).StatusCode);

        var stages = host.Fakes.Hub.Events.Where(e => e.Name == "stage-changed").ToArray();
        Assert.Equal(3, stages.Length);
        var transitions = stages.Select(e =>
        {
            using var payload = JsonDocument.Parse(e.PayloadJson);
            return (Previous: payload.RootElement.GetProperty("previousState").GetString(),
                    New: payload.RootElement.GetProperty("newState").GetString());
        }).ToArray();
        Assert.Equal(("running", "paused"), transitions[0]);
        Assert.Equal(("paused", "running"), transitions[1]);
        Assert.Equal(("running", "cancelled"), transitions[2]);
    }

    [Fact]
    public async Task PortTransitions_RestDoesNotDoublePublishStageChanged()
    {
        // When the engine port owns the transition, the engine owns stage-changed
        // emission (handoff recipe); REST must not add a second event.
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Running, AuditPhase.Crawling);
        host.Fakes.Lifecycle.TrackRun(audit.Id);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync($"/api/audits/{audit.Id}/pause", content: null)).StatusCode);

        Assert.DoesNotContain(host.Fakes.Hub.Events, e => e.Name == "stage-changed");
    }
}
