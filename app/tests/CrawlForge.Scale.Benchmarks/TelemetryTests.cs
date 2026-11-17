using System.Diagnostics;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Scale.Benchmarks.Support;
using Xunit;

namespace CrawlForge.Scale.Benchmarks;

/// <summary>
/// Telemetry validation (criteria #19/#41, FS-CRAWL-002 §7.2/§7.3): live
/// Server-GC diagnostics, the 2-second system-health heartbeat measured on the
/// real hub wire, /metrics family parity with the heartbeat snapshot, and
/// Compact/Trim working-set reduction with truncating WAL checkpoint evidence.
/// Every measurement is published as a durable result fragment.
/// </summary>
[Trait("Category", "Scale")]
public sealed class TelemetryTests
{
    private static readonly string[] ForbiddenLabelFragments =
    [
        "user", "account", "identity", "machine", "license", "entitlement", "email", "profile", "credential"
    ];

    [Fact]
    public async Task Diagnostics_Surface_Reports_Active_Server_GC_Profile()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var backend = await ScaleBackend.StartAsync(ScalePaths.SeededDataDirectory, timeout.Token);

        using var report = await backend.GetJsonAsync("/api/diagnostics/version", timeout.Token);
        var gc = report.RootElement.GetProperty("gc");
        var server = gc.GetProperty("server").GetBoolean();
        var concurrent = gc.GetProperty("concurrent").GetBoolean();

        ScaleBackend.WriteResult("diagnostics-gc", new
        {
            server,
            concurrent,
            runtime = report.RootElement.GetProperty("runtime").GetString(),
            source = "GET /api/diagnostics/version (live GCSettings)"
        });

        Assert.True(server, "Server GC must be active (process-model spec §13).");
        Assert.True(concurrent, "Concurrent GC must be active (process-model spec §13).");
    }

    [Fact]
    public async Task SystemHealth_Rest_Snapshot_Carries_Live_Runtime_Fields()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var backend = await ScaleBackend.StartAsync(ScalePaths.SeededDataDirectory, timeout.Token);
        await using var pressure = await WalPressure.CreateAsync(backend.Services, 500, timeout.Token);

        var health = await backend.GetDataAsync<SystemHealthWire>("/api/system/health", timeout.Token);
        Assert.True(health.BackendWorkingSetBytes > 0);
        Assert.True(health.ManagedHeapBytes > 0);
        Assert.True(health.GcGen0Count >= 0);
        Assert.NotNull(health.SqliteWalBytes);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - health.Timestamp).TotalSeconds) < 10);

        ScaleBackend.WriteResult("system-health-rest", health);
    }

    [Fact]
    public async Task Heartbeat_Arrives_Every_2_Seconds_With_Live_Payload()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var backend = await ScaleBackend.StartAsync(ScalePaths.SeededDataDirectory, timeout.Token);
        await using var probe = await HubProbe.ConnectAsync(backend.BaseAddress, backend.SessionToken, timeout.Token);

        const int samples = 5;
        var arrivals = new List<double>(samples);
        var payloads = new List<JsonElement>(samples);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < samples; i++)
        {
            using var frame = await probe.ReceiveEventAsync("system-health", timeout.Token);
            arrivals.Add(clock.Elapsed.TotalSeconds);
            payloads.Add(frame.RootElement.GetProperty("arguments")[0].Clone());
        }

        var intervals = new List<double>();
        for (var i = 1; i < arrivals.Count; i++)
        {
            intervals.Add(arrivals[i] - arrivals[i - 1]);
        }

        // Every payload carries the locked runtime fields.
        foreach (var payload in payloads)
        {
            Assert.True(payload.GetProperty("backendWorkingSetBytes").GetInt64() > 0);
            Assert.True(payload.GetProperty("managedHeapBytes").GetInt64() > 0);
            Assert.True(payload.TryGetProperty("gcGen0Count", out _));
            Assert.True(payload.TryGetProperty("timestamp", out _));
        }

        // 2-second cadence: allow scheduler/load jitter, reject drift.
        Assert.All(intervals, interval =>
            Assert.True(interval is >= 1.2 and <= 2.8, $"Heartbeat interval {interval:F3}s outside the 2s ± 0.8s band."));
        var mean = intervals.Average();
        Assert.True(mean is >= 1.7 and <= 2.3, $"Mean heartbeat interval {mean:F3}s drifted from 2s.");

        ScaleBackend.WriteResult("heartbeat-accuracy", new
        {
            samples,
            intervalsSeconds = intervals,
            meanIntervalSeconds = mean,
            targetSeconds = 2.0,
            firstPayload = payloads[0]
        });
    }

    [Fact]
    public async Task Metrics_Exposition_Has_Family_Parity_With_Heartbeat()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var backend = await ScaleBackend.StartAsync(ScalePaths.SeededDataDirectory, timeout.Token);
        await using var pressure = await WalPressure.CreateAsync(backend.Services, 500, timeout.Token);

        var exposition = await backend.GetTextAsync("/metrics", timeout.Token);
        var health = await backend.GetDataAsync<SystemHealthWire>("/api/system/health", timeout.Token);

        // Privacy invariant: no identity/machine/account dimensions anywhere.
        foreach (var fragment in ForbiddenLabelFragments)
        {
            Assert.DoesNotContain(fragment, exposition, StringComparison.OrdinalIgnoreCase);
        }

        var samples = ParseExposition(exposition);
        Assert.True(samples.TryGetValue("process_working_set_bytes", out var workingSet), "missing process_working_set_bytes");
        Assert.True(samples.TryGetValue("process_managed_heap_bytes", out var managedHeap), "missing process_managed_heap_bytes");
        Assert.True(samples.TryGetValue("sqlite_wal_bytes", out var wal), "missing sqlite_wal_bytes");
        Assert.Contains("gc_collections_total{generation=\"0\"}", samples.Keys);
        Assert.Contains("gc_collections_total{generation=\"1\"}", samples.Keys);
        Assert.Contains("gc_collections_total{generation=\"2\"}", samples.Keys);

        // Parity: the exposition and the REST heartbeat observe the same runtime.
        // Values are live gauges sampled moments apart — require the same band,
        // not byte equality.
        Assert.True(workingSet > 0 && managedHeap > 0 && wal >= 0);
        Assert.InRange(workingSet / health.BackendWorkingSetBytes, 0.5, 2.0);
        Assert.InRange(managedHeap / Math.Max(1, health.ManagedHeapBytes), 0.5, 2.0);

        ScaleBackend.WriteResult("metrics-parity", new
        {
            families = samples.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
            expositionWorkingSet = workingSet,
            heartbeatWorkingSet = health.BackendWorkingSetBytes,
            expositionManagedHeap = managedHeap,
            heartbeatManagedHeap = health.ManagedHeapBytes,
            expositionWalBytes = wal,
            heartbeatWalBytes = health.SqliteWalBytes,
            identityDimensionsFound = false
        });
    }

    [Fact]
    public async Task Compact_Trims_Working_Set_And_Checkpoints_Wal()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var backend = await ScaleBackend.StartAsync(ScalePaths.SeededDataDirectory, timeout.Token);

        // Grow the WAL with real repository writes and hold the connection open
        // so the WAL file stays observable (SQLite drops it on last close).
        await using var pressure = await WalPressure.CreateAsync(backend.Services, 2_000, timeout.Token);

        // Managed garbage pressure so the trim has reclaimable pages.
        var garbage = new List<byte[]>(256);
        for (var i = 0; i < 256; i++)
        {
            garbage.Add(new byte[1024 * 1024]);
        }
        garbage.Clear();

        var before = await backend.GetDataAsync<SystemHealthWire>("/api/system/health", timeout.Token);
        Assert.True(before.SqliteWalBytes > 0, "WAL should carry the probe writes before compact.");

        var compact = await backend.PostDataAsync<SystemCompactResponse>("/api/system/compact", null, timeout.Token);

        Assert.Contains("managed-gc-compaction", compact.ActionsPerformed);
        Assert.Contains("wal-checkpoint-truncate", compact.ActionsPerformed);
        Assert.Contains("working-set-trim", compact.ActionsPerformed);

        // WAL checkpoint evidence: truncate mode collapses the WAL.
        Assert.True(compact.After.SqliteWalBytes < before.SqliteWalBytes,
            $"WAL did not shrink: {before.SqliteWalBytes} → {compact.After.SqliteWalBytes}.");

        // Working-set reduction under garbage pressure + OS trim.
        Assert.True(compact.After.BackendWorkingSetBytes <= compact.Before.BackendWorkingSetBytes,
            $"Working set grew across compact: {compact.Before.BackendWorkingSetBytes} → {compact.After.BackendWorkingSetBytes}.");

        // Locked rate-limit guard: an immediate second invocation is a conflict.
        using var second = await backend.Http.PostAsync("/api/system/compact",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, second.StatusCode);

        ScaleBackend.WriteResult("compact-trim", new
        {
            actions = compact.ActionsPerformed,
            workingSetBefore = compact.Before.BackendWorkingSetBytes,
            workingSetAfter = compact.After.BackendWorkingSetBytes,
            workingSetReductionBytes = compact.Before.BackendWorkingSetBytes - compact.After.BackendWorkingSetBytes,
            managedHeapBefore = compact.Before.ManagedHeapBytes,
            managedHeapAfter = compact.After.ManagedHeapBytes,
            walBefore = before.SqliteWalBytes,
            walAfter = compact.After.SqliteWalBytes,
            rateLimitGuard = "409 Conflict on immediate second invocation"
        });
    }

    private static Dictionary<string, double> ParseExposition(string exposition)
    {
        var samples = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in exposition.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }
            var space = trimmed.LastIndexOf(' ');
            if (space <= 0)
            {
                continue;
            }
            var key = trimmed[..space];
            if (double.TryParse(trimmed[(space + 1)..], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                samples[key] = value;
            }
        }
        return samples;
    }
}
