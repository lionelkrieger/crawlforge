using System.Runtime;
using System.Runtime.InteropServices;

namespace CrawlForge.Host.Diagnostics;

/// <summary>
/// Version/diagnostics stub (acceptance criterion #19 verification hook). Reports the
/// ACTIVE runtime GC profile — server garbage collection with concurrent collection —
/// as mandated by process-model spec §13. Local-only surface; nothing here is
/// transmitted anywhere (clean-room guardrail #6).
/// </summary>
public static class DiagnosticsSurface
{
    public static object CreateReport()
    {
        // Live runtime state: IsServerGC reflects the active GC mode; the concurrent
        // setting is read from the runtime configuration knob (System.GC.Concurrent),
        // with the GC latency mode as a fallback (Batch = non-concurrent).
        var server = GCSettings.IsServerGC;
        var concurrent = AppContext.GetData("System.GC.Concurrent") as bool?
            ?? GCSettings.LatencyMode != GCLatencyMode.Batch;

        return new
        {
            product = "CrawlForge",
            version = typeof(DiagnosticsSurface).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            runtime = RuntimeInformation.FrameworkDescription,
            gc = new
            {
                server,
                concurrent
            },
            featureAvailability = new
            {
                remoteFlagResolution = false,
                allFeaturesEnabledByDefault = true
            }
        };
    }
}
