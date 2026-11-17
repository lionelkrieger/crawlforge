using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Host;
using CrawlForge.Host.Execution;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Host.Tests;

/// <summary>
/// WP-06-01 reopen defects D4 + D5 wiring proof (task-44): redirect-matrix
/// re-validation replaces findings without duplicating or erasing same-pass
/// rows, and the accepted custom content family (extraction / search / JS
/// snippets) runs in live audits and persists all three result tables.
/// </summary>
public sealed class D4D5WiringTests
{
    [Fact]
    public async Task MatrixRevalidation_ReplacesFindings_KeepsAllSamePassRows()
    {
        await using var backend = await StartBackendAsync();
        var baseUrl = await StartRedirectOriginAsync(rows: 3);
        try
        {
            var (project, configuration) = await SeedAsync(backend, baseUrl);
            var audit = await SeedAuditRowAsync(backend, project.Id, configuration.Id);
            var validator = backend.App.Services.GetRequiredService<IRedirectMatrixValidator>();
            var mappings = Enumerable.Range(0, 3)
                .Select(i => new RedirectMappingDto(0, audit.Id, $"{baseUrl}old-{i}", $"{baseUrl}new-{i}", i))
                .ToList();

            var findings = backend.App.Services.GetRequiredService<IReadRepository<FindingDto>>();
            var metrics = backend.App.Services.GetRequiredService<IReadRepository<MetricSummaryDto>>();

            // Three successive validations mirror three REST result GETs: the
            // finding set is REPLACED each pass — never duplicated, and every
            // row of the current pass survives (no per-row erase).
            for (var pass = 0; pass < 3; pass++)
            {
                var results = await validator.ValidateAsync(audit.Id, mappings);
                Assert.Equal(3, results.Count);

                var matrixFindings = (await findings.ListAsync(new ListQuery(
                    0, 2000, new Dictionary<string, string> { ["auditId"] = audit.Id.ToString() })))
                    .Items.Where(f => f.RuleKey.StartsWith("redirect-matrix.", StringComparison.Ordinal)).ToList();
                Assert.Equal(3, matrixFindings.Count);

                // Aggregates stay idempotent too (delete + insert per verdict).
                var matrixMetrics = (await metrics.ListAsync(new ListQuery(
                    0, 2000, new Dictionary<string, string> { ["auditId"] = audit.Id.ToString() })))
                    .Items.Where(m => m.MetricKey == "redirect-matrix").ToList();
                Assert.Equal(matrixMetrics.Select(m => m.DimensionsJson).Distinct().Count(), matrixMetrics.Count);
            }
        }
        finally
        {
            StopOrigin();
        }
    }

    [Fact]
    public async Task CustomFamily_ObjectForm_ProducesAllThreeResultTables()
    {
        await using var backend = await StartBackendAsync();
        var baseUrl = await StartContentOriginAsync();
        try
        {
            // Object form carries all three families; Always mode lets the
            // sandboxed Jint runner execute against the STORED DOM — the engine
            // pipeline never renders, so this stays fully offline/browser-free.
            var payload = JsonSerializer.Serialize(new
            {
                rules = new[] { new { name = "pageTitle", selector = "title", valueType = 0 } },
                searchItems = new[] { new { key = "needle", pattern = "needle" } },
                snippets = new[]
                {
                    new
                    {
                        key = "linkCount",
                        name = "Link count",
                        javascript = "cf.submit('links', (cf.Html.match(/<a /g) || []).length);",
                        maxRows = 10
                    }
                }
            });
            var auditId = await RunCustomAuditAsync(backend, baseUrl, payload, RenderMode.Always);

            // Three pages are crawled (/ + two links): one row per page per
            // definition in each result table.
            var extracted = await ListAsync<ExtractedContentDto>(backend, auditId);
            var titles = extracted.Where(e => e.RuleName == "pageTitle").ToList();
            Assert.Equal(3, titles.Count);
            Assert.All(titles, t =>
            {
                Assert.False(t.IsInvalid);
                Assert.Contains("Custom Origin", t.TypedValueJson);
            });

            var searches = await ListAsync<ContentSearchResultDto>(backend, auditId);
            var needles = searches.Where(s => s.SearchItemKey == "needle").ToList();
            Assert.Equal(3, needles.Count);
            Assert.All(needles, n => Assert.True(n.MatchCount >= 1));

            var snippets = await ListAsync<SnippetResultDto>(backend, auditId);
            var linkCounts = snippets.Where(s => s.SnippetKey == "linkCount").ToList();
            Assert.Equal(3, linkCounts.Count);
            Assert.All(linkCounts, s =>
            {
                Assert.Contains("links", s.ColumnsJson);
                Assert.NotEqual("[]", s.RowsJson);
            });
        }
        finally
        {
            StopOrigin();
        }
    }

    [Fact]
    public async Task CustomFamily_ArrayForm_BackCompat_ProducesExtractionRowsOnly()
    {
        await using var backend = await StartBackendAsync();
        var baseUrl = await StartContentOriginAsync();
        try
        {
            // Legacy shape: a bare JSON array of extraction rules keeps working
            // unchanged (no search/snippet definitions implied).
            var payload = JsonSerializer.Serialize(new[]
            {
                new { name = "pageTitle", selector = "title", valueType = 0 }
            });
            var auditId = await RunCustomAuditAsync(backend, baseUrl, payload, RenderMode.None);

            var extracted = await ListAsync<ExtractedContentDto>(backend, auditId);
            Assert.Contains(extracted, e => e.RuleName == "pageTitle" && !e.IsInvalid);
            Assert.Empty(await ListAsync<ContentSearchResultDto>(backend, auditId));
            Assert.Empty(await ListAsync<SnippetResultDto>(backend, auditId));
        }
        finally
        {
            StopOrigin();
        }
    }

    [Fact]
    public async Task CustomFamily_NoneMode_SkipsSnippets_ButRunsExtractionAndSearch()
    {
        await using var backend = await StartBackendAsync();
        var baseUrl = await StartContentOriginAsync();
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                rules = new[] { new { name = "pageTitle", selector = "title", valueType = 0 } },
                searchItems = new[] { new { key = "needle", pattern = "needle" } },
                snippets = new[]
                {
                    new { key = "linkCount", name = "Link count", javascript = "cf.submit('links', 1);", maxRows = 10 }
                }
            });
            var auditId = await RunCustomAuditAsync(backend, baseUrl, payload, RenderMode.None);

            Assert.NotEmpty(await ListAsync<ExtractedContentDto>(backend, auditId));
            Assert.NotEmpty(await ListAsync<ContentSearchResultDto>(backend, auditId));
            // The runner's rendering gate: None mode never executes snippets.
            Assert.Empty(await ListAsync<SnippetResultDto>(backend, auditId));
        }
        finally
        {
            StopOrigin();
        }
    }

    [Fact]
    public async Task CustomFamily_FailingRulesAndSnippets_DoNotFailTheAudit()
    {
        await using var backend = await StartBackendAsync();
        var baseUrl = await StartContentOriginAsync();
        try
        {
            // A throwing snippet and a malformed selector: the accepted
            // components isolate rule failures, and the engine isolates the
            // family — the audit must still complete.
            var payload = JsonSerializer.Serialize(new
            {
                rules = new[] { new { name = "broken", selector = "###invalid[[[", valueType = 0 } },
                searchItems = new[] { new { key = "needle", pattern = "needle" } },
                snippets = new[]
                {
                    new { key = "boom", name = "Boom", javascript = "throw new Error('boom');", maxRows = 10 }
                }
            });
            var auditId = await RunCustomAuditAsync(backend, baseUrl, payload, RenderMode.Always);
            var audits = backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
            var audit = await audits.GetByIdAsync(auditId);
            Assert.Equal(AuditStatus.Completed, audit!.Status);
        }
        finally
        {
            StopOrigin();
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<long> RunCustomAuditAsync(
        CrawlForgeBackend backend, string baseUrl, string customPayload, RenderMode renderMode)
    {
        var projects = backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "Custom Family", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
        var configurations = backend.App.Services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>();
        var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "default", JsonSerializer.Serialize(new[] { baseUrl }),
            null, null, null, renderMode, null, null, customPayload, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
        var auditId = await execution.StartAuditAsync(project.Id, configuration.Id, ScheduleTriggerType.Manual, null);
        var audit = await WaitForCompletionAsync(backend, auditId);
        Assert.Equal(AuditStatus.Completed, audit.Status);
        return auditId;
    }

    private static async Task<IReadOnlyList<T>> ListAsync<T>(CrawlForgeBackend backend, long auditId)
        where T : class
    {
        var repository = backend.App.Services.GetRequiredService<IReadRepository<T>>();
        var page = await repository.ListAsync(new ListQuery(
            0, 2000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
        return page.Items;
    }

    private static async Task<CrawlForgeBackend> StartBackendAsync()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "crawlforge-host-tests", Guid.NewGuid().ToString("N"));
        return await CrawlForgeBackend.StartAsync(
            [$"--CrawlForge:DataDirectory={dataDirectory}", "--CrawlForge:Port=0"]);
    }

    private static async Task<(ProjectDto Project, CrawlConfigurationDto Configuration)> SeedAsync(
        CrawlForgeBackend backend, string baseUrl)
    {
        var projects = backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "D4 Matrix", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
        var configurations = backend.App.Services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>();
        var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "default", JsonSerializer.Serialize(new[] { baseUrl }),
            null, null, null, RenderMode.None, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        return (project, configuration);
    }

    private static async Task<AuditDto> SeedAuditRowAsync(CrawlForgeBackend backend, long projectId, long configurationId)
    {
        var audits = backend.App.Services.GetRequiredService<IWriteRepository<AuditDto>>();
        return await audits.CreateAsync(new AuditDto(
            0, projectId, configurationId, "{}", AuditStatus.Completed, AuditPhase.Finalizing,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "{}", "test", false));
    }

    private static async Task<AuditDto> WaitForCompletionAsync(CrawlForgeBackend backend, long auditId)
    {
        var audits = backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var audit = await audits.GetByIdAsync(auditId);
            if (audit?.Status is AuditStatus.Completed or AuditStatus.Failed or AuditStatus.Cancelled)
            {
                return audit;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Audit {auditId} did not reach a terminal state in time.");
    }

    private static HttpListener? _listener;

    private static async Task<string> StartRedirectOriginAsync(int rows)
    {
        var baseUrl = await StartListenerAsync(async context =>
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.StartsWith("/old-", StringComparison.Ordinal))
            {
                var index = path["/old-".Length..];
                context.Response.StatusCode = 301;
                context.Response.RedirectLocation = $"/new-{index}";
                context.Response.ContentLength64 = 0;
                context.Response.Close();
                return;
            }

            var body = Encoding.UTF8.GetBytes("<html><head><title>Migrated</title></head><body>target</body></html>");
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });
        _ = rows;
        return baseUrl;
    }

    private static Task<string> StartContentOriginAsync() =>
        StartListenerAsync(async context =>
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/robots.txt")
            {
                var robots = Encoding.UTF8.GetBytes("User-agent: *\nAllow: /\n");
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = robots.Length;
                await context.Response.OutputStream.WriteAsync(robots);
                context.Response.Close();
                return;
            }

            var body = Encoding.UTF8.GetBytes(
                "<html><head><title>Custom Origin</title></head><body>" +
                "<h1>Heading</h1><p>custom needle text appears here.</p>" +
                "<a href=\"/page-1\">one</a> <a href=\"/page-2\">two</a>" +
                "</body></html>");
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });

    private static async Task<string> StartListenerAsync(Func<HttpListenerContext, Task> handle)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var baseUrl = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl);
        listener.Start();
        _listener = listener;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    await handle(context);
                }
                catch (Exception)
                {
                    // Best-effort response; the crawl records transport errors.
                }
            }
        });
        return baseUrl;
    }

    private static void StopOrigin()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception)
        {
            // Best-effort origin teardown.
        }

        _listener = null;
    }
}
