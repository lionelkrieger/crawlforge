using System.Diagnostics;
using System.Net;
using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Proxy;
using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Real-browser rendering integration (§5.6/§5.6.2) through DIRECT CDP
/// (D-002): local headless browser against loopback test-page servers.
/// Graceful skip when no local browser exists. Proves DOM/console/screenshot
/// capture, the CDP-direct waterfall with transferred bytes (CR-006/CR-031),
/// the wait rule (quiet + hard cap + streaming exclusion), infinite-scroll
/// stabilization and bounds, timeout/failure isolation, header split, stealth
/// posture, the service-worker bypass + registration fact (§7.19), explicit
/// proxy routing opt-in, the WAF render-resolution hook, and artifact
/// persistence through the REAL CompressedContentStore.
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

    private static readonly byte[] PixelGif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    [Fact]
    public async Task Render_CapturesRenderedDom_Console_Screenshot_AndWaterfall()
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

        // CR-006: the waterfall is captured directly by the CDP control layer.
        Assert.NotNull(rendered.NetworkWaterfall);
        var document = Assert.Single(rendered.NetworkWaterfall!, e => e.ResourceType == "document");
        Assert.Equal(200, document.StatusCode);
        Assert.True(document.TransferredBytes > 0);
    }

    [Fact]
    public async Task Render_Waterfall_MeasuresSubresourceTransferredBytes()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        await using var page = LoopbackPageServer.Start(async context =>
        {
            if (context.Request.Url?.AbsolutePath == "/pixel.gif")
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "image/gif";
                context.Response.ContentLength64 = PixelGif.Length;
                await context.Response.OutputStream.WriteAsync(PixelGif);
                context.Response.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes("""
                <!doctype html><html><body><img src="/pixel.gif" alt="pixel"></body></html>
                """);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });
        await using var pool = new HeadlessRenderingPool();

        var rendered = await pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice));

        var image = Assert.Single(rendered.NetworkWaterfall!, e => e.ResourceType == "image");
        Assert.EndsWith("/pixel.gif", image.Url);
        Assert.Equal(200, image.StatusCode);
        Assert.Equal("image/gif", image.MimeType);
        // CR-031: rendered image payloads are measurable (43-byte GIF).
        Assert.True(image.TransferredBytes >= PixelGif.Length);
        Assert.False(image.FromCache); // fresh context per page
        Assert.False(image.FromServiceWorker);
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
            InfiniteScrollMaxIterations = 40,
        });

        var rendered = await pool.RenderAsync(new RenderRequest(
            page.BaseUrl, BrowserGuard.DesktopDevice, InfiniteScroll: true));

        Assert.Contains("item-29", rendered.Html);
        Assert.DoesNotContain("item-30", rendered.Html);
    }

    [Fact]
    public async Task Render_InfiniteScroll_EndlessContent_StopsAtBound()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        const string endlessPage = """
            <!doctype html><html><body><div id="list"></div>
            <script>
              let count = 0;
              function append() {
                const list = document.getElementById('list');
                for (let i = 0; i < 5; i++, count++) {
                  const item = document.createElement('div');
                  item.className = 'item';
                  item.textContent = 'item-' + count;
                  item.style.height = '120px';
                  list.appendChild(item);
                }
              }
              append();
              append();
              window.addEventListener('scroll', () => setTimeout(append, 50));
            </script></body></html>
            """;
        await using var page = LoopbackPageServer.StartWithHtml(endlessPage);
        await using var pool = new HeadlessRenderingPool(new RenderingPoolOptions
        {
            InfiniteScrollMaxIterations = 4,
        });

        var rendered = await pool.RenderAsync(new RenderRequest(
            page.BaseUrl, BrowserGuard.DesktopDevice, InfiniteScroll: true, TimeoutSeconds: 60));

        Assert.Contains("item-", rendered.Html);
    }

    [Fact]
    public async Task Render_Timeout_IsIsolated_PoolSurvives()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        // A loopback server that accepts the request and never answers.
        using var hangCts = new CancellationTokenSource();
        await using var hanging = LoopbackPageServer.Start(async context =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), hangCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Test shutdown.
            }
        });
        await using var page = LoopbackPageServer.StartWithHtml(BasicPage);
        await using var pool = new HeadlessRenderingPool();

        var failure = await Assert.ThrowsAsync<RenderFailureException>(() =>
            pool.RenderAsync(new RenderRequest(hanging.BaseUrl, BrowserGuard.DesktopDevice, TimeoutSeconds: 3)));
        Assert.Equal(RenderFailureKind.Timeout, failure.Kind);

        hangCts.Cancel();

        // Failure isolation: the next render succeeds on a fresh browser.
        var rendered = await pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice));
        Assert.Contains("rendered-by-js", rendered.Html);
    }

    [Fact]
    public async Task Render_NavigationFailure_IsIsolated_PoolSurvives()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        await using var page = LoopbackPageServer.StartWithHtml(BasicPage);
        await using var pool = new HeadlessRenderingPool();

        // A loopback port with no listener: connection refused.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var failure = await Assert.ThrowsAsync<RenderFailureException>(() =>
            pool.RenderAsync(new RenderRequest($"http://127.0.0.1:{deadPort}/", BrowserGuard.DesktopDevice, TimeoutSeconds: 15)));
        Assert.Equal(RenderFailureKind.Navigation, failure.Kind);

        var rendered = await pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice));
        Assert.Contains("rendered-by-js", rendered.Html);
    }

    [Fact]
    public async Task Render_HeaderSplit_CredentialsOnlyReachInScopeOrigins()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        const string headerPage = """
            <!doctype html><html><body><script src="http://foreign.example/app.js"></script></body></html>
            """;
        await using var page = LoopbackPageServer.StartWithHtml(headerPage);
        var options = new RenderingPoolOptions
        {
            NonCredentialHeaders = new Dictionary<string, string> { ["X-Cf-Test"] = "noncred" },
            CredentialHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer secret-token" },
            InScopeOrigins = [page.BaseUrl.TrimEnd('/')],
        };
        await using var pool = new HeadlessRenderingPool(options);

        await pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice, TimeoutSeconds: 30));

        List<RecordedRequest> requests;
        lock (page.Requests)
        {
            requests = [.. page.Requests];
        }

        var documentRequest = Assert.Single(requests, r => r.Url == page.BaseUrl);
        Assert.Equal("noncred", documentRequest.Headers["X-Cf-Test"]);
        Assert.Equal("Bearer secret-token", documentRequest.Headers["Authorization"]);
        Assert.Equal("en-US", documentRequest.Headers["Accept-Language"]);
        Assert.DoesNotContain(requests, r => r.Url.Contains("foreign.example"));
    }

    [Fact]
    public async Task Render_StealthProfile_WhenEnabled()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        const string probePage = """
            <!doctype html><html><body><div id="root"></div>
            <script>
              document.getElementById('root').textContent = 'webdriver=' + navigator.webdriver;
            </script></body></html>
            """;
        await using var page = LoopbackPageServer.StartWithHtml(probePage);
        await using var pool = new HeadlessRenderingPool();

        var rendered = await pool.RenderAsync(new RenderRequest(
            page.BaseUrl, BrowserGuard.DesktopDevice, Stealth: true));

        Assert.Contains("webdriver=undefined", rendered.Html);
    }

    [Fact]
    public async Task Render_ServiceWorker_Bypassed_AndRegistrationFactExposed()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        // 127.0.0.1 is a secure context: service workers may register.
        await using var page = LoopbackPageServer.Start(async context =>
        {
            if (context.Request.Url?.AbsolutePath == "/sw.js")
            {
                var sw = Encoding.UTF8.GetBytes("self.addEventListener('fetch', (event) => {});");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/javascript";
                context.Response.ContentLength64 = sw.Length;
                await context.Response.OutputStream.WriteAsync(sw);
                context.Response.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes("""
                <!doctype html><html><body><div id="root">sw-page</div>
                <script>
                  navigator.serviceWorker.register('/sw.js').then(() => {
                    document.getElementById('root').textContent = 'sw-registered';
                  });
                </script></body></html>
                """);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });
        await using var pool = new HeadlessRenderingPool();

        var result = await pool.RenderDetailedAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice));

        // The registration completed (DOM marker) and the fact is exposed.
        Assert.Contains("sw-registered", result.Page.Html);
        Assert.NotEmpty(result.ServiceWorkerRegistrations);
        Assert.Contains(result.ServiceWorkerRegistrations, r => r.ScopeUrl.StartsWith(page.BaseUrl));
        // §7.19: no captured entry was served by a service worker (bypass).
        Assert.All(result.Page.NetworkWaterfall!, e => Assert.False(e.FromServiceWorker));
    }

    [Fact]
    public async Task Render_StreamingSse_DoesNotBlockTheQuietTest()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        using var streamCts = new CancellationTokenSource();
        await using var page = LoopbackPageServer.Start(async context =>
        {
            if (context.Request.Url?.AbsolutePath == "/events")
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.SendChunked = true;
                var payload = Encoding.UTF8.GetBytes("data: hello\n\n");
                await context.Response.OutputStream.WriteAsync(payload);
                await context.Response.OutputStream.FlushAsync();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), streamCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Test shutdown.
                }

                context.Response.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes("""
                <!doctype html><html><body><div id="root">sse-page</div>
                <script>new EventSource('/events');</script></body></html>
                """);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });
        await using var pool = new HeadlessRenderingPool(new RenderingPoolOptions
        {
            RenderWaitQuietMs = 500,
            RenderWaitHardCapMs = 6000,
        });

        var clock = Stopwatch.StartNew();
        var rendered = await pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice));
        clock.Stop();
        streamCts.Cancel();

        Assert.Contains("sse-page", rendered.Html);
        // The open SSE stream must not force the hard cap (§5.6.2 exclusion).
        Assert.True(clock.ElapsedMilliseconds < 5000, $"SSE blocked quiescence: {clock.ElapsedMilliseconds}ms.");
        Assert.Contains(rendered.NetworkWaterfall!, e => e.ResourceType == "eventsource");
    }

    [Fact]
    public async Task Render_HardCap_CapturesState_WhenDomNeverQuiet()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        // Mutate every 25ms: the DOM can never reach the 500ms quiet window,
        // with wide margin even when the full-suite parallel browser load
        // stretches timer delivery. (The historical hard-cap flake was a
        // fractional-timestamp parse bug in the wait-rule probe — fixed in
        // CdpPageSession.ReadDomQuietStateAsync — not this page's cadence.)
        const string busyPage = """
            <!doctype html><html><body><div id="root">busy</div>
            <script>
              let n = 0;
              setInterval(() => {
                const div = document.createElement('div');
                div.textContent = 'tick-' + (n++);
                document.body.appendChild(div);
              }, 25);
            </script></body></html>
            """;
        await using var page = LoopbackPageServer.StartWithHtml(busyPage);
        await using var pool = new HeadlessRenderingPool(new RenderingPoolOptions
        {
            RenderWaitQuietMs = 500,
            RenderWaitHardCapMs = 1500,
        });

        var clock = Stopwatch.StartNew();
        var rendered = await pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice));
        clock.Stop();

        Assert.Contains("busy", rendered.Html);
        Assert.True(clock.ElapsedMilliseconds >= 1400, $"Expected hard-cap wait, got {clock.ElapsedMilliseconds}ms.");
        Assert.True(clock.ElapsedMilliseconds < 15_000, $"Hard cap did not bound the wait: {clock.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task Render_ExplicitProxyOptIn_RoutesCrawlTraffic_LoopbackOnly()
    {
        if (BrowserGuard.TryResolve() is null)
        {
            return;
        }

        await using var page = LoopbackPageServer.StartWithHtml(BasicPage);
        await using var proxy = new LoopbackInterceptingProxy();
        var options = new RenderingPoolOptions { RouteThroughProxy = true };
        await using var pool = new HeadlessRenderingPool(options, proxy: proxy);

        var rendered = await pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice));
        Assert.Contains("rendered-by-js", rendered.Html);

        // Explicit opt-in routes the page's crawl traffic through the loopback proxy.
        var detailed = await proxy.DrainDetailedCapturedAsync();
        Assert.NotEmpty(detailed);

        // The crawl target itself is captured over loopback.
        Assert.Contains(detailed, e => e.Url == page.BaseUrl && e.Method == "GET");

        // STRICT loopback-only boundary — the same assertion the accepted
        // LabProbeIntegrationTests applies: EVERY captured exchange (including
        // any CONNECT tunnel) must be loopback, zero exceptions. The inert
        // about:blank startup tab (CdpBrowserProcess) suppresses the New Tab
        // Page whose search preconnect previously emitted vendor CONNECTs
        // through the launch proxy; with it, zero non-loopback egress is
        // achievable (WP-10-01 §5.3 correction: the earlier "inherent
        // unsuppressible vendor egress" conclusion was NTP startup traffic,
        // not the headless mode). The waterfall stays CDP-direct (CR-006).
        Assert.All(detailed, entry =>
        {
            var host = new Uri(entry.Url).Host;
            Assert.True(IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address),
                $"Non-loopback traffic captured through the launch proxy: {entry.Url}");
        });

        // ...but the waterfall STILL comes from the CDP capture (CR-006).
        Assert.NotNull(rendered.NetworkWaterfall);
        Assert.Contains(rendered.NetworkWaterfall!, e => e.ResourceType == "document");

        // Detailed proxy capture carries per-subresource sizes (CR-031).
        Assert.Contains(detailed, e => e.TransferredBytes > 0);
        // The locked projection keeps the identity fields.
        Assert.All(detailed, e => Assert.Equal(e.Url, e.ToLockedRecord().Url));
    }

    [Fact]
    public async Task Render_PostRenderWafHook_FeedsTheEvaluator()
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
        Assert.NotNull(evaluator.LastInput);
        Assert.Equal(page.BaseUrl, evaluator.LastInput!.Url);
        Assert.Equal(200, evaluator.LastInput.StatusCode);
        Assert.Contains("rendered-by-js", evaluator.LastInput.BodySnippet);
    }

    [Fact]
    public async Task Render_WithArtifactContext_PersistsDom_Screenshot_AndWaterfall()
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
        Assert.Equal(3, artifacts.Count);
        Assert.Contains(artifacts, a => a.Kind == ContentArtifactKind.RenderedDom && a.SizeBytes > 0);
        Assert.Contains(artifacts, a => a.Kind == ContentArtifactKind.Screenshot && a.SizeBytes > 0);
        Assert.Contains(artifacts, a => a.Kind == ContentArtifactKind.NetworkWaterfall && a.SizeBytes > 0);
    }

    [Fact]
    public async Task Render_ConcurrencyIsBounded()
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
            lock (page_lock)
            {
                maxConcurrent = Math.Max(maxConcurrent, now);
            }

            await Task.Delay(300);
            Interlocked.Decrement(ref concurrent);
            var bytes = Encoding.UTF8.GetBytes(BasicPage);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });
        await using var pool = new HeadlessRenderingPool(new RenderingPoolOptions { MaxConcurrency = 1 });
        var renders = Enumerable.Range(0, 3).Select(_ =>
            pool.RenderAsync(new RenderRequest(page.BaseUrl, BrowserGuard.DesktopDevice))).ToArray();
        await Task.WhenAll(renders);

        Assert.Equal(1, maxConcurrent);
        Assert.All(renders, task => Assert.Contains("rendered-by-js", task.Result.Html));
    }

    private static readonly object page_lock = new();

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
