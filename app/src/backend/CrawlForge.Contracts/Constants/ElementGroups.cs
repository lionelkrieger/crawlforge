namespace CrawlForge.Contracts.Constants;

/// <summary>
/// Element inventory group names. Groups are open strings so new groups arrive
/// additively (schema catalog: url_elements.element group). The constants below
/// are the additive groups defined by FS-CRAWL-002 Appendix A (url_elements row).
/// </summary>
public static class ElementGroups
{
    /// <summary>Additive: content-scope diagnostics (FS-CRAWL-002 §4.4).</summary>
    public const string ContentScope = "content_scope";

    /// <summary>Additive: llms.txt structure inventory (FS-CRAWL-002 §13.4).</summary>
    public const string LlmsStructure = "llms_structure";

    /// <summary>Additive: consent state snapshot (FS-CRAWL-002 §14.4).</summary>
    public const string ConsentState = "consent_state";

    /// <summary>Additive: bounded dataLayer snapshot (FS-CRAWL-002 §14.4).</summary>
    public const string DataLayerSnapshot = "datalayer_snapshot";

    /// <summary>Additive: SERP pixel metrics (FS-CRAWL-002 §15.5).</summary>
    public const string SerpMetrics = "serp_metrics";

    /// <summary>Additive: HTTP header directives (FS-CRAWL-002 §15.5).</summary>
    public const string HeaderDirectives = "header_directives";
}

/// <summary>
/// Real-time hub event names. Events are open strings so new events arrive
/// additively alongside existing ones (FS-CRAWL-002 §1.4 rule 3). The constants
/// below are the event names fixed by the specification surface; the hub
/// publishes a 2-second system-health heartbeat and throttled progress events.
/// </summary>
public static class HubEventNames
{
    /// <summary>Per-host AIMD throttle adjustments (FS-CRAWL-002 §3).</summary>
    public const string HostThrottle = "host-throttle";

    /// <summary>System health heartbeat published every 2 seconds (FS-CRAWL-002 §7).</summary>
    public const string SystemHealth = "system-health";
}
