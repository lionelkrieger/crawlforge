using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Pre-crawl probe outcome of one list entry (FS-CRAWL-001 §5.1 pre-crawl
/// start-URL validation; FS-CRAWL-002 §10.2: pre-crawl validation probes a
/// bounded sample of list entries).
/// </summary>
/// <param name="Url">The probed list entry.</param>
/// <param name="IsValid">Whether the entry is usable as a crawl seed.</param>
/// <param name="Issues">Human-readable issues found during validation.</param>
/// <param name="FinalUrl">Final URL after redirects, when reachable.</param>
/// <param name="StatusCode">Observed status code, when reachable.</param>
public sealed record PreCrawlProbeEntry(
    string Url,
    bool IsValid,
    IReadOnlyList<string> Issues,
    string? FinalUrl,
    int? StatusCode);

/// <summary>
/// Pre-crawl probe report surfaced in the audit configuration/start surface
/// (UI-10) BEFORE work begins (FS-CRAWL-001 §5.1: "surface blockers before work
/// begins"). The payload is local and additive; it carries no identity
/// dimensions (privacy invariant).
/// </summary>
/// <param name="Entries">Probed entries in list order (bounded sample).</param>
/// <param name="SkippedInvalidCount">List entries rejected by validation before probing.</param>
/// <param name="ProbedCount">Number of entries actually probed.</param>
/// <param name="ValidCount">Probed entries usable as seeds.</param>
/// <param name="BlockedCount">Probed entries with blockers.</param>
/// <param name="HasBlockers">True when at least one probed entry is blocked.</param>
/// <param name="SampleBoundReached">True when the list exceeded the probe sample bound.</param>
public sealed record PreCrawlProbeReport(
    IReadOnlyList<PreCrawlProbeEntry> Entries,
    int SkippedInvalidCount,
    int ProbedCount,
    int ValidCount,
    int BlockedCount,
    bool HasBlockers,
    bool SampleBoundReached);

/// <summary>
/// Orchestrates pre-crawl start-URL validation over a bounded, deterministic
/// sample of list entries (FS-CRAWL-002 §10.2). Probing runs through the locked
/// <see cref="IPreCrawlValidator"/> — the same protocol/redirect/TLS/login-gate
/// validation the crawl start uses — and never fetches through any other path.
/// Malformed entries are skipped with diagnostics (they never fail the audit);
/// blockers are surfaced in the report for UI-10 before the crawl starts.
/// </summary>
public sealed class ListPreCrawlProbe
{
    /// <summary>Default number of list entries probed (bounded sample).</summary>
    public const int DefaultSampleSize = 10;

    private readonly IPreCrawlValidator _validator;
    private readonly int _sampleSize;

    /// <summary>Creates a probe over the locked pre-crawl validator.</summary>
    /// <param name="validator">Pre-crawl start-URL validator (same probe pipeline as the crawl).</param>
    /// <param name="sampleSize">Maximum entries probed (at least 1); default 10.</param>
    /// <exception cref="ArgumentOutOfRangeException">The sample size is invalid.</exception>
    public ListPreCrawlProbe(IPreCrawlValidator validator, int sampleSize = DefaultSampleSize)
    {
        ArgumentNullException.ThrowIfNull(validator);
        if (sampleSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleSize), sampleSize, "The probe sample must cover at least one entry.");
        }

        _validator = validator;
        _sampleSize = sampleSize;
    }

    /// <summary>
    /// Probes the first <c>sampleSize</c> valid entries of the list in input
    /// order (deterministic selection). Invalid entries are counted and skipped;
    /// probing stops at the sample bound. Cancellation propagates.
    /// </summary>
    /// <param name="urls">List entries (raw user input).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The probe report for the UI-10 start surface.</returns>
    public async Task<PreCrawlProbeReport> ProbeAsync(
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(urls);

        var entries = new List<PreCrawlProbeEntry>();
        var skippedInvalid = 0;
        var validCount = 0;
        var blockedCount = 0;
        var sampleBoundReached = false;

        foreach (var raw in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (raw is null || !ListInputParser.TryValidateUrl(raw.Trim(), out _))
            {
                skippedInvalid++;
                continue;
            }

            if (entries.Count >= _sampleSize)
            {
                sampleBoundReached = true;
                break;
            }

            var url = raw.Trim();
            var result = await _validator.ValidateAsync(url, cancellationToken).ConfigureAwait(false);
            entries.Add(new PreCrawlProbeEntry(url, result.IsValid, result.Issues, result.FinalUrl, result.StatusCode));
            if (result.IsValid)
            {
                validCount++;
            }
            else
            {
                blockedCount++;
            }
        }

        return new PreCrawlProbeReport(
            entries,
            skippedInvalid,
            entries.Count,
            validCount,
            blockedCount,
            blockedCount > 0,
            sampleBoundReached);
    }
}
