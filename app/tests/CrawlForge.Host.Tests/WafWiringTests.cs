using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Host;
using CrawlForge.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Host.Tests;

/// <summary>
/// WP-06-01 reopen fix proof (third wiring defect): the composed engine
/// persists WAF verdicts through the accepted Crawler recording surfaces —
/// detection row + blocked classification + per-URL finding, suspected-challenge
/// Notice for single-facet hits, and the site-wide block-rate diagnostic.
/// D-021 (WP-10-04): a hard challenge class stops its host for the rest of the
/// audit (WafBlockPolicy -> WafBlockResponse.HostStop -> AuditRunState.StoppedHosts),
/// so the challenge page and the single-facet suspected page are served from two
/// different loopback host names — the suspected notice must not depend on the
/// stopped host.
/// </summary>
public sealed class WafWiringTests
{
    [Fact]
    public async Task ChallengeAudit_PersistsDetection_Findings_AndBlockRateDiagnostic()
    {
        // Two loopback origins (D-021): the challenge host serves a clean index
        // that links one corroborated Cloudflare challenge page and one
        // still-queued /late-page; the second host serves the single-facet
        // suspected page. The hard interstitial on /challenge stops the
        // challenge host (its Host key is 127.0.0.1 — a second port would share
        // the key, a second host name does not), so /suspected must live on the
        // other host. /late-page, declared after /challenge on the index,
        // proves the stop: the frontier dequeues in link order (FIFO within a
        // host queue) and drains the queued entry without any request.
        using var challengeSite = new LoopbackSite("127.0.0.1", new Dictionary<string, SitePage>
        {
            ["/robots.txt"] = new SitePage("User-agent: *\nAllow: /\n", ContentType: "text/plain"),
            ["/challenge"] = new SitePage(
                "<html><head><title>Challenge</title></head><body><div>challenge-platform</div></body></html>",
                CfMitigated: "challenge"),
            ["/late-page"] = new SitePage(
                "<html><head><title>Late</title></head><body>queued after the hard block</body></html>"),
            ["/"] = new SitePage(
                "<html><head><title>Index</title></head><body>" +
                "<a href=\"/challenge\">challenge</a> <a href=\"/late-page\">late</a></body></html>"),
        });
        using var suspectedSite = new LoopbackSite("localhost", new Dictionary<string, SitePage>
        {
            ["/robots.txt"] = new SitePage("User-agent: *\nAllow: /\n", ContentType: "text/plain"),
            ["/suspected"] = new SitePage(
                "<html><head><title>Suspected</title></head><body><p>Just a moment — please wait.</p></body></html>"),
            ["/"] = new SitePage(
                "<html><head><title>Index</title></head><body><a href=\"/suspected\">suspected</a></body></html>"),
        });

        var dataDirectory = Path.Combine(Path.GetTempPath(), "crawlforge-host-tests", Guid.NewGuid().ToString("N"));
        var channel = new RecordingHubChannel();
        var backend = await CrawlForgeBackend.StartAsync(
            [$"--CrawlForge:DataDirectory={dataDirectory}", "--CrawlForge:Port=0"],
            builder => builder.Services.AddSingleton<IHubClientChannel>(channel));
        try
        {
            var projects = backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
            var project = await projects.CreateAsync(new ProjectDto(
                0, "WAF Wiring", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            var configurations = backend.App.Services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>();
            var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
                0, project.Id, "default",
                JsonSerializer.Serialize(new[] { challengeSite.BaseUrl, suspectedSite.BaseUrl }),
                null, null, null, RenderMode.None, null, null, null, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

            var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
            var auditId = await execution.StartAuditAsync(project.Id, configuration.Id, ScheduleTriggerType.Manual, null);

            var audits = backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
            AuditDto? audit = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                audit = await audits.GetByIdAsync(auditId);
                if (audit?.Status is AuditStatus.Completed or AuditStatus.Failed)
                {
                    break;
                }

                await Task.Delay(100);
            }

            Assert.NotNull(audit);
            Assert.Equal(AuditStatus.Completed, audit!.Status);

            // Detection row persisted with provider attribution.
            var detections = await backend.App.Services.GetRequiredService<IReadRepository<WafDetectionDto>>()
                .ListAsync(new ListQuery(0, 50, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
            var detection = Assert.Single(detections.Items);
            Assert.Equal(WafProvider.Cloudflare, detection.Provider);
            Assert.Equal(WafChallengeClass.Interstitial, detection.ChallengeClass);

            // Blocked classification on the URL record.
            var urls = await backend.App.Services.GetRequiredService<IReadRepository<UrlRecordDto>>()
                .ListAsync(new ListQuery(0, 50, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
            var blocked = Assert.Single(urls.Items, u => u.CanonicalUrl.EndsWith("/challenge", StringComparison.Ordinal));
            Assert.Equal(CrawlStatusClass.Blocked, blocked.StatusClass);
            Assert.Equal("waf-challenge", blocked.BlockReason);

            // Per-URL challenge finding + suspected Notice for the single-facet page.
            var findings = await backend.App.Services.GetRequiredService<IReadRepository<FindingDto>>()
                .ListAsync(new ListQuery(0, 100, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
            Assert.Contains(findings.Items, f => f.RuleKey == FindingRuleKeys.WafChallengeDetected && f.UrlId == blocked.Id);
            Assert.Contains(findings.Items, f => f.RuleKey == FindingRuleKeys.SuspectedChallenge && f.Severity == FindingSeverity.Notice);

            // Block-rate diagnostic materialized its metrics (sample below the
            // 20-URL alert threshold: metrics present, no site-wide alert).
            var metrics = await backend.App.Services.GetRequiredService<IReadRepository<MetricSummaryDto>>()
                .ListAsync(new ListQuery(0, 100, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
            Assert.Contains(metrics.Items, m => m.MetricKey == MetricKeys.WafBlocks && m.Value >= 1);
            Assert.Contains(metrics.Items, m => m.MetricKey == MetricKeys.WafBlockRate);

            // WP-09-11 (G05): the waf-blocks hub producer emits the frozen
            // batch shape — string auditId + blocks[] with string urlId,
            // canonicalUrl, wire provider/challengeClass, numeric confidence.
            var wafEvents = channel.Events.Where(e => e.Name == HubEventCatalog.WafBlocks).ToList();
            Assert.True(wafEvents.Count > 0, "no waf-blocks hub events emitted for the challenge audit");
            var wafPayload = JsonDocument.Parse(wafEvents[0].PayloadJson).RootElement;
            Assert.Equal(JsonValueKind.String, wafPayload.GetProperty("auditId").ValueKind);
            Assert.Equal(auditId.ToString(), wafPayload.GetProperty("auditId").GetString());
            var blocks = wafPayload.GetProperty("blocks");
            Assert.Equal(JsonValueKind.Array, blocks.ValueKind);
            var challengeBlock = blocks.EnumerateArray().Single(b =>
                b.GetProperty("canonicalUrl").GetString()!.EndsWith("/challenge", StringComparison.Ordinal));
            Assert.Equal(JsonValueKind.String, challengeBlock.GetProperty("urlId").ValueKind);
            Assert.Equal(blocked.Id.ToString(), challengeBlock.GetProperty("urlId").GetString());
            Assert.Equal("cloudflare", challengeBlock.GetProperty("provider").GetString());
            Assert.Equal("interstitial", challengeBlock.GetProperty("challengeClass").GetString());
            Assert.Equal(JsonValueKind.Number, challengeBlock.GetProperty("confidence").ValueKind);

            // The same flush cadence produces url-batch and findings-updated.
            Assert.Contains(channel.Events, e => e.Name == HubEventCatalog.UrlBatch);
            Assert.Contains(channel.Events, e => e.Name == HubEventCatalog.FindingsUpdated);

            // D-021: hard WAF class stops the host
            // After the hard-blocked /challenge fetch no further request reaches
            // the challenge host — the linked /late-page stays queued and is
            // drained at dequeue without any request.
            var challengeRequests = challengeSite.Requests.ToList();
            var blockIndex = challengeRequests.FindIndex(path => path == "/challenge");
            Assert.True(
                blockIndex >= 0 && blockIndex == challengeRequests.Count - 1,
                "the challenge host must receive no request after the hard-blocked /challenge fetch; requests: "
                + string.Join(", ", challengeRequests));
        }
        finally
        {
            await backend.StopAsync();
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    /// <summary>
    /// One loopback test site: a fixed path map served over an HttpListener
    /// plus a request log (the D-021 host-stop proof reads it). Unknown paths
    /// serve the "/" page, matching the original single-site fixture.
    /// </summary>
    private sealed class LoopbackSite : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly IReadOnlyDictionary<string, SitePage> _pages;
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly Task _serve;

        public LoopbackSite(string hostName, IReadOnlyDictionary<string, SitePage> pages)
        {
            _pages = pages;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://{hostName}:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serve = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        /// <summary>Absolute path of every request this site received, in arrival order.</summary>
        public IReadOnlyList<string> Requests => _requests.ToList();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? "/";
                _requests.Enqueue(path);
                var page = _pages.TryGetValue(path, out var mapped) ? mapped : _pages["/"];
                if (page.ContentType is not null)
                {
                    context.Response.ContentType = page.ContentType;
                }

                if (page.CfMitigated is not null)
                {
                    context.Response.Headers["cf-mitigated"] = page.CfMitigated;
                }

                var bytes = Encoding.UTF8.GetBytes(page.Body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            try
            {
                _serve.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Shutdown race.
            }
        }
    }

    private sealed record SitePage(string Body, string? ContentType = null, string? CfMitigated = null);

    private sealed class RecordingHubChannel : IHubClientChannel
    {
        public ConcurrentQueue<(string Name, string PayloadJson)> Events { get; } = new();

        public Task SendAllAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default)
        {
            Events.Enqueue((eventName, payloadJson));
            return Task.CompletedTask;
        }

        public Task SendGroupAsync(string groupName, string eventName, string payloadJson, CancellationToken cancellationToken = default)
        {
            Events.Enqueue((eventName, payloadJson));
            return Task.CompletedTask;
        }

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
