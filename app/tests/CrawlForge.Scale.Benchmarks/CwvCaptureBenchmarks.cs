using System.Diagnostics;
using CrawlForge.Audit.Performance;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Scale.Benchmarks.Support;
using Xunit;

namespace CrawlForge.Scale.Benchmarks;

/// <summary>
/// Rendered Core Web Vitals capture benchmark (criterion #19, FS-CRAWL-001
/// §7.11): the production <see cref="LabRenderingProbe"/> (PuppeteerSharp,
/// instrumented local render, zero-vendor-egress posture) measures lab
/// LCP/CLS/INP-proxy/TBT/FCP + coverage + composition against a deterministic
/// loopback origin through local Chrome — no runtime download. The analyzer
/// band path and the requires-rendering guard are proven on the same probe.
/// </summary>
[Trait("Category", "Scale")]
public sealed class CwvCaptureBenchmarks
{
    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const int MeasuredSamples = 24;

    [Fact]
    public async Task Rendered_Cwv_Capture_Through_Real_Lab_Probe_With_Bounded_Memory()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var cancellationToken = timeout.Token;
        ScalePaths.EnsureArtifacts();

        await using var origin = FixtureOrigin.Start(40);
        var device = new DeviceProfile("desktop", 1366, 768, "CrawlForgeLabBench/1.0", false);

        await using var probe = new LabRenderingProbe(new LabProbeOptions
        {
            MaxConcurrency = 2,
            DefaultTimeoutSeconds = 45,
            SettleDelayMs = 500,
            BrowserResolution = new LabBrowserResolutionOptions
            {
                UserConfiguredExecutablePath = File.Exists(ChromePath) ? ChromePath : null
            }
        });

        using var process = Process.GetCurrentProcess();
        var workingSetBefore = process.WorkingSet64;
        var heapBefore = GC.GetGCMemoryInfo().HeapSizeBytes;

        // Warm-up: includes the lazy browser launch (idle-lazy contract).
        var launchWatch = Stopwatch.StartNew();
        var warmup = await probe.CollectAsync(
            new LabVitalsRequest(origin.BaseAddress + "/page-0", device), cancellationToken);
        launchWatch.Stop();
        Assert.NotNull(warmup.LcpMilliseconds);

        // Measured sweep over distinct pages.
        var latencies = new List<double>(MeasuredSamples);
        var samples = new List<object>(MeasuredSamples);
        var lcpMeasured = 0;
        var clsMeasured = 0;
        var inpMeasured = 0;
        for (var i = 1; i <= MeasuredSamples; i++)
        {
            var watch = Stopwatch.StartNew();
            var sample = await probe.CollectAsync(
                new LabVitalsRequest($"{origin.BaseAddress}/page-{i}", device, TimeoutSeconds: 45),
                cancellationToken);
            watch.Stop();
            latencies.Add(watch.Elapsed.TotalMilliseconds);
            if (sample.LcpMilliseconds is not null) lcpMeasured++;
            if (sample.Cls is not null) clsMeasured++;
            if (sample.InpProxyMilliseconds is not null) inpMeasured++;
            samples.Add(new
            {
                page = i,
                latencyMs = watch.Elapsed.TotalMilliseconds,
                lcpMs = sample.LcpMilliseconds,
                cls = sample.Cls,
                inpProxyMs = sample.InpProxyMilliseconds,
                tbtMs = sample.TbtMilliseconds,
                fcpMs = sample.FcpMilliseconds,
                domNodes = sample.Composition?.DomNodes,
                jsCoverage = sample.Coverage is { } c && c.JsTotalBytes > 0
                    ? (double?)Math.Round((double)c.JsUsedBytes / c.JsTotalBytes, 4)
                    : null
            });
        }

        process.Refresh();
        var workingSetAfter = process.WorkingSet64;
        var heapAfter = GC.GetGCMemoryInfo().HeapSizeBytes;

        // Analyzer band path on the same probe (rendering enabled).
        var analyzer = new CoreWebVitalsAnalyzer(probe: probe);
        var url = new UrlRecordDto(
            999_001, 1, origin.BaseAddress + "/page-25", "127.0.0.1", "/page-25", 0,
            DiscoverySource.Seed, CrawlStatusClass.Success, 200, null, "text/html", 2048,
            null, RobotsState.Allowed, IndexabilityFlags.None, true);
        var configuration = new CrawlConfigurationDto(
            1, 1, "cwv-bench", "[]", null, null, null, RenderMode.Auto,
            null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var context = new PageAuditContext(1, url, configuration, null, null, null, null, null);
        var analyzed = await analyzer.AnalyzeAsync(context, cancellationToken);
        Assert.NotNull(analyzed.LcpMilliseconds);
        Assert.NotNull(analyzed.Cls);
        Assert.NotEmpty(analyzed.Findings);

        // Requires-rendering guard: rendering off → null vitals + notice.
        var offlineConfiguration = configuration with { RenderingMode = RenderMode.None };
        var guarded = await analyzer.AnalyzeAsync(context with { Configuration = offlineConfiguration }, cancellationToken);
        Assert.Null(guarded.LcpMilliseconds);
        Assert.NotEmpty(guarded.Findings);

        var ordered = latencies.OrderBy(value => value).ToArray();
        var median = ordered[ordered.Length / 2];
        var p95 = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];

        ScaleBackend.WriteResult("cwv-capture", new
        {
            browser = File.Exists(ChromePath) ? "local Chrome 157 (user-configured path)" : "local install resolution",
            probe = "production LabRenderingProbe (PuppeteerSharp, instrumented render, zero-vendor-egress)",
            warmupLaunchMs = launchWatch.Elapsed.TotalMilliseconds,
            measuredSamples = MeasuredSamples,
            lcpMeasured,
            clsMeasured,
            inpMeasured,
            medianLatencyMs = median,
            p95LatencyMs = p95,
            maxLatencyMs = ordered[^1],
            analyzerBandFindings = analyzed.Findings.Count,
            requiresRenderingGuard = "null vitals + notice when RenderMode.None",
            backendWorkingSetBefore = workingSetBefore,
            backendWorkingSetAfter = workingSetAfter,
            managedHeapBefore = heapBefore,
            managedHeapAfter = heapAfter,
            samples
        });

        Assert.True(lcpMeasured >= MeasuredSamples - 2, $"LCP measured on only {lcpMeasured}/{MeasuredSamples} pages.");
        Assert.True(clsMeasured >= MeasuredSamples - 2, $"CLS measured on only {clsMeasured}/{MeasuredSamples} pages.");
        Assert.True(inpMeasured >= MeasuredSamples - 2, $"INP proxy measured on only {inpMeasured}/{MeasuredSamples} pages.");
        Assert.True(median < 10_000, $"Median capture latency {median:F0}ms exceeds the 10s local budget.");
        Assert.True(workingSetAfter - workingSetBefore < 500_000_000,
            "Backend working set grew beyond the 500MB probe bound (Chrome runs out-of-process).");
    }
}
