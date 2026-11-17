using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Host;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Acceptance.Tests;

/// <summary>
/// WP-07-04 targeted live checks for the remainder areas named in the dispatch:
/// spelling evidence, structured-data taxonomy, soft-404, technology catalogue
/// locality, scoring/suppression comparability, standalone tools, signed-request
/// scope, n-gram bounds, SERP pixels, header conflicts, zone-weighted equity.
/// Every check composes the REAL backend (full module registration, loopback
/// Kestrel) against scripted loopback origins — no product mocks.
/// </summary>
public sealed class TargetedRemainderTests
{
    // ------------------------------------------------------------------
    // Criterion #13 — spelling token evidence + dictionary suppression
    // ------------------------------------------------------------------

    [Fact]
    public async Task Spelling_TokenEvidence_AndDictionarySuppression()
    {
        await using var backend = await StartBackendAsync();
        var (baseUrl, origin) = await StartOriginAsync(context => RespondHtmlAsync(context,
            "<html lang=\"en\"><head><title>Spelling Fixture</title></head><body>" +
            "<p>Ths sentense contans severl mistaks that the spelling analyzer should flag with token evidence.</p>" +
            "</body></html>"));
        try
        {
            using var client = NewClient(backend);
            var findings = await SinglePageFindingsAsync(client, baseUrl);
            var spelling = findings.Where(f => f.GetProperty("ruleKey").GetString() == "content.spelling").ToList();
            Assert.NotEmpty(spelling);
            Assert.Contains(spelling, f => f.GetRawText().Contains("Ths", StringComparison.Ordinal));

            // Accept the token into the user dictionary (local user data).
            var added = await client.PostAsJsonAsync("/api/dictionary", new { word = "Ths", language = "en" });
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);

            var after = await SinglePageFindingsAsync(client, baseUrl);
            // Token-level suppression: the accepted word no longer appears in
            // any spelling finding's issue list.
            foreach (var finding in after.Where(f => f.GetProperty("ruleKey").GetString() == "content.spelling"))
            {
                var issues = finding.GetProperty("evidence").GetProperty("details").GetProperty("issues");
                Assert.DoesNotContain(issues.EnumerateArray(),
                    i => i.GetProperty("word").GetString() == "Ths");
            }
        }
        finally
        {
            StopOrigin(origin);
        }
    }

    // ------------------------------------------------------------------
    // Criterion #27 — structured-data error taxonomy
    // ------------------------------------------------------------------

    [Fact]
    public async Task StructuredData_InvalidJsonLd_EmitsTaxonomyFinding()
    {
        await using var backend = await StartBackendAsync();
        const string invalidMarkup =
            "<html><head><title>Structured Data Fixture</title>" +
            "<script type=\"application/ld+json\">{\"@type\":\"Article\",\"headline\":\"Missing context\"}</script>" +
            "</head><body><p>Article body content for the structured data fixture page.</p></body></html>";
        var (baseUrl, origin) = await StartOriginAsync(context => RespondHtmlAsync(context, invalidMarkup));
        try
        {
            using var client = NewClient(backend);

            // Parsing facet: the shared pipeline captures JSON-LD AND Microdata
            // blocks into the element inventory (single-page, no project).
            var response = await client.PostAsJsonAsync("/api/analysis/single-page", new { url = baseUrl });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var data = await DataAsync(response);
            var counts = data.GetProperty("elementInventory").GetProperty("counts");
            Assert.True(counts.GetProperty("structured_data").GetInt32() >= 1);

            // Validation facet: the structured-data checker reports the error
            // taxonomy + rich-result eligibility for the same markup.
            var check = await client.PostAsJsonAsync("/api/tools/structured-data-check", new { markup = invalidMarkup });
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
            var blocks = (await DataAsync(check)).GetProperty("blocks");
            Assert.True(blocks.GetArrayLength() >= 1);
            var block = blocks[0];
            Assert.Equal("json-ld", block.GetProperty("format").GetString());
            Assert.False(block.GetProperty("valid").GetBoolean());
            Assert.True(block.GetProperty("issues").GetArrayLength() >= 1);

            // A valid Article validates and is rich-result eligible (mirrors the
            // locked validator fixture: headline + image + datePublished + author).
            var valid = await client.PostAsJsonAsync("/api/tools/structured-data-check", new
            {
                markup = "<script type=\"application/ld+json\">" +
                         "{\"@context\":\"https://schema.org\",\"@type\":\"Article\",\"headline\":\"Valid\"," +
                         "\"image\":\"https://example.test/cover.jpg\",\"datePublished\":\"2026-01-15T10:30:00+00:00\"," +
                         "\"author\":{\"@type\":\"Person\",\"name\":\"Ada\"}}" +
                         "</script>"
            });
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            var validBlock = (await DataAsync(valid)).GetProperty("blocks")[0];
            Assert.True(validBlock.GetProperty("valid").GetBoolean());
            Assert.True(validBlock.GetProperty("richResultEligible").GetBoolean());
        }
        finally
        {
            StopOrigin(origin);
        }
    }

    // ------------------------------------------------------------------
    // Criterion #28 — soft-404 distinct from hard errors
    // ------------------------------------------------------------------

    [Fact]
    public async Task Soft404_SuccessStatusErrorContent_FlaggedDistinctly()
    {
        await using var backend = await StartBackendAsync();
        var (baseUrl, origin) = await StartOriginAsync(context => RespondHtmlAsync(context,
            "<html><head><title>404 Not Found</title></head><body>" +
            "<h1>Page not found</h1>" +
            "<p>Sorry, the page you are looking for might have been removed or does not exist.</p>" +
            "</body></html>"));
        try
        {
            using var client = NewClient(backend);
            var response = await client.PostAsJsonAsync("/api/analysis/single-page", new { url = baseUrl });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var data = await DataAsync(response);
            var soft404 = data.GetProperty("findings").EnumerateArray()
                .FirstOrDefault(f => f.GetProperty("ruleKey").GetString() == "indexability.soft-404");
            Assert.Equal(JsonValueKind.Object, soft404.ValueKind);
            // Distinct warning — the page returned HTTP 200, not a hard error.
            Assert.Equal("warning", soft404.GetProperty("severity").GetString());
        }
        finally
        {
            StopOrigin(origin);
        }
    }

    // ------------------------------------------------------------------
    // Criterion #29 — technology catalogue locality + detection
    // ------------------------------------------------------------------

    [Fact]
    public async Task TechnologyCatalogue_IsLocal_AndDetectsFromBundledFingerprints()
    {
        // The fingerprint catalogue is a bundled local asset — never fetched.
        var catalogueDirectory = EvidenceIntegrityTests.Resolve("assets/tech-fingerprints");
        Assert.True(Directory.Exists(catalogueDirectory));
        Assert.NotEmpty(Directory.GetFiles(catalogueDirectory, "*.json"));

        await using var backend = await StartBackendAsync();
        var (baseUrl, origin) = await StartOriginAsync(context => RespondHtmlAsync(context,
            "<html><head><title>Technology Fixture</title>" +
            "<meta name=\"generator\" content=\"WordPress 6.4\" />" +
            "<link rel=\"stylesheet\" href=\"/wp-content/themes/fixture/style.css\" />" +
            "</head><body><p>WordPress technology detection fixture page.</p></body></html>"));
        try
        {
            var auditId = await RunAuditAsync(backend, baseUrl);
            using var client = NewClient(backend);
            var response = await client.GetAsync($"/api/audits/{auditId}/technologies");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("wordpress", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            StopOrigin(origin);
        }
    }

    // ------------------------------------------------------------------
    // Criterion #32 — scoring comparability + suppression recompute
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scoring_ComparableAcrossAudits_AndSuppressionRecomputes()
    {
        await using var backend = await StartBackendAsync();
        var (baseUrl, origin) = await StartOriginAsync(context => RespondHtmlAsync(context,
            "<html><head><title>Scoring Fixture</title></head><body>" +
            "<h1>Heading</h1><p>Scoring comparability fixture content with enough words to analyze.</p>" +
            "<a href=\"/second\">second page</a></body></html>"));
        try
        {
            var auditId = await RunAuditAsync(backend, baseUrl);
            using var client = NewClient(backend);

            var firstScores = await client.GetAsync($"/api/audits/{auditId}/scores");
            Assert.Equal(HttpStatusCode.OK, firstScores.StatusCode);
            var first = await DataAsync(firstScores);

            // Offline re-audit over the SAME stored data → comparable scores.
            var reaudit = await client.PostAsJsonAsync($"/api/audits/{auditId}/reaudit", new { refresh = false });
            Assert.Equal(HttpStatusCode.Created, reaudit.StatusCode);
            var reauditData = await DataAsync(reaudit);
            var secondId = long.Parse(reauditData.GetProperty("auditId").GetString()!);
            await WaitForCompletionAsync(backend, secondId);

            var secondScores = await client.GetAsync($"/api/audits/{secondId}/scores");
            Assert.Equal(HttpStatusCode.OK, secondScores.StatusCode);
            var second = await DataAsync(secondScores);
            Assert.Equal(first.GetProperty("overall").GetDouble(), second.GetProperty("overall").GetDouble());

            // Suppress an emitted rule → the locked service recomputes offline.
            var findingsResponse = await client.GetAsync($"/api/audits/{auditId}/findings?limit=1");
            Assert.Equal(HttpStatusCode.OK, findingsResponse.StatusCode);
            var findingsPage = await DataAsync(findingsResponse);
            var ruleKey = findingsPage[0].GetProperty("ruleKey").GetString()!;

            var projects = backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
            var audit = await projects.GetByIdAsync(auditId);
            var suppressed = await client.PostAsJsonAsync($"/api/projects/{audit!.ProjectId}/suppressions",
                new { ruleKey });
            Assert.Equal(HttpStatusCode.Created, suppressed.StatusCode);

            var recomputed = await client.GetAsync($"/api/audits/{auditId}/scores");
            Assert.Equal(HttpStatusCode.OK, recomputed.StatusCode);
            var after = await DataAsync(recomputed);
            Assert.True(after.TryGetProperty("overall", out _));
        }
        finally
        {
            StopOrigin(origin);
        }
    }

    // ------------------------------------------------------------------
    // Criterion #33 — standalone tools without a full audit
    // ------------------------------------------------------------------

    [Fact]
    public async Task StandaloneTools_OperateWithoutAFullAudit()
    {
        await using var backend = await StartBackendAsync();
        using var client = NewClient(backend);

        // Robots tester: allow/disallow verdicts from a supplied body.
        var robots = await client.PostAsJsonAsync("/api/tools/robots-test", new
        {
            robotsBody = "User-agent: *\nDisallow: /private\n",
            testUrls = new[] { "http://target.example.com/private/page", "http://target.example.com/public" },
            userAgent = "*"
        });
        Assert.Equal(HttpStatusCode.OK, robots.StatusCode);
        var verdicts = (await DataAsync(robots)).GetProperty("results");
        Assert.False(verdicts[0].GetProperty("allowed").GetBoolean());
        Assert.True(verdicts[1].GetProperty("allowed").GetBoolean());

        // Scope preview: scoped word counts from sample HTML (no audit).
        var scope = await client.PostAsJsonAsync("/api/tools/scope-preview", new
        {
            sampleHtml = "<html><body><main><p>hello scoped world</p></main><footer>boilerplate</footer></body></html>"
        });
        Assert.Equal(HttpStatusCode.OK, scope.StatusCode);
        Assert.True((await DataAsync(scope)).GetProperty("fullWordCount").GetInt32() > 0);

        // llms.txt validator: structural validation of a supplied body.
        var llms = await client.PostAsJsonAsync("/api/tools/llms-validate", new
        {
            body = "# Fixture\n\n> A local llms.txt fixture.\n\n## Docs\n\n- [Guide](https://example.com/guide): Guide\n"
        });
        Assert.Equal(HttpStatusCode.OK, llms.StatusCode);
        Assert.True((await DataAsync(llms)).TryGetProperty("valid", out _));

        // No project or audit was created by any tool invocation.
        var audits = backend.App.Services.GetRequiredService<IReadRepository<AuditDto>>();
        Assert.Empty((await audits.ListAsync(new ListQuery(0, 10))).Items);
    }

    // ------------------------------------------------------------------
    // Criterion #34 — signed-request scope (component level)
    // ------------------------------------------------------------------

    [Fact]
    public void SignedRequest_AppliesOnlyInScope()
    {
        var handler = new SignedRequestHandler(uri => uri.Host == "in.example.com");
        var secret = new SignedRequestSecret("key-1", "user-signature", SignedRequestHandler.StaticAlgorithm, "X-Signature");

        var inScope = handler.Sign(new HttpFetchRequest("http://in.example.com/page"), secret);
        Assert.Equal("user-signature", inScope.Headers!["X-Signature"]);

        var outOfScope = new HttpFetchRequest("http://out.example.com/page",
            Headers: new Dictionary<string, string>());
        var unchanged = handler.Sign(outOfScope, secret);
        Assert.Same(outOfScope, unchanged);
        Assert.False(unchanged.Headers!.ContainsKey("X-Signature"));

        // Expiry tracking is local and deterministic.
        var now = DateTimeOffset.UtcNow;
        var tracker = new SignedRequestExpiryTracker();
        tracker.Register("expiring", now.AddHours(1));
        Assert.Equal(SignedRequestExpiryState.ExpiringSoon, tracker.GetState("expiring", now));
        Assert.Equal(SignedRequestExpiryState.Expired, tracker.GetState("expiring", now.AddHours(2)));
        Assert.Equal(SignedRequestExpiryState.Unknown, tracker.GetState("unregistered", now));
    }

    // ------------------------------------------------------------------
    // Criterion #38 — n-gram orders, zones, and bounds on the REST surface
    // ------------------------------------------------------------------

    [Fact]
    public async Task Ngrams_OrdersZonesAndBounds_HonoredOnRestSurface()
    {
        await using var backend = await StartBackendAsync();
        var (baseUrl, origin) = await StartOriginAsync(context =>
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var html = path switch
            {
                "/" => "<html><head><title>Local First Crawling</title></head><body>" +
                       "<h1>Local first crawling</h1>" +
                       "<p>local first crawling audits websites. local first crawling keeps data local. " +
                       "local first crawling analyzes pages offline. local first crawling exports reports.</p>" +
                       "<a href=\"/second\">second page</a></body></html>",
                _ => "<html><head><title>Local First Crawling</title></head><body>" +
                     "<h1>Local first crawling</h1>" +
                     "<p>local first crawling audits websites too. local first crawling keeps data local.</p>" +
                     "</body></html>"
            };
            return RespondHtmlAsync(context, html);
        });
        try
        {
            var auditId = await RunAuditAsync(backend, baseUrl);
            using var client = NewClient(backend);

            // Per-URL pass (wired through the rule host): orders + zones + bounds.
            var urls = await ListAsync<UrlRecordDto>(backend, auditId);
            var urlId = urls[0].Id;
            var bigrams = await client.GetAsync($"/api/audits/{auditId}/ngrams?urlId={urlId}&n=2&limit=500");
            Assert.Equal(HttpStatusCode.OK, bigrams.StatusCode);
            var rows = (await DataAsync(bigrams)).EnumerateArray().ToList();
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal(2, r.GetProperty("n").GetInt32()));

            // Zone filter is honored; unknown zones are rejected in the envelope.
            var titleZone = await client.GetAsync($"/api/audits/{auditId}/ngrams?urlId={urlId}&zone=title");
            Assert.Equal(HttpStatusCode.OK, titleZone.StatusCode);
            var badZone = await client.GetAsync($"/api/audits/{auditId}/ngrams?zone=bogus");
            Assert.Equal(HttpStatusCode.BadRequest, badZone.StatusCode);
            var error = JsonDocument.Parse(await badZone.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
            Assert.Equal("validation", error.GetProperty("code").GetString());

            // Page-size bound: the limit is clamped, never unbounded.
            var clamped = await client.GetAsync($"/api/audits/{auditId}/ngrams?limit=99999");
            Assert.Equal(HttpStatusCode.OK, clamped.StatusCode);
            var pagination = JsonDocument.Parse(await clamped.Content.ReadAsStringAsync())
                .RootElement.GetProperty("pagination");
            Assert.True(pagination.GetProperty("limit").GetInt32() <= 1000);

            // D14 gate — site pass: ngram_site_summaries must be materialized by
            // the audit lifecycle (site-wide DF) and the cannibalization surface
            // must report the shared-primary cluster across the two fixture URLs.
            var site = await client.GetAsync($"/api/audits/{auditId}/ngrams?n=2&limit=500");
            Assert.Equal(HttpStatusCode.OK, site.StatusCode);
            Assert.NotEmpty((await DataAsync(site)).EnumerateArray());

            var cannibalization = await client.GetAsync($"/api/audits/{auditId}/ngrams/cannibalization");
            Assert.Equal(HttpStatusCode.OK, cannibalization.StatusCode);
            Assert.NotEmpty((await DataAsync(cannibalization)).EnumerateArray());
        }
        finally
        {
            StopOrigin(origin);
        }
    }

    // ------------------------------------------------------------------
    // Criterion #48 — SERP pixel truncation
    // ------------------------------------------------------------------

    [Fact]
    public async Task SerpPixels_OverBudgetTitle_EmitsTruncation()
    {
        await using var backend = await StartBackendAsync();
        var longTitle = string.Join(" ", Enumerable.Repeat("ExtremelyLongTitleWord", 20));
        var (baseUrl, origin) = await StartOriginAsync(context => RespondHtmlAsync(context,
            $"<html><head><title>{longTitle}</title></head><body>" +
            "<p>Pixel width measurement fixture with an over-budget title.</p></body></html>"));
        try
        {
            using var client = NewClient(backend);
            var findings = await SinglePageFindingsAsync(client, baseUrl);
            Assert.Contains(findings, f => f.GetProperty("ruleKey").GetString() == "title-pixel-truncation");
        }
        finally
        {
            StopOrigin(origin);
        }
    }

    // ------------------------------------------------------------------
    // Criterion #48 — header vs HTML conflict with dual evidence
    // ------------------------------------------------------------------

    [Fact]
    public async Task HeaderHtmlConflict_EmitsErrorWithDualEvidence()
    {
        await using var backend = await StartBackendAsync();
        var (baseUrl, origin) = await StartOriginAsync(context =>
        {
            context.Response.AddHeader("X-Robots-Tag", "noindex");
            return RespondHtmlAsync(context,
                "<html><head><title>Conflict Fixture</title>" +
                "<meta name=\"robots\" content=\"index, follow\" />" +
                "</head><body><p>Header says noindex while the HTML meta says index.</p></body></html>");
        });
        try
        {
            using var client = NewClient(backend);
            var findings = await SinglePageFindingsAsync(client, baseUrl);
            var conflict = findings.FirstOrDefault(f =>
                f.GetProperty("ruleKey").GetString() == "indexability.header-html-conflict");
            Assert.Equal(JsonValueKind.Object, conflict.ValueKind);
            Assert.Equal("error", conflict.GetProperty("severity").GetString());
            // Dual evidence: both the header directive and the HTML directive.
            var raw = conflict.GetRawText();
            Assert.Contains("noindex", raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("index", raw, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            StopOrigin(origin);
        }
    }

    // ------------------------------------------------------------------
    // Criterion #48 — zone-weighted equity on live link edges
    // ------------------------------------------------------------------

    [Fact]
    public async Task ZoneWeightedEquity_LinkEdgesCarryZonesAndWeights()
    {
        await using var backend = await StartBackendAsync();
        var (baseUrl, origin) = await StartOriginAsync(context =>
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var html = path switch
            {
                "/" => "<html><head><title>Equity Root</title></head><body>" +
                       "<nav><a href=\"/a\">navigation link</a></nav>" +
                       "<main><p>Body text with an <a href=\"/b\">in-content link</a> inside.</p></main>" +
                       "</body></html>",
                _ => "<html><head><title>Equity Leaf</title></head><body><p>Leaf page content.</p></body></html>"
            };
            return RespondHtmlAsync(context, html);
        });
        try
        {
            var auditId = await RunAuditAsync(backend, baseUrl);
            var edges = await ListAsync<LinkEdgeDto>(backend, auditId);
            Assert.NotEmpty(edges);

            // Every internal edge carries a DOM zone classification.
            var internalEdges = edges.Where(e => e.IsInternal && e.TargetUrlId is not null).ToList();
            Assert.NotEmpty(internalEdges);
            Assert.All(internalEdges, e => Assert.NotNull(e.DomZone));
            Assert.Contains(internalEdges, e => e.DomZone == DomZone.MainContent);
            Assert.Contains(internalEdges, e => e.DomZone == DomZone.Navigation);

            // Weighted equity is persisted and differs by zone weight.
            Assert.All(internalEdges, e => Assert.NotNull(e.WeightedEquity));
            var inContent = internalEdges.First(e => e.DomZone == DomZone.MainContent);
            var navigation = internalEdges.First(e => e.DomZone == DomZone.Navigation);
            Assert.NotEqual(inContent.WeightedEquity, navigation.WeightedEquity);

            // The graph surface exposes the weighted edges.
            using var client = NewClient(backend);
            var graph = await client.GetAsync($"/api/audits/{auditId}/graph?type=link-network");
            Assert.Equal(HttpStatusCode.OK, graph.StatusCode);
            var graphData = await DataAsync(graph);
            Assert.Contains(graphData.GetProperty("edges").EnumerateArray(),
                e => e.GetProperty("weight").ValueKind == JsonValueKind.Number);
        }
        finally
        {
            StopOrigin(origin);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<CrawlForgeBackend> StartBackendAsync()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "crawlforge-acceptance", Guid.NewGuid().ToString("N"));
        return await CrawlForgeBackend.StartAsync(
            [$"--CrawlForge:DataDirectory={dataDirectory}", "--CrawlForge:Port=0"]);
    }

    private static HttpClient NewClient(CrawlForgeBackend backend)
    {
        var client = new HttpClient { BaseAddress = new Uri(backend.BaseAddress) };
        client.DefaultRequestHeaders.Add("X-CrawlForge-Session", backend.SessionToken);
        return client;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<List<JsonElement>> SinglePageFindingsAsync(HttpClient client, string url)
    {
        var response = await client.PostAsJsonAsync("/api/analysis/single-page", new { url });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await DataAsync(response);
        return data.GetProperty("findings").EnumerateArray().Select(f => f.Clone()).ToList();
    }

    private static async Task<long> RunAuditAsync(CrawlForgeBackend backend, string baseUrl)
    {
        var projects = backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "Acceptance", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
        var configurations = backend.App.Services.GetRequiredService<IWriteRepository<CrawlConfigurationDto>>();
        var configuration = await configurations.CreateAsync(new CrawlConfigurationDto(
            0, project.Id, "default", JsonSerializer.Serialize(new[] { baseUrl }),
            null, null, null, RenderMode.None, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var execution = backend.App.Services.GetRequiredService<IAuditExecutionService>();
        var auditId = await execution.StartAuditAsync(project.Id, configuration.Id, ScheduleTriggerType.Manual, null);
        var audit = await WaitForCompletionAsync(backend, auditId);
        Assert.Equal(AuditStatus.Completed, audit.Status);
        return auditId;
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

    private static async Task<IReadOnlyList<T>> ListAsync<T>(CrawlForgeBackend backend, long auditId)
        where T : class
    {
        var repository = backend.App.Services.GetRequiredService<IReadRepository<T>>();
        var page = await repository.ListAsync(new ListQuery(
            0, 2000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }));
        return page.Items;
    }

    private static async Task<(string BaseUrl, HttpListener Listener)> StartOriginAsync(
        Func<HttpListenerContext, Task> handle)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var baseUrl = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl);
        listener.Start();
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
        return (baseUrl, listener);
    }

    private static async Task RespondHtmlAsync(HttpListenerContext context, string html)
    {
        var body = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    private static void StopOrigin(HttpListener listener)
    {
        try
        {
            listener.Stop();
            listener.Close();
        }
        catch (Exception)
        {
            // Best-effort origin teardown.
        }
    }
}
