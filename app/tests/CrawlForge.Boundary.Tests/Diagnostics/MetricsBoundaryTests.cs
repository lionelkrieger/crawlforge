using System.Net;
using CrawlForge.Boundary.Tests.Harness;
using Xunit;

namespace CrawlForge.Boundary.Tests.Diagnostics;

/// <summary>
/// Acceptance criterion #20: /metrics is a pull-only local technical exposition
/// with NO identity dimensions, and metrics are never pushed anywhere. The
/// structured logs produced by the running backend are also scanned for ambient
/// identity (the privacy invariant of the local log sink).
/// </summary>
public sealed class MetricsBoundaryTests
{
    [Fact]
    public async Task Metrics_HaveNoIdentityDimensions_AndAreNeverPushed()
    {
        using var monitor = OutboundConnectionMonitor.Start(BackendFixture.ProcessId);
        await using var fixture = await BackendFixture.StartAsync();

        // Exercise the backend so metrics and logs carry real activity.
        foreach (var route in new[] { "/api/projects", "/api/audits", "/api/health", "/api/version" })
        {
            using var response = await fixture.Http.GetAsync(route);
            Assert.True(response.IsSuccessStatusCode);
        }

        monitor.SetPhase("metrics-pull");
        using var metrics = await fixture.Http.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.StartsWith("text/plain", metrics.Content.Headers.ContentType?.ToString());
        var exposition = await metrics.Content.ReadAsStringAsync();
        Assert.NotEmpty(exposition);

        // No ambient identity values may appear in the exposition.
        var identityValues = new[]
        {
            Environment.UserName,
            Environment.MachineName,
            Environment.UserDomainName,
        };
        foreach (var value in identityValues.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            Assert.DoesNotContain(value, exposition, StringComparison.OrdinalIgnoreCase);
        }

        // No identity-dimension label names.
        string[] identityLabels = ["machine_guid", "machineguid", "hostname", "user_name", "username", "os_user", "device_id", "installation_id"];
        foreach (var label in identityLabels)
        {
            Assert.DoesNotContain(label, exposition, StringComparison.OrdinalIgnoreCase);
        }

        // Pull-only: no outbound push during idle + scrape window.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var nonLoopback = monitor.NonLoopbackObservations;

        // Structured logs: no ambient identity in any local log line.
        var logFiles = Directory.EnumerateFiles(fixture.DataDirectory, "*.log", SearchOption.AllDirectories).ToList();
        var logLeaks = new List<string>();
        foreach (var file in logFiles)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            foreach (var value in identityValues.Where(v => !string.IsNullOrWhiteSpace(v)))
            {
                if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    logLeaks.Add(Path.GetFileName(file));
                }
            }
        }

        EvidenceWriter.WriteMarkdown(
            "metrics-boundary.md",
            $"""
            # Criterion #20 — metrics boundary

            - GET /metrics: 200, text/plain Prometheus-class exposition ({exposition.Length} chars)
            - Ambient identity values absent (user/machine/domain): yes
            - Identity label names absent: yes ({string.Join(", ", identityLabels)})
            - Non-loopback connections during idle + scrape (3s): **{nonLoopback.Count}** (required: 0 — pull-only, never pushed)
            - Local log files scanned: {logFiles.Count}; identity leaks: **{logLeaks.Count}** (required: 0)
            """);

        Assert.Empty(nonLoopback);
        Assert.Empty(logLeaks);
    }
}
