using CrawlForge.Audit.Accessibility;
using CrawlForge.Audit.Performance;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Proxy;
using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Audit.Tests.Accessibility;

/// <summary>
/// Real-browser integration (FS-CRAWL-001 §7.11, WP-03-06): lab vitals,
/// code coverage, and composition measured through the bounded probe against
/// a loopback page server, with STRICT loopback-only traffic assertions
/// (every proxied capture and every server request must be loopback, zero
/// exceptions). Graceful skip when no local browser resolves — runtime
/// download is never attempted. Also exercises accessibility over a real
/// rendered tree from the accepted rendering pool.
/// </summary>
public sealed class LabProbeIntegrationTests
{
    private const string VitalsPageHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Lab vitals fixture</title>
          <link rel="stylesheet" href="/site.css">
        </head>
        <body>
          <main>
            <h1>Lab fixture</h1>
            <div id="shifter" style="height:50px;background:#eeeeee">banner</div>
            <p id="large-text">Largest contentful paint candidate text block. Lorem ipsum dolor sit amet,
            consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.
            Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo
            consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat
            nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt
            mollit anim id est laborum. Sed ut perspiciatis unde omnis iste natus error sit voluptatem
            accusantium doloremque laudantium, totam rem aperiam, eaque ipsa quae ab illo inventore veritatis
            et quasi architecto beatae vitae dicta sunt explicabo.</p>
            <img src="/pixel.gif" alt="tiny pixel" width="1" height="1">
            <p class="used">styled text</p>
          </main>
          <script src="/app.js"></script>
          <script>
            // Blocking long task (~120ms) → longtask entry → TBT > 0.
            var start = performance.now();
            while (performance.now() - start < 120) { /* block */ }
            // Delayed layout shift → CLS > 0.
            setTimeout(function () {
              document.getElementById('shifter').style.height = '300px';
            }, 100);
          </script>
        </body>
        </html>
        """;

    private const string SiteCss = """
        .used { color: #111111; }
        .unused-rule-a { color: red; background: blue; padding: 10px; margin: 5px; border: 1px solid green; }
        .unused-rule-b { font-weight: bold; text-decoration: underline; letter-spacing: 2px; }
        .unused-rule-c { display: flex; justify-content: center; align-items: center; gap: 8px; }
        """;

    private const string AppJs = """
        function usedFunction() { return document.title.length; }
        function unusedFunctionOne() {
          var accumulator = 0;
          for (var i = 0; i < 100; i++) { accumulator += i * i; }
          return accumulator;
        }
        function unusedFunctionTwo() {
          var parts = [];
          for (var j = 0; j < 100; j++) { parts.push('segment-' + j); }
          return parts.join(',');
        }
        usedFunction();
        """;

    // Classic 1x1 transparent GIF (43 bytes).
    private const string PixelGifBase64 = "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    private static readonly DeviceProfile Desktop = new(
        "desktop",
        ViewportWidth: 1280,
        ViewportHeight: 800,
        UserAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        IsMobile: false);

    private static LoopbackPageServer StartVitalsServer() =>
        LoopbackPageServer.StartWithHtml(VitalsPageHtml, new Dictionary<string, (string, byte[])>
        {
            ["/site.css"] = ("text/css", System.Text.Encoding.UTF8.GetBytes(SiteCss)),
            ["/app.js"] = ("application/javascript", System.Text.Encoding.UTF8.GetBytes(AppJs)),
            ["/pixel.gif"] = ("image/gif", Convert.FromBase64String(PixelGifBase64)),
        });

    private static async Task AssertCaptureStrictlyLoopback(LoopbackInterceptingProxy proxy)
    {
        var capture = await proxy.DrainCapturedAsync();
        Assert.NotEmpty(capture);
        foreach (var request in capture)
        {
            var host = new Uri(request.Url).Host;
            Assert.True(
                host is "127.0.0.1" or "localhost" or "::1",
                $"Non-loopback traffic observed through the intercepting proxy: {request.Url}");
        }
    }

    [Fact]
    public async Task LabProbe_MeasuresVitalsCoverageAndComposition_LoopbackOnly()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return; // Graceful skip: no local browser; runtime download prohibited.
        }

        await using var server = StartVitalsServer();
        await using var proxy = new LoopbackInterceptingProxy();
        var proxyEndpoint = await proxy.StartAsync(new ProxyOptions(null));

        await using var labProbe = new LabRenderingProbe(new LabProbeOptions
        {
            MaxConcurrency = 1,
            Proxy = proxyEndpoint,
            SettleDelayMs = 800,
        });

        var sample = await labProbe.CollectAsync(new LabVitalsRequest(server.BaseUrl, Desktop, 60));

        // Lab vitals measured on the fixture.
        Assert.NotNull(sample.LcpMilliseconds);
        Assert.True(sample.LcpMilliseconds > 0, "LCP should be measured for the large text block.");
        Assert.NotNull(sample.Cls);
        Assert.True(sample.Cls > 0, "The delayed height change should produce a layout shift.");
        Assert.NotNull(sample.TbtMilliseconds);
        Assert.True(sample.TbtMilliseconds > 0, "The blocking script should produce total blocking time.");
        Assert.NotNull(sample.InpProxyMilliseconds);
        Assert.True(sample.InpProxyMilliseconds >= 0);
        Assert.NotNull(sample.FcpMilliseconds);
        Assert.True(sample.FcpMilliseconds > 0);

        // Code coverage: unused CSS rules and unused JS functions detected.
        Assert.NotNull(sample.Coverage);
        Assert.True(sample.Coverage!.JsTotalBytes > 0);
        Assert.True(sample.Coverage.CssTotalBytes > 0);
        Assert.True(sample.Coverage.JsUsedBytes < sample.Coverage.JsTotalBytes, "Unused JS functions expected.");
        Assert.True(sample.Coverage.CssUsedBytes < sample.Coverage.CssTotalBytes, "Unused CSS rules expected.");
        Assert.Contains(sample.Coverage.Entries, entry => entry.Url.EndsWith("/app.js", StringComparison.Ordinal));
        Assert.Contains(sample.Coverage.Entries, entry => entry.Url.EndsWith("/site.css", StringComparison.Ordinal));

        // Composition: nodes, image count and transferred image bytes.
        Assert.NotNull(sample.Composition);
        Assert.True(sample.Composition!.DomNodes > 10);
        Assert.True(sample.Composition.ImageCount >= 1);
        Assert.True(sample.Composition.ImageBytes > 0, "The served GIF should report transferred bytes.");
        Assert.NotNull(sample.Composition.SmallestFontSizePx);

        // STRICT egress guard: every captured request and every server
        // request must be loopback — zero exceptions.
        await AssertCaptureStrictlyLoopback(proxy);
        server.AssertAllRequestsLoopback();
    }

    [Fact]
    public async Task LabProbe_ThroughAnalyzers_ProducesBandedFindingsAndMetrics_LoopbackOnly()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return; // Graceful skip.
        }

        await using var server = StartVitalsServer();
        await using var proxy = new LoopbackInterceptingProxy();
        var proxyEndpoint = await proxy.StartAsync(new ProxyOptions(null));

        await using var labProbe = new LabRenderingProbe(new LabProbeOptions
        {
            MaxConcurrency = 1,
            Proxy = proxyEndpoint,
            SettleDelayMs = 800,
        });
        var cache = new LabVitalsSampleCache();
        var vitalsAnalyzer = new CoreWebVitalsAnalyzer(probe: labProbe, cache: cache);
        var coverageAnalyzer = new CodeCoverageAnalyzer(probe: labProbe, cache: cache);
        var budgetEvaluator = new PerformanceBudgetEvaluator(probe: labProbe, cache: cache);

        var context = AuditContexts.Create(
            html: VitalsPageHtml,
            renderingMode: RenderMode.Always,
            fetch: AuditContexts.Fetch(ttfbMs: 5));
        // Point the fixture URL at the loopback server.
        context = context with { Url = AuditContexts.Url(url: server.BaseUrl) };

        var vitals = await vitalsAnalyzer.AnalyzeAsync(context);
        Assert.NotNull(vitals.LcpMilliseconds);
        Assert.NotEmpty(vitals.Findings);
        Assert.All(vitals.Findings, finding => Assert.Equal(AuditCategory.Performance, finding.Category));

        var coverage = await coverageAnalyzer.AnalyzeDetailedAsync(context);
        Assert.True(coverage.Result.TotalBytes > 0);
        Assert.True(coverage.Result.UnusedPercent > 0);

        var budgetFindings = await budgetEvaluator.EvaluateAsync(
            context, new PerformanceBudget(MaxLcpMilliseconds: 1));
        Assert.Contains(budgetFindings, finding => finding.RuleKey == PerformanceRuleKeys.BudgetExceeded);

        // One shared measurement across all three analyzers.
        var metrics = PerformanceMetricContributions.FromVitals(vitals, context.Url.Id);
        Assert.Contains(metrics, metric => metric.MetricKey == PerformanceMetricKeys.Lcp);

        await AssertCaptureStrictlyLoopback(proxy);
        server.AssertAllRequestsLoopback();
    }

    [Fact]
    public async Task RenderingPool_AccessibilityOverRealRenderedTree_LoopbackOnly()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return; // Graceful skip.
        }

        const string html = """
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>Rendered a11y</title></head>
            <body>
              <main>
                <h1>Rendered fixture</h1>
                <img id="js-image" src="/pixel.gif">
                <p style="color:#000000;background-color:#ffffff">readable text</p>
              </main>
              <script>
                // The raw HTML lacks alt; the rendered DOM gains it.
                document.getElementById('js-image').setAttribute('alt', 'Injected alternative');
              </script>
            </body></html>
            """;

        await using var server = LoopbackPageServer.StartWithHtml(html, new Dictionary<string, (string, byte[])>
        {
            ["/pixel.gif"] = ("image/gif", Convert.FromBase64String(PixelGifBase64)),
        });
        await using var proxy = new LoopbackInterceptingProxy();

        await using var pool = new HeadlessRenderingPool(
            new RenderingPoolOptions { MaxConcurrency = 1, RouteThroughProxy = true },
            proxy: proxy);
        var rendered = await pool.RenderAsync(new RenderRequest(
            server.BaseUrl,
            Desktop,
            TimeoutSeconds: 45));

        Assert.Contains("Injected alternative", rendered.Html);

        var analyzer = new AccessibilityAnalyzer();
        var context = AuditContexts.Create(
            html: html,
            renderedHtml: rendered.Html,
            renderingMode: RenderMode.Always);
        var findings = await analyzer.AnalyzeAsync(context);

        // The JS-injected alt satisfies the image-alt rule on the rendered tree.
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.ImageAlt);
        // Contrast is evaluated (no requires-rendering skip) on the rendered tree.
        Assert.DoesNotContain(findings, finding => finding.RuleKey == AccessibilityRuleKeys.ColorContrast
            && finding.EvidenceJson.Contains("requiresRendering"));

        await AssertCaptureStrictlyLoopback(proxy);
        server.AssertAllRequestsLoopback();
    }

    [Fact]
    public async Task LabProbe_ConfiguredBrowserMissing_FailsLocal_WithoutDownload()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "crawlforge-no-such-browser-" + Guid.NewGuid().ToString("N") + ".exe");
        await using var probe = new LabRenderingProbe(new LabProbeOptions
        {
            BrowserResolution = new LabBrowserResolutionOptions { UserConfiguredExecutablePath = missingPath },
        });

        var exception = await Assert.ThrowsAsync<LabProbeException>(() =>
            probe.CollectAsync(new LabVitalsRequest("http://127.0.0.1:1/", Desktop, 10)));
        Assert.Equal(LabProbeFailureKind.BrowserUnavailable, exception.Kind);
        Assert.Contains("download is prohibited", exception.Message);

        // The analyzer degrades to a notice, never a crash.
        var analyzer = new CoreWebVitalsAnalyzer(probe: probe);
        var context = AuditContexts.Create(html: "<html><body>x</body></html>", renderingMode: RenderMode.Always);
        var result = await analyzer.AnalyzeAsync(context);
        Assert.Null(result.LcpMilliseconds);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(PerformanceRuleKeys.ProbeUnavailable, finding.RuleKey);
    }

    [Fact]
    public async Task LabProbe_InvalidTarget_FailsIsolated()
    {
        await using var probe = new LabRenderingProbe();
        var exception = await Assert.ThrowsAsync<LabProbeException>(() =>
            probe.CollectAsync(new LabVitalsRequest("not-a-url", Desktop, 10)));
        Assert.Equal(LabProbeFailureKind.Navigation, exception.Kind);
    }
}
