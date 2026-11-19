using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Traps;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Admission decision of the composed crawl-input gate for one frontier entry.
/// </summary>
/// <param name="Admit">Whether the entry may enter the frontier.</param>
/// <param name="SkipReason">Machine-readable skip reason when not admitted; otherwise null.</param>
public sealed record AdmissionDecision(bool Admit, string? SkipReason)
{
    /// <summary>The entry is allowed into the frontier.</summary>
    public static AdmissionDecision Allowed { get; } = new(true, null);

    /// <summary>Creates a skip decision with the given reason.</summary>
    /// <param name="reason">Machine-readable skip reason.</param>
    /// <returns>The skip decision.</returns>
    public static AdmissionDecision Skip(string reason) => new(false, reason);
}

/// <summary>
/// Composed crawl-input admission gate (FS-CRAWL-001 §5.1 input modes;
/// FS-CRAWL-002 §10.2 List Mode; §5.14 crawler-trap detection). One consult
/// point the frontier/engine calls BEFORE enqueueing any discovered URL,
/// composing the input-mode components in a fixed, deterministic order:
/// <list type="number">
/// <item>sample-crawl page bound (§5.1: depth/page-bounded sampling run) —
/// once the sample's page budget is spent, nothing further is admitted;</item>
/// <item>sample-crawl depth bound;</item>
/// <item>List Mode discovery gate (list-only disables discovery; list-bounded
/// admits only on-list URLs — §10.2);</item>
/// <item>crawler-trap detector (flagged patterns are throttled — §5.14).</item>
/// </list>
/// Every component is optional: with none configured the gate admits
/// everything, reproducing baseline crawl behavior exactly. The gate never
/// fetches or persists; skip decisions carry the underlying component's
/// reason so the engine records them verbatim.
/// <para>
/// Sample bounds arrive as the user-configured <see cref="SampleCrawlSettings"/>
/// and flow through the standard limits resolution
/// (<see cref="SampleCrawlLimits.ToAuditOverrides"/>); the gate enforces the
/// same bounds at admission, so the sample uses the SAME pipeline — no
/// sample-specific resolution path exists (§5.1, criterion #34).
/// </para>
/// </summary>
public sealed class CrawlAdmissionGate
{
    /// <summary>Skip reason when the sample-crawl page budget is spent.</summary>
    public const string SamplePageBoundReason = "sample-page-bound";

    /// <summary>Skip reason when the sample-crawl depth bound is exceeded.</summary>
    public const string SampleDepthBoundReason = "sample-depth-bound";

    private readonly ListDiscoveryGate? _listGate;
    private readonly CrawlerTrapDetector? _trapDetector;
    private readonly SampleCrawlSettings? _sampleSettings;
    private int _fetchedCount;

    /// <summary>Creates a gate over the configured input-mode components.</summary>
    /// <param name="listGate">List Mode discovery gate; null when List Mode is off.</param>
    /// <param name="trapDetector">Crawler-trap detector; null when detection is disabled.</param>
    /// <param name="sampleSettings">Sample-crawl bounds; null for a full (non-sample) run.</param>
    /// <exception cref="ArgumentOutOfRangeException">Sample bounds are invalid.</exception>
    public CrawlAdmissionGate(
        ListDiscoveryGate? listGate = null,
        CrawlerTrapDetector? trapDetector = null,
        SampleCrawlSettings? sampleSettings = null)
    {
        sampleSettings?.Validate();
        _listGate = listGate;
        _trapDetector = trapDetector;
        _sampleSettings = sampleSettings;
    }

    /// <summary>Pages fetched so far (fed by the engine after each fetch).</summary>
    public int FetchedCount => Volatile.Read(ref _fetchedCount);

    /// <summary>Records one completed fetch against the sample page budget.</summary>
    public void RecordFetched() => Interlocked.Increment(ref _fetchedCount);

    /// <summary>
    /// Decides whether a frontier entry may be enqueued. Seeds and list entries
    /// still pass the sample bounds and the trap detector; the List Mode gate
    /// itself always passes seed/list sources (§10.2).
    /// </summary>
    /// <param name="entry">The discovered frontier entry (canonical URL).</param>
    /// <returns>The admission decision; skips carry a machine-readable reason.</returns>
    public AdmissionDecision Admit(FrontierEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_sampleSettings is { } sample)
        {
            if (FetchedCount >= sample.MaxPages)
            {
                return AdmissionDecision.Skip(SamplePageBoundReason);
            }

            if (entry.Depth > sample.MaxDepth)
            {
                return AdmissionDecision.Skip(SampleDepthBoundReason);
            }
        }

        if (_listGate is { } listGate)
        {
            var listDecision = listGate.Decide(entry);
            if (!listDecision.Enqueue)
            {
                return AdmissionDecision.Skip(listDecision.SkipReason ?? "list-mode");
            }
        }

        if (_trapDetector is { } trapDetector)
        {
            var trapAdmission = trapDetector.Admit(entry.Url);
            if (!trapAdmission.Admit)
            {
                return AdmissionDecision.Skip(trapAdmission.SkipReason ?? CrawlerTrapDetector.ThrottleSkipReason);
            }
        }

        return AdmissionDecision.Allowed;
    }
}
