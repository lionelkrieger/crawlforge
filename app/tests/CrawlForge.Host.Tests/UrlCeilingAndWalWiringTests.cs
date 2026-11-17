using System.Net.Http.Json;
using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Host;
using CrawlForge.Host.Execution;
using CrawlForge.Host.Hosting;
using Xunit;

namespace CrawlForge.Host.Tests;

/// <summary>
/// WP-06-01 reopen fix proofs (criterion #4): the HardUrlSafetyCap clamp is
/// removed — FS-CRAWL-001 §4.2 defaults every page ceiling to Unlimited and
/// §4.3 represents unlimited as the absence of a value (never a magic large
/// number). Explicit overrides are honored exactly, including values above the
/// former 100k cap (unit-proven at 100_001 without a 100k crawl). Also proves
/// the WAL-observability composition fix (sqliteWalBytes wired end to end).
/// </summary>
public sealed class UrlCeilingAndWalWiringTests
{
    [Fact]
    public void DefaultCeiling_IsUnlimitedSentinel_NotAMagicNumber()
    {
        // §4.3: unlimited = absence of a value. No overrides and empty
        // overrides both resolve to the null sentinel.
        Assert.Null(AuditExecutionEngine.ResolveUrlCeiling(null));
        Assert.Null(AuditExecutionEngine.ResolveUrlCeiling(new CrawlLimitOverrides()));
        Assert.Null(AuditExecutionEngine.ResolveUrlCeiling(new CrawlLimitOverrides(MaxDepth: 3)));
    }

    [Fact]
    public void ExplicitOverride_AboveFormerCap_IsHonoredExactly()
    {
        // §4.2: override = positive integer or unlimited, no upper bound.
        // 100_001 exceeds the removed 100k clamp and must survive resolution.
        var resolved = AuditExecutionEngine.ResolveUrlCeiling(new CrawlLimitOverrides(MaxUrls: 100_001));
        Assert.Equal(100_001, resolved);

        // And far beyond it — resolution never clamps.
        Assert.Equal(5_000_000, AuditExecutionEngine.ResolveUrlCeiling(new CrawlLimitOverrides(MaxUrls: 5_000_000)));
    }

    [Fact]
    public void UnlimitedCeiling_NeverStops_RegardlessOfFetchedCount()
    {
        // §4.3 no-magic-number assertion: even counts above the former cap
        // never trip an unlimited ceiling.
        Assert.False(AuditExecutionEngine.IsUrlCeilingReached(null, 0));
        Assert.False(AuditExecutionEngine.IsUrlCeilingReached(null, 100_001));
        Assert.False(AuditExecutionEngine.IsUrlCeilingReached(null, long.MaxValue));
    }

    [Fact]
    public void ExplicitCeiling_StopsExactlyAtTheOverride()
    {
        Assert.False(AuditExecutionEngine.IsUrlCeilingReached(100_001, 100_000));
        Assert.True(AuditExecutionEngine.IsUrlCeilingReached(100_001, 100_001));
        Assert.True(AuditExecutionEngine.IsUrlCeilingReached(100_001, 100_002));
        Assert.True(AuditExecutionEngine.IsUrlCeilingReached(5, 5));
        Assert.False(AuditExecutionEngine.IsUrlCeilingReached(5, 4));
    }

    [Fact]
    public void RemovalProvenance_HardUrlSafetyCap_NoLongerExists()
    {
        // Documents the removal (WP-06-01 reopen): the public constant that
        // clamped defaults AND overrides is gone from the engine surface.
        var field = typeof(AuditExecutionEngine).GetField(
            "HardUrlSafetyCap",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.Null(field);
    }

    [Fact]
    public async Task WalObservability_HealthAndMetrics_ReportSqliteWalBytes()
    {
        // WP-06-01 reopen (second defect): SystemHealthSampler must receive the
        // SQLite WAL monitor — /api/system/health and /metrics carry real WAL
        // evidence instead of null.
        var dataDirectory = Path.Combine(Path.GetTempPath(), "crawlforge-host-tests", Guid.NewGuid().ToString("N"));
        var backend = await CrawlForgeBackend.StartAsync(
            [$"--CrawlForge:DataDirectory={dataDirectory}", "--CrawlForge:Port=0"]);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(backend.BaseAddress) };
            http.DefaultRequestHeaders.Add(SessionTokenMiddleware.HeaderName, backend.SessionToken);

            using var health = await http.GetAsync("/api/system/health");
            health.EnsureSuccessStatusCode();
            using var healthJson = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
            var data = healthJson.RootElement.GetProperty("data");
            Assert.True(data.TryGetProperty("sqliteWalBytes", out var wal),
                "Health payload must carry sqliteWalBytes.");
            Assert.NotEqual(JsonValueKind.Null, wal.ValueKind);
            Assert.True(wal.GetInt64() >= 0);

            var metrics = await http.GetStringAsync("/metrics");
            Assert.Contains("sqlite_wal_bytes", metrics);
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
}
