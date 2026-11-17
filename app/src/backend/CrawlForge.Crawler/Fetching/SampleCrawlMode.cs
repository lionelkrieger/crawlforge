using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// User-configured sample-crawl bounds (FS-CRAWL-001 §5.1): a depth/page-bounded
/// sampling run for a quick preview.
/// </summary>
/// <param name="MaxPages">Maximum pages to fetch in the sample (at least 1).</param>
/// <param name="MaxDepth">Maximum link depth from the seeds (at least 0).</param>
public sealed record SampleCrawlSettings(int MaxPages, int MaxDepth)
{
    /// <summary>Validates the bounds.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is invalid.</exception>
    public void Validate()
    {
        if (MaxPages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPages), MaxPages, "A sample must allow at least one page.");
        }

        if (MaxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDepth), MaxDepth, "The depth bound cannot be negative.");
        }
    }
}

/// <summary>
/// Sample-crawl limits integration (§5.1: "using the same pipeline and limits
/// resolution"). Sample bounds are expressed as ordinary per-audit
/// <see cref="CrawlLimitOverrides"/> and flow through the standard
/// <see cref="ILimitsResolver"/> priority order (audit → project → application
/// → built-in). No sample-specific resolution path exists, and no other layer
/// is consulted or altered (criterion #34; precedence rule 5).
/// </summary>
public static class SampleCrawlLimits
{
    /// <summary>
    /// Converts sample bounds into the per-audit override layer. The caller
    /// passes the result as <c>auditOverrides</c> to
    /// <see cref="ILimitsResolver.Resolve"/> together with the unchanged
    /// project/application layers.
    /// </summary>
    /// <param name="settings">Sample bounds.</param>
    /// <returns>The per-audit override layer carrying the sample bounds.</returns>
    public static CrawlLimitOverrides ToAuditOverrides(SampleCrawlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return new CrawlLimitOverrides(MaxUrls: settings.MaxPages, MaxDepth: settings.MaxDepth);
    }
}

/// <summary>
/// One page observed by a sample-crawl run.
/// </summary>
/// <param name="Url">Fetched URL.</param>
/// <param name="Depth">Link depth from the seeds (seeds are depth 0).</param>
/// <param name="StatusCode">Final HTTP status, when the fetch produced a response.</param>
/// <param name="StatusClass">Classified outcome.</param>
/// <param name="Error">Failure classification label, when the fetch failed.</param>
public sealed record SampleCrawlPage(string Url, int Depth, int? StatusCode, CrawlStatusClass StatusClass, string? Error);

/// <summary>
/// Summary of a sample-crawl run.
/// </summary>
/// <param name="Pages">Observed pages in fetch order.</param>
/// <param name="PageBoundReached">True when the page bound stopped the run.</param>
public sealed record SampleCrawlSummary(IReadOnlyList<SampleCrawlPage> Pages, bool PageBoundReached);

/// <summary>
/// Executes a depth/page-bounded sampling run through the SAME fetch pipeline
/// as the full crawl (§5.1). Bounds arrive as already-resolved limits — the
/// runner never resolves or alters limits itself. Link discovery is supplied
/// by the composition root (the full pipeline's discovery stage); without it
/// the sample probes the seed set only.
/// </summary>
public sealed class SampleCrawlRunner
{
    private readonly IHttpFetcher _fetcher;

    /// <summary>Creates a sample runner over the shared fetch pipeline.</summary>
    /// <param name="fetcher">The same fetcher the full crawl uses.</param>
    /// <exception cref="ArgumentNullException">The fetcher is null.</exception>
    public SampleCrawlRunner(IHttpFetcher fetcher)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
    }

    /// <summary>Runs the bounded sample.</summary>
    /// <param name="seedUrls">Seed URLs (depth 0).</param>
    /// <param name="limits">Effective limits resolved through the standard resolver.</param>
    /// <param name="linkDiscovery">
    /// Optional discovery callback returning outlinks of a fetched page; the
    /// full pipeline plugs its discovery stage in here.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sample summary.</returns>
    /// <exception cref="ArgumentNullException">Seeds or limits is null.</exception>
    public async Task<SampleCrawlSummary> RunAsync(
        IReadOnlyList<string> seedUrls,
        ResolvedCrawlLimits limits,
        Func<SampleCrawlPage, IReadOnlyList<string>>? linkDiscovery = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seedUrls);
        ArgumentNullException.ThrowIfNull(limits);

        var pages = new List<SampleCrawlPage>();
        var frontier = new Queue<(string Url, int Depth)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string seed in seedUrls)
        {
            if (seen.Add(seed))
            {
                frontier.Enqueue((seed, 0));
            }
        }

        bool pageBoundReached = false;
        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (limits.MaxUrls is long maxUrls && pages.Count >= maxUrls)
            {
                pageBoundReached = true;
                break;
            }

            (string url, int depth) = frontier.Dequeue();
            SampleCrawlPage page = await FetchPageAsync(url, depth, cancellationToken).ConfigureAwait(false);
            pages.Add(page);

            if (linkDiscovery is null || page.StatusClass != CrawlStatusClass.Success)
            {
                continue;
            }

            if (limits.MaxDepth is int maxDepth && depth + 1 > maxDepth)
            {
                continue;
            }

            foreach (string link in linkDiscovery(page))
            {
                if (seen.Add(link))
                {
                    frontier.Enqueue((link, depth + 1));
                }
            }
        }

        return new SampleCrawlSummary(pages, pageBoundReached);
    }

    private async Task<SampleCrawlPage> FetchPageAsync(string url, int depth, CancellationToken cancellationToken)
    {
        try
        {
            HttpFetchResult result = await _fetcher
                .FetchAsync(new HttpFetchRequest(url), cancellationToken)
                .ConfigureAwait(false);
            return new SampleCrawlPage(url, depth, result.StatusCode, FetchStatusClassifier.Classify(result.StatusCode), null);
        }
        catch (FetchFailedException ex)
        {
            return new SampleCrawlPage(url, depth, null, CrawlStatusClass.FetchError, ex.Kind.ToString());
        }
    }
}