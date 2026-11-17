using System.Collections.Concurrent;
using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Proxy;
using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Real-browser rendering integration (§5.6): local headless browser against a
/// loopback test-page server. Graceful skip when no local browser exists.
/// Proves DOM/console/screenshot capture, infinite-scroll stabilization and
/// bounds, timeout/failure isolation, header split, stealth posture, proxy
/// routing capture, the WAF render-resolution hook, and artifact persistence
/// through the REAL CompressedContentStore.
/// </summary>
public sealed class RenderingPoolIntegrationTests
{
    private const string BasicPage = """
        <!doctype html><html><head><title>render-test</title></head>
        <body><div id="root">static</div>
        <script>
          document.getElementById('root').innerHTML = '<span id="injected">rendered-by-js</span>';
          console.warn('warn-message');
          console.error('error-message');
        </script></body></html>
        """;

    [Fact]
    public async Task Render_CapturesRenderedDom_Console_AndScreenshot()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return; // graceful skip: no local browser
        }

        await using var page = LoopbackPageServer.StartWithHtml(BasicPage);
        await using var pool = new HeadlessRenderingPool();

        var rendered = await pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice));

        Assert.Equal(page.BaseUrl, rendered.FinalUrl);
        Assert.Contains("rendered-by-js", rendered.Html);
        Assert.True(rendered.Screenshot.Length > 8);
        Assert.Equal(0x89, rendered.Screenshot.Span[0]); // PNG magic
        Assert.Equal((byte)'P', rendered.Screenshot.Span[1]);
        Assert.Contains(rendered.ConsoleMessages, m => m.Level == "warning" && m.Text.Contains("warn-message"));
        Assert.Contains(rendered.ConsoleMessages, m => m.Level == "error" && m.Text.Contains("error-message"));
        Assert.Null(rendered.PostRenderWafVerdict);
    }

    [Fact]
    public async Task Render_InfiniteScroll_ExpandsUntilContentStabilizes()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        const string scrollPage = """
            <!doctype html><html><body><div id="list"></div>
            <script>
              let count = 0;
              function append() {
                const list = document.getElementById('list');
                for (let i = 0; i < 5 && count < 30; i++, count++) {
                  const item = document.createElement('div');
                  item.className = 'item';
                  item.textContent = 'item-' + count;
                  item.style.height = '120px';
                  list.appendChild(item);
                }
              }
              append();
              append(); // initial content must exceed the viewport so scrolling is possible
              window.addEventListener('scroll', () => setTimeout(append, 50));
            </script></body></html>
            """;
        await using var page = LoopbackPageServer.StartWithHtml(scrollPage);
        await using var pool = new HeadlessRenderingPool(new RenderingPoolOptions
        {
            InfiniteScrollPauseMs = 150,
            InfiniteScrollMaxIterations = 40,
        });

        var rendered = await pool.RenderAsync(new RenderRequest(
            page.BaseUrl, BrowserGuard.DesktopDevice, InfiniteScroll: true));

        Assert.Contains("item-29", rendered.Html); // reached the stabilized end
    }

    [Fact]
    public async Task Render_InfiniteScroll_HonorsTheIterationBound()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        const string endlessPage = """
            <!doctype html><html><body><div id="list"></div>
            <script>
              let count = 0;
              window.addEventListener('scroll', () => {
                const list = document.getElementById('list');
                for (let i = 0; i < 5; i++, count++) {
                  const item = document.createElement('div');
                  item.textContent = 'endless-' + count;
                  item.style.height = '120px';
                  list.appendChild(item);
                }
              });
            </script></body></html>
            """;
        await using var page = LoopbackPageServer.StartWithHtml(endlessPage);
        await using var pool = new HeadlessRenderingPool(new RenderingPoolOptions
        {
            InfiniteScrollMaxIterations = 3,
            InfiniteScrollStableChecks = 99, // never stabilize; only the bound stops it
            InfiniteScrollPauseMs = 100,
        });

        var rendered = await pool.RenderAsync(new RenderRequest(
            page.BaseUrl, BrowserGuard.DesktopDevice, InfiniteScroll: true, TimeoutSeconds: 30));

        Assert.Contains("endless-", rendered.Html);
        Assert.DoesNotContain("endless-500", rendered.Html); // bounded, not endless
    }

    [Fact]
    public async Task Render_Timeout_IsIsolated_PoolSurvives()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        await using var stalling = LoopbackPageServer.Start(async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            context.Response.StatusCode = 200;
            context.Response.Close();
        });
        await using var healthy = LoopbackPageServer.StartWithHtml(BasicPage);
        await using var pool = new HeadlessRenderingPool();

        var failure = await Assert.ThrowsAsync<RenderFailureException>(() =>
            pool.RenderAsync(new RenderRequest(stalling.BaseUrl, BrowserGuard.DesktopDevice, TimeoutSeconds: 3)));
        Assert.Equal(RenderFailureKind.Timeout, failure.Kind);

        // Failure isolation: the same pool renders the next page successfully.
        var rendered = await pool.RenderAsync(new RenderRequest(healthy.BaseUrl, BrowserGuard.DesktopDevice));
        Assert.Contains("rendered-by-js", rendered.Html);
    }

    [Fact]
    public async Task Render_NavigationFailure_IsIsolated_PoolSurvives()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        var deadPort = LoopbackPageServer.GetFreeLoopbackPort();
        await using var healthy = LoopbackPageServer.StartWithHtml(BasicPage);
        await using var pool = new HeadlessRenderingPool();

        var failure = await Assert.ThrowsAsync<RenderFailureException>(() =>
            pool.RenderAsync(new RenderRequest($"http://127.0.0.1:{deadPort}/", BrowserGuard.DesktopDevice, TimeoutSeconds: 15)));
        Assert.Equal(RenderFailureKind.Navigation, failure.Kind);

        var rendered = await pool.RenderAsync(new RenderRequest(healthy.BaseUrl, BrowserGuard.DesktopDevice));
        Assert.Contains("rendered-by-js", rendered.Html);
    }

    [Fact]
    public async Task Render_HeaderSplit_SendsCredentialsOnlyToInScopeOrigins()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        await using var foreign = LoopbackPageServer.StartWithHtml("window.foreignLoaded = true;");
        var mainHtml = $"""
            <!doctype html><html><body>main
            <script src="{foreign.BaseUrl}resource.js"></script>
            </body></html>
            """;
        await using var main = LoopbackPageServer.StartWithHtml(mainHtml);

        var options = new RenderingPoolOptions
        {
            NonCredentialHeaders = new Dictionary<string, string> { ["X-Language-Pref"] = "en" },
            CredentialHeaders = new Dictionary<string, string> { ["X-Site-Credential"] = "secret-token" },
            InScopeOrigins = [new Uri(main.BaseUrl).GetLeftPart(UriPartial.Authority)],
        };
        await using var pool = new HeadlessRenderingPool(options);

        await pool.RenderAsync(new RenderRequest(main.BaseUrl, BrowserGuard.DesktopDevice));

        var mainRequest = Assert.Single(main.Requests, r => r.Url == main.BaseUrl);
        Assert.Equal("secret-token", mainRequest.Headers["X-Site-Credential"]);
        Assert.Equal("en", mainRequest.Headers["X-Language-Pref"]);

        var foreignRequest = Assert.Single(foreign.Requests);
        Assert.False(foreignRequest.Headers.ContainsKey("X-Site-Credential"));
        Assert.Equal("en", foreignRequest.Headers["X-Language-Pref"]);
    }

    [Fact]
    public async Task Render_Stealth_ReducesAutomationFingerprint()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        const string probePage = """
            <!doctype html><html><body><div id="probe"></div>
            <script>
              document.getElementById('probe').textContent = 'webdriver=' + String(navigator.webdriver);
            </script></body></html>
            """;
        await using var page = LoopbackPageServer.StartWithHtml(probePage);
        await using var pool = new HeadlessRenderingPool();

        var rendered = await pool.RenderAsync(new RenderRequest(
            page.BaseUrl, BrowserGuard.DesktopDevice, Stealth: true));

        Assert.Contains("webdriver=undefined", rendered.Html);
    }

    [Fact]
    public async Task Render_ProxyRouting_CapturesPageTraffic()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        await using var page = LoopbackPageServer.StartWithHtml(BasicPage);
        await using var proxy = new LoopbackInterceptingProxy();
        await using var pool = new HeadlessRenderingPool(
            new RenderingPoolOptions { RouteThroughProxy = true },
            proxy: proxy);

        var rendered = await pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice));
        Assert.Contains("rendered-by-js", rendered.Html);

        var captured = await pool.DrainNetworkCaptureAsync();
        Assert.Contains(captured, c => c.Url == page.BaseUrl && c.Method == "GET" && c.StatusCode == 200);
        // Boundary proof: the hardened browser emits no vendor background
        // traffic — every captured request targets the loopback crawl target.
        Assert.All(captured, c => Assert.Equal("127.0.0.1", new Uri(c.Url).Host));
    }

    [Fact]
    public async Task Render_WafHook_FlowsPostRenderVerdict()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        await using var page = LoopbackPageServer.StartWithHtml(BasicPage);
        var evaluator = new RecordingWafEvaluator();
        await using var pool = new HeadlessRenderingPool(wafEvaluator: evaluator);

        var rendered = await pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice));

        Assert.NotNull(rendered.PostRenderWafVerdict);
        Assert.True(rendered.PostRenderWafVerdict!.IsChallenge);
        Assert.Equal(WafProvider.Cloudflare, rendered.PostRenderWafVerdict.Provider);
        Assert.NotNull(evaluator.LastInput);
        Assert.Equal(page.BaseUrl, evaluator.LastInput!.Url);
        Assert.Equal(200, evaluator.LastInput.StatusCode);
        Assert.Contains("rendered-by-js", evaluator.LastInput.BodySnippet);
    }

    [Fact]
    public async Task Render_Artifacts_PersistThroughRealCompressedContentStore()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        await using var database = await RenderingTestDatabase.CreateAsync();
        await using var page = LoopbackPageServer.StartWithHtml(BasicPage);
        await using var pool = new HeadlessRenderingPool(contentStore: database.Store);

        var rendered = await pool.RenderAsync(
            new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice),
            new RenderArtifactContext(AuditId: 1, UrlId: 1));

        var artifacts = await database.Store.GetForUrlAsync(1, 1);
        Assert.Equal(2, artifacts.Count);
        Assert.Contains(artifacts, a => a.Kind == ContentArtifactKind.RenderedDom);
        var screenshotRow = Assert.Single(artifacts, a => a.Kind == ContentArtifactKind.Screenshot);

        await using var screenshotStream = await database.Store.OpenReadAsync(screenshotRow.StorageKey);
        Assert.NotNull(screenshotStream);
        using var buffer = new MemoryStream();
        await screenshotStream!.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);

        await using var domStream = await database.Store.OpenReadAsync(
            Assert.Single(artifacts, a => a.Kind == ContentArtifactKind.RenderedDom).StorageKey);
        using var domBuffer = new MemoryStream();
        await domStream!.CopyToAsync(domBuffer);
        Assert.Contains("rendered-by-js", Encoding.UTF8.GetString(domBuffer.ToArray()));
        Assert.True(rendered.Screenshot.Length > 0);
    }

    [Fact]
    public async Task Render_Concurrency_IsBounded()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        var concurrent = 0;
        var maxConcurrent = 0;
        await using var page = LoopbackPageServer.Start(async context =>
        {
            var now = Interlocked.Increment(ref concurrent);
            lock (typeof(RenderingPoolIntegrationTests))
            {
                maxConcurrent = Math.Max(maxConcurrent, now);
            }

            await Task.Delay(500);
            var bytes = Encoding.UTF8.GetBytes(BasicPage);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
            Interlocked.Decrement(ref concurrent);
        });

        await using var pool = new HeadlessRenderingPool(new RenderingPoolOptions { MaxConcurrency = 1 });
        var renders = Enumerable.Range(0, 3).Select(_ =>
            pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice))).ToArray();
        await Task.WhenAll(renders);

        Assert.Equal(1, maxConcurrent);
        Assert.All(renders, task => Assert.Contains("rendered-by-js", task.Result.Html));
    }

    private sealed class RecordingWafEvaluator : IWafChallengeEvaluator
    {
        public WafEvaluationInput? LastInput { get; private set; }

        public WafEvaluationResult Evaluate(WafEvaluationInput input)
        {
            LastInput = input;
            return new WafEvaluationResult(
                true,
                WafProvider.Cloudflare,
                WafChallengeClass.Interstitial,
                ["test-signature"],
                "{\"test\":true}");
        }
    }
}
