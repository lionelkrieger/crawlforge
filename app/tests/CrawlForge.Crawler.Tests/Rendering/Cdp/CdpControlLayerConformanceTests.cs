using System.Diagnostics;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Proxy;
using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// CDP control-layer lifecycle conformance (D-002/CR-006 component proof):
/// the REAL pool + CDP client + page session run against a scripted loopback
/// CDP browser. Proves the Target/Page/Network/Runtime domain lifecycle,
/// fresh contexts, the service-worker bypass, direct waterfall capture,
/// console capture, the wait rule, header split, resource blocking, and
/// timeout/failure isolation — deterministically, without a real browser.
/// </summary>
public sealed class CdpControlLayerConformanceTests
{
    private static RenderRequest Request(string url, bool stealth = false, bool infiniteScroll = false, int? timeoutSeconds = null) =>
        new(url, BrowserGuard.DesktopDevice, InfiniteScroll: infiniteScroll, Stealth: stealth, TimeoutSeconds: timeoutSeconds);

    [Fact]
    public async Task Render_IssuesDomainLifecycle_AndBypassesServiceWorkers()
    {
        await using var harness = FakeRenderHarness.Create();
        var rendered = await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page"));

        Assert.Contains("rendered-by-js", rendered.Html);
        var commands = harness.Server.Commands;

        // Target-domain lifecycle: fresh context → target → flat attach.
        Assert.Contains(commands, c => c.Method == "Target.createBrowserContext");
        Assert.Contains(commands, c => c.Method == "Target.createTarget");
        var attach = Assert.Single(commands, c => c.Method == "Target.attachToTarget");
        Assert.True(attach.Params.GetProperty("flatten").GetBoolean());

        // Page/Network/Runtime (+ServiceWorker) domains enabled on the session.
        var sessionId = attach.SessionId is null
            ? commands.First(c => c.Method == "Page.enable").SessionId
            : attach.SessionId;
        Assert.NotNull(sessionId);
        foreach (var domain in new[] { "Page.enable", "Network.enable", "Runtime.enable", "ServiceWorker.enable" })
        {
            Assert.Contains(commands, c => c.Method == domain && c.SessionId == sessionId);
        }

        // §7.19: service workers bypassed on EVERY render.
        var bypass = Assert.Single(commands, c => c.Method == "Network.setBypassServiceWorker");
        Assert.True(bypass.Params.GetProperty("bypass").GetBoolean());
        Assert.Equal(sessionId, bypass.SessionId);

        // Emulation + header surface applied.
        Assert.Contains(commands, c => c.Method == "Emulation.setDeviceMetricsOverride");
        Assert.Contains(commands, c => c.Method == "Emulation.setUserAgentOverride");
        Assert.Contains(commands, c => c.Method == "Network.setExtraHTTPHeaders");

        // Teardown: target closed, fresh context disposed.
        Assert.Contains(commands, c => c.Method == "Target.closeTarget");
        Assert.Contains(commands, c => c.Method == "Target.disposeBrowserContext");
    }

    [Fact]
    public async Task Render_CapturesWaterfallDirectly_WithTransferredBytes()
    {
        await using var harness = FakeRenderHarness.Create(options =>
        {
            options.Subresources.Add(new FakeSubresource("http://127.0.0.1:9/pixel.gif", "Image", "image/gif", EncodedBytes: 43));
            options.Subresources.Add(new FakeSubresource("http://127.0.0.1:9/app.js", "Script", "text/javascript", EncodedBytes: 2048));
        });

        var rendered = await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page"));

        // CR-006: the waterfall comes from the CDP Network domain, not a proxy.
        Assert.NotNull(rendered.NetworkWaterfall);
        Assert.Equal(3, rendered.NetworkWaterfall!.Count);
        var image = Assert.Single(rendered.NetworkWaterfall, e => e.ResourceType == "image");
        Assert.Equal("http://127.0.0.1:9/pixel.gif", image.Url);
        Assert.Equal(200, image.StatusCode);
        Assert.Equal("image/gif", image.MimeType);
        Assert.True(image.TransferredBytes >= 43); // CR-031 measurable
        Assert.Equal(43, image.EncodedDataLength);
        Assert.Contains(rendered.NetworkWaterfall, e => e.ResourceType == "document");
        Assert.Contains(rendered.NetworkWaterfall, e => e.ResourceType == "script");
    }

    [Fact]
    public async Task Render_CapturesConsole_AndRuntimeExceptions()
    {
        await using var harness = FakeRenderHarness.Create();
        var rendered = await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page"));

        Assert.Contains(rendered.ConsoleMessages, m => m.Level == "warning" && m.Text.Contains("warn-message"));
        Assert.Contains(rendered.ConsoleMessages, m => m.Level == "error" && m.Text.Contains("error-message"));
        Assert.Contains(rendered.ConsoleMessages, m => m.Level == "error" && m.Text.Contains("boom is not a function"));
    }

    [Fact]
    public async Task Render_ScreenshotIsPng_AndFinalUrlObserved()
    {
        await using var harness = FakeRenderHarness.Create(options =>
        {
            options.FinalUrl = "http://127.0.0.1:9/after-client-redirect";
        });
        var rendered = await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page"));

        Assert.True(rendered.Screenshot.Length > 8);
        Assert.Equal(0x89, rendered.Screenshot.Span[0]);
        Assert.Equal((byte)'P', rendered.Screenshot.Span[1]);
        Assert.Equal("http://127.0.0.1:9/after-client-redirect", rendered.FinalUrl);
        Assert.Null(rendered.PostRenderWafVerdict);
    }

    [Fact]
    public async Task TwoRenders_ReuseBrowser_ButUseFreshContexts()
    {
        await using var harness = FakeRenderHarness.Create();
        await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/one"));
        await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/two"));

        Assert.Equal(1, harness.Factory.Launches); // long-lived pooled browser
        var commands = harness.Server.Commands;
        Assert.Equal(2, commands.Count(c => c.Method == "Target.createBrowserContext"));
        Assert.Equal(2, commands.Count(c => c.Method == "Target.disposeBrowserContext"));
        Assert.Equal(2, commands.Count(c => c.Method == "Target.closeTarget"));
        Assert.Equal(2, commands.Count(c => c.Method == "Network.setBypassServiceWorker"));
    }

    [Fact]
    public async Task RenderTimeout_IsIsolated_PoolSurvives()
    {
        await using var harness = FakeRenderHarness.Create(options => options.StallNavigate = true);

        var failure = await Assert.ThrowsAsync<RenderFailureException>(() =>
            harness.Pool.RenderAsync(Request("http://127.0.0.1:9/slow", timeoutSeconds: 1)));
        Assert.Equal(RenderFailureKind.Timeout, failure.Kind);

        // The faulted browser is retired; the next render launches a fresh one.
        harness.Server.Options.StallNavigate = false;
        var rendered = await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/next"));
        Assert.Contains("rendered-by-js", rendered.Html);
        Assert.Equal(2, harness.Factory.Launches);
    }

    [Fact]
    public async Task NavigationFailure_IsIsolated_PoolSurvives()
    {
        await using var harness = FakeRenderHarness.Create(options =>
            options.NavigateError = "net::ERR_CONNECTION_REFUSED");

        var failure = await Assert.ThrowsAsync<RenderFailureException>(() =>
            harness.Pool.RenderAsync(Request("http://127.0.0.1:9/dead")));
        Assert.Equal(RenderFailureKind.Navigation, failure.Kind);

        harness.Server.Options.NavigateError = null;
        var rendered = await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/alive"));
        Assert.Contains("rendered-by-js", rendered.Html);
    }

    [Fact]
    public async Task ServiceWorkerRegistrations_AreExposed_AsFacts()
    {
        await using var harness = FakeRenderHarness.Create(options =>
        {
            options.ServiceWorkerRegistrations.Add(new FakeServiceWorkerRegistration(
                "reg-1", "http://127.0.0.1:9/", "http://127.0.0.1:9/sw.js"));
        });

        var result = await harness.Pool.RenderDetailedAsync(Request("http://127.0.0.1:9/page"));

        var registration = Assert.Single(result.ServiceWorkerRegistrations);
        Assert.Equal("http://127.0.0.1:9/", registration.ScopeUrl);
        Assert.Equal("http://127.0.0.1:9/sw.js", registration.ScriptUrl);
        // Bypass still applied on the render that observed the registration.
        Assert.Contains(harness.Server.Commands, c => c.Method == "Network.setBypassServiceWorker");
        Assert.NotNull(result.Page.NetworkWaterfall);
    }

    [Fact]
    public async Task StealthProfile_AppliedOnlyWhenRequested()
    {
        await using var harness = FakeRenderHarness.Create();
        await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page", stealth: true));

        var scripts = harness.Server.Commands
            .Where(c => c.Method == "Page.addScriptToEvaluateOnNewDocument")
            .Select(c => c.Params.GetProperty("source").GetString() ?? string.Empty)
            .ToList();
        Assert.Contains(scripts, s => s.Contains("webdriver"));

        var before = harness.Server.Commands.Count;
        await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page", stealth: false));
        var newScripts = harness.Server.Commands.Skip(before)
            .Where(c => c.Method == "Page.addScriptToEvaluateOnNewDocument")
            .Select(c => c.Params.GetProperty("source").GetString() ?? string.Empty)
            .ToList();
        // The DOM-quiet observer is always injected; stealth is not.
        Assert.All(newScripts, s => Assert.DoesNotContain("webdriver", s));
        Assert.NotEmpty(newScripts);
    }

    [Fact]
    public async Task CredentialHeaders_GoOnlyToInScopeOrigins()
    {
        await using var harness = FakeRenderHarness.Create(
            options =>
            {
                options.FetchPausedRequests.Add(new FakePausedRequest("pause-1", "http://127.0.0.1:9/page", "Document"));
                options.FetchPausedRequests.Add(new FakePausedRequest("pause-2", "http://foreign.example/script.js", "Script"));
            },
            new RenderingPoolOptions
            {
                RenderWaitQuietMs = 100,
                RenderWaitHardCapMs = 5000,
                CredentialHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer secret-token" },
                InScopeOrigins = ["http://127.0.0.1:9"],
            });

        await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page"));
        await WaitForCommandsAsync(harness.Server, cmds => cmds.Count(c => c.Method == "Fetch.continueRequest") == 2);

        Assert.Contains(harness.Server.Commands, c => c.Method == "Fetch.enable");
        var continues = harness.Server.Commands.Where(c => c.Method == "Fetch.continueRequest").ToList();
        Assert.Equal(2, continues.Count);

        var inScope = Assert.Single(continues, c => c.Params.GetProperty("requestId").GetString() == "pause-1");
        var headers = inScope.Params.GetProperty("headers").EnumerateArray()
            .ToDictionary(h => h.GetProperty("name").GetString()!, h => h.GetProperty("value").GetString());
        Assert.Equal("Bearer secret-token", headers["Authorization"]);

        var foreign = Assert.Single(continues, c => c.Params.GetProperty("requestId").GetString() == "pause-2");
        if (foreign.Params.TryGetProperty("headers", out var foreignHeaders))
        {
            Assert.DoesNotContain(foreignHeaders.EnumerateArray(),
                h => h.GetProperty("name").GetString() == "Authorization");
        }
    }

    [Fact]
    public async Task FastBlockingProfile_BlocksFontsAndMedia_Only()
    {
        await using var harness = FakeRenderHarness.Create(
            options =>
            {
                options.FetchPausedRequests.Add(new FakePausedRequest("pause-font", "http://127.0.0.1:9/font.woff2", "Font"));
                options.FetchPausedRequests.Add(new FakePausedRequest("pause-media", "http://127.0.0.1:9/clip.mp4", "Media"));
                options.FetchPausedRequests.Add(new FakePausedRequest("pause-img", "http://127.0.0.1:9/img.png", "Image"));
            },
            new RenderingPoolOptions
            {
                RenderWaitQuietMs = 100,
                RenderWaitHardCapMs = 5000,
                BlockedResourceTypes = ResourceBlockingProfiles.Fast,
            });

        await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page"));
        await WaitForCommandsAsync(harness.Server, cmds =>
            cmds.Count(c => c.Method == "Fetch.failRequest") == 2
            && cmds.Count(c => c.Method == "Fetch.continueRequest") == 1);

        var failed = harness.Server.Commands.Where(c => c.Method == "Fetch.failRequest").ToList();
        Assert.Equal(2, failed.Count);
        Assert.All(failed, c =>
        {
            Assert.Equal("BlockedByClient", c.Params.GetProperty("errorReason").GetString());
            var id = c.Params.GetProperty("requestId").GetString();
            Assert.True(id == "pause-font" || id == "pause-media");
        });
        Assert.Contains(harness.Server.Commands,
            c => c.Method == "Fetch.continueRequest" && c.Params.GetProperty("requestId").GetString() == "pause-img");
    }

    [Fact]
    public async Task DefaultProfile_BlocksNothing_NoFetchInterception()
    {
        await using var harness = FakeRenderHarness.Create();
        await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page"));
        Assert.DoesNotContain(harness.Server.Commands, c => c.Method == "Fetch.enable");
        Assert.DoesNotContain(harness.Server.Commands, c => c.Method == "Fetch.failRequest");
    }

    [Fact]
    public async Task WaitRule_HardCap_CapturesCurrentState_WhenDomNeverQuiet()
    {
        await using var harness = FakeRenderHarness.Create(
            options => options.DomQuietMs = 0, // DOM mutates forever
            new RenderingPoolOptions { RenderWaitQuietMs = 100, RenderWaitHardCapMs = 400 });

        var clock = Stopwatch.StartNew();
        var rendered = await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/busy"));
        clock.Stop();

        // The hard cap bounds the wait; capture still succeeds (§5.6.2).
        Assert.Contains("rendered-by-js", rendered.Html);
        Assert.True(clock.ElapsedMilliseconds >= 380, $"Expected hard-cap wait, got {clock.ElapsedMilliseconds}ms.");
        Assert.True(clock.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public async Task WaitRule_StreamingConnections_DoNotBlockSettle()
    {
        await using var harness = FakeRenderHarness.Create(options =>
        {
            // An SSE stream and a websocket stay open forever.
            options.Subresources.Add(new FakeSubresource("http://127.0.0.1:9/sse", "EventSource", "text/event-stream", 0, LeaveOpen: true));
            options.Subresources.Add(new FakeSubresource("http://127.0.0.1:9/ws", "WebSocket", "application/octet-stream", 0, LeaveOpen: true));
        });

        var clock = Stopwatch.StartNew();
        var rendered = await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/streaming"));
        clock.Stop();

        Assert.Contains("rendered-by-js", rendered.Html);
        Assert.True(clock.ElapsedMilliseconds < 3000, "Streaming requests must be excluded from the quiet test.");
        Assert.Contains(rendered.NetworkWaterfall!, e => e.ResourceType == "eventsource");
        Assert.Contains(rendered.NetworkWaterfall!, e => e.ResourceType == "websocket");
    }

    [Fact]
    public async Task WaitRule_LongPolling_IsExcluded_AfterThreshold()
    {
        await using var harness = FakeRenderHarness.Create(
            options =>
            {
                options.OnNavigate = async connection =>
                {
                    await connection.EmitAsync("Network.requestWillBeSent", new Dictionary<string, object?>
                    {
                        ["requestId"] = "doc",
                        ["type"] = "Document",
                        ["request"] = new Dictionary<string, object?> { ["url"] = "http://127.0.0.1:9/page", ["method"] = "GET" },
                    });
                    await connection.EmitAsync("Network.loadingFinished", new Dictionary<string, object?>
                    {
                        ["requestId"] = "doc",
                        ["encodedDataLength"] = 10,
                    });
                    await connection.EmitAsync("Page.frameNavigated", new Dictionary<string, object?>
                    {
                        ["frame"] = new Dictionary<string, object?> { ["id"] = "f", ["url"] = "http://127.0.0.1:9/page" },
                    });
                    // A long-poll XHR that never receives a response.
                    await connection.EmitAsync("Network.requestWillBeSent", new Dictionary<string, object?>
                    {
                        ["requestId"] = "poll",
                        ["type"] = "XHR",
                        ["request"] = new Dictionary<string, object?> { ["url"] = "http://127.0.0.1:9/poll", ["method"] = "GET" },
                    });
                };
            },
            new RenderingPoolOptions
            {
                RenderWaitQuietMs = 100,
                RenderWaitHardCapMs = 5000,
                StreamingPendingThresholdMs = 150,
            });

        var clock = Stopwatch.StartNew();
        var rendered = await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page"));
        clock.Stop();

        Assert.Contains("rendered-by-js", rendered.Html);
        Assert.True(clock.ElapsedMilliseconds < 3000, "Long-polling must be excluded from the quiet test.");
    }

    [Fact]
    public async Task InfiniteScroll_StopsWhenContentStabilizes()
    {
        await using var harness = FakeRenderHarness.Create(options =>
        {
            options.ScrollHeights.Clear();
            options.ScrollHeights.AddRange([800, 1600, 1600, 1600, 1600, 1600]);
        });

        var rendered = await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/scroll", infiniteScroll: true));
        Assert.Contains("rendered-by-js", rendered.Html);

        var scrolls = harness.Server.Commands.Count(c =>
            c.Method == "Runtime.evaluate"
            && c.Params.GetProperty("expression").GetString()!.StartsWith("window.scrollTo"));
        // 800 → 1600 → stable check 1 → stable check 2 ends it.
        Assert.Equal(3, scrolls);
    }

    [Fact]
    public async Task InfiniteScroll_EndlessContent_StopsAtIterationBound()
    {
        await using var harness = FakeRenderHarness.Create(
            options =>
            {
                options.ScrollHeights.Clear();
                options.ScrollHeights.AddRange([800, 1600, 2400, 3200, 4000, 4800, 5600, 6400]);
            },
            new RenderingPoolOptions
            {
                RenderWaitQuietMs = 100,
                RenderWaitHardCapMs = 5000,
                InfiniteScrollMaxIterations = 3,
            });

        await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/endless", infiniteScroll: true));

        var scrolls = harness.Server.Commands.Count(c =>
            c.Method == "Runtime.evaluate"
            && c.Params.GetProperty("expression").GetString()!.StartsWith("window.scrollTo"));
        Assert.Equal(3, scrolls);
    }

    [Fact]
    public async Task Concurrency_IsBounded_ByPoolSize()
    {
        await using var harness = FakeRenderHarness.Create(
            options => options.NavigateDelayMs = 400,
            new RenderingPoolOptions
            {
                MaxConcurrency = 2,
                RenderWaitQuietMs = 100,
                RenderWaitHardCapMs = 8000,
                DefaultTimeoutSeconds = 30,
            });

        var renders = Enumerable.Range(0, 4).Select(i =>
            harness.Pool.RenderAsync(Request($"http://127.0.0.1:9/page-{i}"))).ToArray();
        var results = await Task.WhenAll(renders);

        Assert.All(results, page => Assert.Contains("rendered-by-js", page.Html));
        Assert.True(harness.Server.MaxConcurrentNavigates <= 2);
        Assert.Equal(2, harness.Factory.Launches); // two long-lived browsers, reused
    }

    [Fact]
    public async Task ExplicitProxyOptIn_PassesLoopbackEndpointToBrowser()
    {
        await using var proxy = new LoopbackInterceptingProxy();
        await using var harness = FakeRenderHarness.Create(
            options => { },
            new RenderingPoolOptions
            {
                RenderWaitQuietMs = 100,
                RenderWaitHardCapMs = 5000,
                RouteThroughProxy = true,
            },
            proxy: proxy);

        await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page"));
        Assert.NotNull(harness.Factory.LastProxyEndpoint);
        Assert.Equal("127.0.0.1", harness.Factory.LastProxyEndpoint!.Host);
    }

    [Fact]
    public async Task DefaultRender_DoesNotRouteThroughProxy()
    {
        await using var proxy = new LoopbackInterceptingProxy();
        await using var harness = FakeRenderHarness.Create(options => { }, proxy: proxy);

        await harness.Pool.RenderAsync(Request("http://127.0.0.1:9/page"));
        // CR-006: rendered traffic is not proxy-routed by default.
        Assert.Null(harness.Factory.LastProxyEndpoint);
        Assert.Empty(await proxy.DrainCapturedAsync());
    }

    [Fact]
    public async Task IdlePool_LaunchesNothing_AndOpensNoOutbound()
    {
        var connector = new CountingConnector(throwOnConnect: true);
        await using var proxy = new LoopbackInterceptingProxy(connector: connector);
        await using var harness = FakeRenderHarness.Create(
            options => { },
            new RenderingPoolOptions
            {
                RenderWaitQuietMs = 100,
                RenderWaitHardCapMs = 5000,
                RouteThroughProxy = true,
            },
            proxy: proxy);

        await Task.Delay(150);
        Assert.Equal(0, harness.Factory.Launches);
        Assert.Equal(0, connector.Attempts);
        Assert.Empty(await harness.Pool.DrainNetworkCaptureAsync());
    }

    [Fact]
    public async Task BrowserIdentity_IsRecorded_ForTheRender()
    {
        await using var harness = FakeRenderHarness.Create();
        var result = await harness.Pool.RenderDetailedAsync(Request("http://127.0.0.1:9/page"));

        // §5.6.3: the browser product/version used is recorded per render.
        Assert.Equal(BrowserSource.Bundled, result.Browser.Source);
        Assert.Equal("Chrome for Testing", result.Browser.Product);
        Assert.Equal("131.0.6778.85", result.Browser.Version);
        Assert.Equal("131.0.6778.85", result.Browser.PinnedVersion);
    }

    private static async Task WaitForCommandsAsync(FakeCdpServer server, Func<IReadOnlyList<FakeCdpServer.FakeCommand>, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition(server.Commands))
            {
                return;
            }

            await Task.Delay(20);
        }
    }
}
