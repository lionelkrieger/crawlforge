using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Performance;

/// <summary>
/// Local performance analyzer options (FS-CRAWL-001 §7.11, optional
/// category). Thresholds default to the publicly documented Core Web Vitals
/// bands; everything is user-configurable locally — no remote resolution.
/// </summary>
public sealed class PerformanceAnalyzerOptions
{
    private static readonly JsonSerializerOptions ToggleJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Category enablement; when false the analyzers produce nothing.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>LCP "good" upper bound, milliseconds (web.dev: 2500).</summary>
    public double LcpGoodMilliseconds { get; init; } = 2500;

    /// <summary>LCP "poor" lower bound, milliseconds (web.dev: 4000).</summary>
    public double LcpPoorMilliseconds { get; init; } = 4000;

    /// <summary>CLS "good" upper bound (web.dev: 0.1).</summary>
    public double ClsGood { get; init; } = 0.1;

    /// <summary>CLS "poor" lower bound (web.dev: 0.25).</summary>
    public double ClsPoor { get; init; } = 0.25;

    /// <summary>INP "good" upper bound, milliseconds (web.dev: 200).</summary>
    public double InpGoodMilliseconds { get; init; } = 200;

    /// <summary>INP "poor" lower bound, milliseconds (web.dev: 500).</summary>
    public double InpPoorMilliseconds { get; init; } = 500;

    /// <summary>Unused-code finding threshold, percent (0–100).</summary>
    public double UnusedCodePercentThreshold { get; init; } = 50;

    /// <summary>Minimum total code bytes before unused-code findings fire.</summary>
    public long UnusedCodeMinTotalBytes { get; init; } = 100 * 1024;

    /// <summary>Default lab measurement timeout, seconds.</summary>
    public int LabTimeoutSeconds { get; init; } = 30;

    /// <summary>Device profile for lab measurements (desktop default).</summary>
    public DeviceProfile Device { get; init; } = new(
        "desktop-lab",
        1280,
        800,
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        false);

    /// <summary>
    /// Builds options from the per-category audit enablement payload
    /// (AuditTogglesJson): a JSON object mapping category names
    /// (case-insensitive, e.g. "performance") to booleans. Missing or
    /// malformed payloads keep the baseline default: enabled.
    /// </summary>
    /// <param name="auditTogglesJson">The enablement payload, when present.</param>
    /// <returns>Options reflecting the local category toggle.</returns>
    public static PerformanceAnalyzerOptions FromAuditTogglesJson(string? auditTogglesJson)
    {
        var enabled = true;
        if (!string.IsNullOrWhiteSpace(auditTogglesJson))
        {
            try
            {
                using var document = JsonDocument.Parse(auditTogglesJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (string.Equals(property.Name, nameof(AuditCategory.Performance), StringComparison.OrdinalIgnoreCase)
                            && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            enabled = property.Value.GetBoolean();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed local payload: keep the baseline default (enabled).
            }
        }

        return new PerformanceAnalyzerOptions { Enabled = enabled };
    }
}
