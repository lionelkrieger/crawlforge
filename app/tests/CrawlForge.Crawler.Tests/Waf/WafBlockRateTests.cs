using System.Globalization;
using System.Text;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Tests.Waf.Support;
using CrawlForge.Crawler.Waf;
using Xunit;

namespace CrawlForge.Crawler.Tests.Waf;

/// <summary>
/// Site-wide waf-block-rate diagnostic: 1% Warning / 5% Error thresholds,
/// the 20-URL minimum sample, site-wide finding persistence, idempotency, and
/// the waf-blocks / waf-block-rate metric summaries.
/// </summary>
public class WafBlockRateTests : IAsyncLifetime
{
    private readonly WafTestDatabase _db = new();

    /// <summary>Creates the per-test fixture with the parent graph.</summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await _db.InsertGraphAsync();
    }

    /// <summary>Tears down the per-test fixture.</summary>
    /// <returns>A task that completes when teardown finished.</returns>
    public Task DisposeAsync() => _db.DisposeAsync();

    /// <summary>Below the minimum sample the diagnostic never reports, even at a high block share.</summary>
    [Fact]
    public async Task BelowMinimumSampleNeverReports()
    {
        await SeedUrlsAsync(total: 19, blocked: 5);
        var diagnostic = new WafBlockRateDiagnostic(_db.Provider);
        var report = await diagnostic.ReportAsync(1);
        Assert.True(report.InsufficientSample);
        Assert.Null(report.Severity);
        Assert.Equal(0, await _db.CountRowsAsync("findings"));
    }

    /// <summary>A 1% block rate reports a site-wide Warning finding.</summary>
    [Fact]
    public async Task OnePercentIsWarning()
    {
        await SeedUrlsAsync(total: 100, blocked: 1);
        var diagnostic = new WafBlockRateDiagnostic(_db.Provider);
        var report = await diagnostic.ReportAsync(1);
        Assert.False(report.InsufficientSample);
        Assert.Equal(0.01, report.BlockRate);
        Assert.Equal(FindingSeverity.Warning, report.Severity);
        Assert.Equal(FindingRuleKeys.WafBlockRate, await _db.ScalarAsync("SELECT rule_key FROM findings;"));
        Assert.Equal(DBNull.Value, await _db.ScalarAsync("SELECT url_id FROM findings;"));
        Assert.Equal((int)FindingSeverity.Warning, Convert.ToInt32(await _db.ScalarAsync("SELECT severity FROM findings;")));
    }

    /// <summary>A 5% block rate reports a site-wide Error finding.</summary>
    [Fact]
    public async Task FivePercentIsError()
    {
        await SeedUrlsAsync(total: 100, blocked: 5);
        var diagnostic = new WafBlockRateDiagnostic(_db.Provider);
        var report = await diagnostic.ReportAsync(1);
        Assert.Equal(0.05, report.BlockRate);
        Assert.Equal(FindingSeverity.Error, report.Severity);
        Assert.Equal((int)FindingSeverity.Error, Convert.ToInt32(await _db.ScalarAsync("SELECT severity FROM findings;")));
    }

    /// <summary>Below 1% the diagnostic stays silent but still records the metrics.</summary>
    [Fact]
    public async Task BelowOnePercentIsSilent()
    {
        await SeedUrlsAsync(total: 100, blocked: 0);
        var diagnostic = new WafBlockRateDiagnostic(_db.Provider);
        var report = await diagnostic.ReportAsync(1);
        Assert.Null(report.Severity);
        Assert.Equal(0, await _db.CountRowsAsync("findings"));
        Assert.Equal(2, await _db.CountRowsAsync("metric_summaries"));
    }

    /// <summary>Exactly the minimum sample is eligible: 1 of 20 URLs is a 5% Error.</summary>
    [Fact]
    public async Task MinimumSampleBoundaryReports()
    {
        await SeedUrlsAsync(total: 20, blocked: 1);
        var diagnostic = new WafBlockRateDiagnostic(_db.Provider);
        var report = await diagnostic.ReportAsync(1);
        Assert.False(report.InsufficientSample);
        Assert.Equal(0.05, report.BlockRate);
        Assert.Equal(FindingSeverity.Error, report.Severity);
    }

    /// <summary>Repeated reporting never duplicates the finding or the metrics.</summary>
    [Fact]
    public async Task ReportIsIdempotent()
    {
        await SeedUrlsAsync(total: 100, blocked: 5);
        var diagnostic = new WafBlockRateDiagnostic(_db.Provider);
        await diagnostic.ReportAsync(1);
        await diagnostic.ReportAsync(1);
        Assert.Equal(1, await _db.CountRowsAsync("findings"));
        Assert.Equal(2, await _db.CountRowsAsync("metric_summaries"));
    }

    /// <summary>The waf-blocks count and waf-block-rate share are persisted as metric summaries.</summary>
    [Fact]
    public async Task MetricsArePersisted()
    {
        await SeedUrlsAsync(total: 100, blocked: 5);
        var diagnostic = new WafBlockRateDiagnostic(_db.Provider);
        await diagnostic.ReportAsync(1);

        var blocks = await _db.ScalarAsync($"SELECT metric_value FROM metric_summaries WHERE metric_key = '{MetricKeys.WafBlocks}';");
        var rate = await _db.ScalarAsync($"SELECT metric_value FROM metric_summaries WHERE metric_key = '{MetricKeys.WafBlockRate}';");
        Assert.Equal(5.0, Convert.ToDouble(blocks, CultureInfo.InvariantCulture));
        Assert.Equal(0.05, Convert.ToDouble(rate, CultureInfo.InvariantCulture));
    }

    /// <summary>Only waf-challenge blocks count toward the rate; other block reasons are excluded.</summary>
    [Fact]
    public async Task OnlyWafChallengeBlocksCount()
    {
        await SeedUrlsAsync(total: 100, blocked: 5, blockReason: "robots-denied");
        var diagnostic = new WafBlockRateDiagnostic(_db.Provider);
        var report = await diagnostic.ReportAsync(1);
        Assert.Equal(0, report.WafBlockedUrls);
        Assert.Null(report.Severity);
        Assert.Equal(0, await _db.CountRowsAsync("findings"));
    }

    private async Task SeedUrlsAsync(int total, int blocked, string blockReason = WafDetectionRecorder.WafChallengeBlockReason)
    {
        var sql = new StringBuilder(
            "INSERT INTO url_records (id, audit_id, canonical_url, host, path, depth, discovery_source, status_class, robots_state, indexability_flags, is_indexable) VALUES ");
        for (var i = 1; i <= total; i++)
        {
            if (i > 1)
            {
                sql.Append(", ");
            }

            sql.Append(CultureInfo.InvariantCulture,
                $"({i}, 1, 'https://example.com/page-{i}', 'example.com', '/page-{i}', 0, 0, 1, 1, 0, 1)");
        }

        sql.Append(';');
        await _db.ExecuteAsync(sql.ToString());

        if (blocked > 0)
        {
            await _db.ExecuteAsync(
                $"UPDATE url_records SET status_class = {(int)CrawlStatusClass.Blocked}, block_reason = '{blockReason}', " +
                $"waf_provider = {(int)WafProvider.Cloudflare}, challenge_class = {(int)WafChallengeClass.Interstitial} " +
                $"WHERE audit_id = 1 AND id <= {blocked};");
        }
    }
}
