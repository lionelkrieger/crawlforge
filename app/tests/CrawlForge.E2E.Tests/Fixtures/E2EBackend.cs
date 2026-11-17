using System.Net.Http.Headers;
using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Host;
using CrawlForge.Host.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlForge.E2E.Tests.Fixtures;

/// <summary>
/// One composed backend under test (WP-07-01): real Kestrel on loopback, full
/// module composition, per-launch session token, isolated temp data directory.
/// The optional <c>configure</c> hook runs AFTER module registration — it is the
/// provider slot-in point for the live-MySQL parity run (criterion #18): a live
/// server overrides IDbProvider there without any suite rework.
/// </summary>
public sealed class E2EBackend : IAsyncDisposable
{
    private E2EBackend(CrawlForgeBackend backend, string dataDirectory, HttpClient http)
    {
        Backend = backend;
        DataDirectory = dataDirectory;
        Http = http;
    }

    public CrawlForgeBackend Backend { get; }

    /// <summary>Isolated per-test data directory.</summary>
    public string DataDirectory { get; }

    /// <summary>Session-authenticated client for the REST surface.</summary>
    public HttpClient Http { get; }

    public string BaseAddress => Backend.BaseAddress;

    public string SessionToken => Backend.SessionToken;

    public IServiceProvider Services => Backend.App.Services;

    /// <summary>Set by restart tests so the first launch keeps the shared data dir.</summary>
    public bool PreserveDataDirectory { get; set; }

    public static async Task<E2EBackend> StartAsync(
        Action<WebApplicationBuilder>? configure = null,
        string? dataDirectory = null)
    {
        dataDirectory ??= Path.Combine(
            Path.GetTempPath(), "crawlforge-e2e", Guid.NewGuid().ToString("N"));
        var backend = await CrawlForgeBackend.StartAsync(
            [$"--CrawlForge:DataDirectory={dataDirectory}", "--CrawlForge:Port=0"],
            configure);

        var http = new HttpClient { BaseAddress = new Uri(backend.BaseAddress) };
        http.DefaultRequestHeaders.Add(SessionTokenMiddleware.HeaderName, backend.SessionToken);
        return new E2EBackend(backend, dataDirectory, http);
    }

    public T Service<T>() where T : class => Services.GetRequiredService<T>();

    /// <summary>Seeds a project + default configuration pointing at the seed URLs.</summary>
    public async Task<(long ProjectId, long ConfigurationId)> SeedProjectAsync(
        string seedUrl,
        string name = "E2E Project",
        CrawlLimitOverrides? limits = null,
        string? politenessJson = null,
        RenderMode renderMode = RenderMode.None,
        string? scopeRulesJson = null,
        Func<CrawlConfigurationDto, CrawlConfigurationDto>? customize = null)
    {
        var projects = Service<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, name, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));

        var configurations = Service<IWriteRepository<CrawlConfigurationDto>>();
        var configuration = new CrawlConfigurationDto(
            0, project.Id, "default", JsonSerializer.Serialize(new[] { seedUrl }),
            scopeRulesJson, null, limits, renderMode, politenessJson, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        if (customize is not null)
        {
            configuration = customize(configuration);
        }

        configuration = await configurations.CreateAsync(configuration);
        return (project.Id, configuration.Id);
    }

    /// <summary>Starts an audit through the locked execution contract.</summary>
    public Task<long> StartAuditAsync(
        long projectId,
        long configurationId,
        ScheduleTriggerType trigger = ScheduleTriggerType.Manual,
        CrawlLimitOverrides? overrides = null) =>
        Service<IAuditExecutionService>().StartAuditAsync(projectId, configurationId, trigger, overrides);

    /// <summary>Waits until the audit satisfies the condition (fails fast on Failed).</summary>
    public async Task<AuditDto> WaitForAuditAsync(
        long auditId, Func<AuditDto, bool> condition, TimeSpan? timeout = null)
    {
        var reads = Service<IReadRepository<AuditDto>>();
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var audit = await reads.GetByIdAsync(auditId);
            if (audit is not null && condition(audit))
            {
                return audit;
            }

            if (audit?.Status == AuditStatus.Failed)
            {
                throw new InvalidOperationException($"Audit {auditId} failed while waiting for the expected state.");
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Audit {auditId} did not reach the expected state in time.");
    }

    public Task<AuditDto> WaitForStatusAsync(long auditId, AuditStatus status, TimeSpan? timeout = null) =>
        WaitForAuditAsync(auditId, a => a.Status == status, timeout);

    /// <summary>Lists rows of a DTO filtered by audit id.</summary>
    public async Task<IReadOnlyList<T>> ListByAuditAsync<T>(long auditId, int limit = 500)
        where T : class
    {
        var page = await Service<IReadRepository<T>>().ListAsync(new ListQuery(
            0, limit, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
        return page.Items;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Backend.StopAsync();
        if (PreserveDataDirectory)
        {
            return;
        }

        try
        {
            if (Directory.Exists(DataDirectory))
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the per-test temp directory.
        }
    }
}

/// <summary>Shared JSON conventions for wire assertions (locked §10.1 shape).</summary>
public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Parses a response body and returns the envelope root.</summary>
    public static async Task<JsonElement> EnvelopeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
