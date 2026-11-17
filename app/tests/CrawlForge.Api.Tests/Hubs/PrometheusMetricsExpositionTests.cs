using CrawlForge.Api.Metrics;
using Xunit;

namespace CrawlForge.Api.Tests.Hubs;

/// <summary>
/// Prometheus exposition tests (criterion #20; FS-CRAWL-001 §10.5,
/// FS-CRAWL-002 §3.7/§7.3): pull-only text format, counter vs gauge
/// semantics, runtime memory/GC/WAL families, host-throttle families,
/// and the identity-label privacy guard.
/// </summary>
public sealed class PrometheusMetricsExpositionTests
{
    [Fact]
    public void Render_GaugesAndCounters_WithHelpAndType()
    {
        var metrics = new PrometheusMetricsExposition();
        metrics.Record(MetricFamilyCatalog.FrontierDepth, 42);
        metrics.Record(MetricFamilyCatalog.CrawlUrlsTotal, 3,
            new Dictionary<string, string> { ["status_class"] = "success" });
        metrics.Record(MetricFamilyCatalog.CrawlUrlsTotal, 4,
            new Dictionary<string, string> { ["status_class"] = "success" });

        var text = metrics.Render();
        Assert.Contains("# HELP frontier_depth", text);
        Assert.Contains("# TYPE frontier_depth gauge", text);
        Assert.Contains("frontier_depth 42", text);
        Assert.Contains("# TYPE crawl_urls_total counter", text);
        Assert.Contains("crawl_urls_total{status_class=\"success\"} 7", text);
    }

    [Fact]
    public void Record_Gauge_KeepsLatestValue()
    {
        var metrics = new PrometheusMetricsExposition();
        metrics.Record(MetricFamilyCatalog.HubConnections, 5);
        metrics.Record(MetricFamilyCatalog.HubConnections, 2);
        Assert.Contains("hub_connections 2", metrics.Render());
        Assert.DoesNotContain("hub_connections 5", metrics.Render());
    }

    [Fact]
    public void Render_LabelsSortedAndEscaped()
    {
        var metrics = new PrometheusMetricsExposition();
        metrics.Record("custom_family", 1, new Dictionary<string, string>
        {
            ["zebra"] = "line\nbreak",
            ["alpha"] = "quote\"and\\slash",
        });

        var text = metrics.Render();
        Assert.Contains("custom_family{alpha=\"quote\\\"and\\\\slash\",zebra=\"line\\nbreak\"} 1", text);
    }

    [Theory]
    [InlineData("user_id")]
    [InlineData("machineId")]
    [InlineData("account")]
    [InlineData("IDENTITY_KEY")]
    [InlineData("license_state")]
    [InlineData("email")]
    public void Record_IdentityDimensions_Rejected(string labelKey)
    {
        var metrics = new PrometheusMetricsExposition();
        Assert.Throws<ArgumentException>(() =>
            metrics.Record("some_family", 1, new Dictionary<string, string> { [labelKey] = "x" }));
    }

    [Fact]
    public void Record_InvalidLabelName_Rejected()
    {
        var metrics = new PrometheusMetricsExposition();
        Assert.Throws<ArgumentException>(() =>
            metrics.Record("some_family", 1, new Dictionary<string, string> { ["bad-name"] = "x" }));
    }

    [Fact]
    public void Record_NonFinite_Rejected()
    {
        var metrics = new PrometheusMetricsExposition();
        Assert.Throws<ArgumentOutOfRangeException>(() => metrics.Record("some_family", double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => metrics.Record("some_family", double.PositiveInfinity));
    }

    [Fact]
    public void Render_RuntimeFamilies_RefreshFromSampler()
    {
        var sampler = new TestHealthSampler();
        var metrics = new PrometheusMetricsExposition(sampler);

        var text = metrics.Render();
        Assert.Contains("# TYPE process_working_set_bytes gauge", text);
        Assert.Contains("process_working_set_bytes 1000000", text);
        Assert.Contains("process_managed_heap_bytes 500000", text);
        Assert.Contains("gc_collections_total{generation=\"0\"} 3", text);
        Assert.Contains("gc_collections_total{generation=\"1\"} 2", text);
        Assert.Contains("gc_collections_total{generation=\"2\"} 1", text);
        Assert.Contains("sqlite_wal_bytes 4096", text);

        sampler.Payload = sampler.Payload with { BackendWorkingSetBytes = 9_000_000 };
        Assert.Contains("process_working_set_bytes 9000000", metrics.Render());
    }

    [Fact]
    public void Render_ExternalProvider_WalFamilyAbsent()
    {
        var sampler = new TestHealthSampler { Payload = new TestHealthSampler().Payload with { SqliteWalBytes = null } };
        var metrics = new PrometheusMetricsExposition(sampler);
        Assert.DoesNotContain("sqlite_wal_bytes", metrics.Render());
    }

    [Fact]
    public void Render_HostThrottleFamilies()
    {
        var metrics = new PrometheusMetricsExposition();
        metrics.Record(MetricFamilyCatalog.HostEffectiveRate, 2.5,
            new Dictionary<string, string> { ["host"] = "a.test" });
        metrics.Record(MetricFamilyCatalog.HostConcurrency, 4,
            new Dictionary<string, string> { ["host"] = "a.test" });
        metrics.Record(MetricFamilyCatalog.HostThrottleAdjustmentsTotal, 1,
            new Dictionary<string, string> { ["trigger"] = "ttfb" });
        metrics.Record(MetricFamilyCatalog.HostThrottleAdjustmentsTotal, 1,
            new Dictionary<string, string> { ["trigger"] = "ttfb" });

        var text = metrics.Render();
        Assert.Contains("host_effective_rate{host=\"a.test\"} 2.5", text);
        Assert.Contains("host_concurrency{host=\"a.test\"} 4", text);
        Assert.Contains("host_throttle_adjustments_total{trigger=\"ttfb\"} 2", text);
    }

    [Fact]
    public void Render_Empty_ExpositionIsEmpty()
    {
        Assert.Equal(string.Empty, new PrometheusMetricsExposition().Render());
    }

    [Fact]
    public void Render_FamiliesSorted_Deterministic()
    {
        var metrics = new PrometheusMetricsExposition();
        metrics.Record("zeta_family", 1);
        metrics.Record("alpha_family", 2);
        var text = metrics.Render();
        Assert.True(text.IndexOf("alpha_family", StringComparison.Ordinal)
            < text.IndexOf("zeta_family", StringComparison.Ordinal));
    }
}
