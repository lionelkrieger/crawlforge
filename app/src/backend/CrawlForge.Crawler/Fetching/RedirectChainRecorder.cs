using System.Collections.Concurrent;
using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Full redirect chain of one URL record, emitted after every recorded hop so
/// the composition root can persist it through the storage layer.
/// </summary>
/// <param name="AuditId">Audit identifier.</param>
/// <param name="UrlId">URL record identifier.</param>
/// <param name="Hops">The complete chain in hop order.</param>
public sealed record RedirectChainSnapshot(long AuditId, long UrlId, IReadOnlyList<RedirectHop> Hops)
{
    /// <summary>
    /// Additive (WP-09-01, cf-wp-09-02 request): Set-Cookie header values
    /// observed on each redirect response, keyed by hop ordinal. Empty when the
    /// hops were recorded through the locked <see cref="IRedirectChainRecorder"/>
    /// interface (which carries no headers); populated by the fetcher through
    /// the concrete recorder's cookie-aware overload. Carried for persistence
    /// and evidence — the per-audit cookie jar itself is cf-wp-09-02's scope.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> HopSetCookies { get; init; } =
        new Dictionary<int, IReadOnlyList<string>>();
}

/// <summary>
/// Records hop-by-hop redirect chains per URL (FS-CRAWL-001 §5.4: every hop is
/// recorded so full chains and loops can be reconstructed). Enforces loop
/// detection and a chain cap at record time, and emits a full-chain snapshot
/// through an optional persistence hook after each hop — durable storage is
/// composed by the orchestration layer through the repository stack.
/// </summary>
/// <remarks>
/// Chains are keyed by (audit, URL record) and kept in hop order. Loop
/// detection treats a redirect as a loop when its target (or source) revisits
/// a URL that already issued a redirect at an earlier ordinal in the chain,
/// or the hop redirects to itself; only strictly earlier hops are considered,
/// so hops may be recorded out of order. One self-redirect carrying Set-Cookie
/// is tolerated as a cookie gate when recorded through the cookie-aware
/// overload (cf-wp-09-02). Thread-safe; intended as a process-wide singleton.
/// </remarks>
public sealed class RedirectChainRecorder : IRedirectChainRecorder
{
    private readonly ConcurrentDictionary<(long AuditId, long UrlId), ChainState> _chains = new();
    private readonly Func<RedirectChainSnapshot, CancellationToken, Task>? _persistAsync;
    private readonly int _maxHops;

    /// <summary>Creates a recorder with a hop ceiling and optional persistence hook.</summary>
    /// <param name="maxHops">Maximum hops retained per chain (at least 1).</param>
    /// <param name="persistAsync">
    /// Optional hook invoked with the full chain after each recorded hop; the
    /// composition root wires it to durable storage.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The hop ceiling is less than 1.</exception>
    public RedirectChainRecorder(
        int maxHops = 20,
        Func<RedirectChainSnapshot, CancellationToken, Task>? persistAsync = null)
    {
        if (maxHops < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHops), maxHops, "The chain cap must be at least 1.");
        }

        _maxHops = maxHops;
        _persistAsync = persistAsync;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="hop"/> is null.</exception>
    /// <exception cref="RedirectChainException">The hop forms a loop or exceeds the chain cap.</exception>
    public Task RecordHopAsync(long auditId, long urlId, RedirectHop hop, CancellationToken cancellationToken = default) =>
        RecordHopCoreAsync(auditId, urlId, hop, setCookies: null, cancellationToken);

    /// <summary>
    /// Cookie-aware hop recording (WP-09-01, cf-wp-09-02 request): identical to
    /// the contract method, additionally retaining the redirect response's
    /// Set-Cookie values on the emitted snapshots (keyed by hop ordinal). The
    /// fetcher calls this overload when the injected recorder is this concrete
    /// type; the locked interface path is unchanged.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="urlId">URL record identifier.</param>
    /// <param name="hop">Observed hop.</param>
    /// <param name="setCookies">Set-Cookie values of the redirect response, when present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the hop is recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hop"/> is null.</exception>
    /// <exception cref="RedirectChainException">The hop forms a loop or exceeds the chain cap.</exception>
    public async Task RecordHopAsync(long auditId, long urlId, RedirectHop hop, IReadOnlyList<string>? setCookies, CancellationToken cancellationToken = default) =>
        await RecordHopCoreAsync(auditId, urlId, hop, setCookies, cancellationToken).ConfigureAwait(false);

    private async Task RecordHopCoreAsync(long auditId, long urlId, RedirectHop hop, IReadOnlyList<string>? setCookies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hop);
        cancellationToken.ThrowIfCancellationRequested();

        ChainState state = _chains.GetOrAdd((auditId, urlId), _ => new ChainState());
        RedirectChainSnapshot snapshot;
        lock (state.Gate)
        {
            // Cookie-gate tolerance (cf-wp-09-02): ONE self-redirect carrying
            // Set-Cookie is a gate, not a loop — mirrors the fetcher's
            // IFetchHook.AllowRevisit escape hatch so cookie gates work under a
            // fetch context. Self-redirects without cookies, repeat self-hops,
            // and cross-URL loops stay strict.
            var cookieSelfRevisit = setCookies is { Count: > 0 }
                && string.Equals(hop.FromUrl, hop.ToUrl, StringComparison.OrdinalIgnoreCase)
                && state.CookieSelfRevisits < 1;
            if (cookieSelfRevisit)
            {
                state.CookieSelfRevisits++;
            }
            else if (IsLoop(state, hop))
            {
                throw new RedirectChainException(
                    RedirectChainViolation.Loop,
                    $"Redirect loop detected: '{hop.FromUrl}' -> '{hop.ToUrl}'.");
            }

            if (state.Hops.Count >= _maxHops)
            {
                throw new RedirectChainException(
                    RedirectChainViolation.ChainCapExceeded,
                    $"Redirect chain for URL record {urlId} exceeded the cap of {_maxHops} hops.");
            }

            state.Hops.Add(hop);
            if (setCookies is { Count: > 0 })
            {
                state.SetCookies[hop.Ordinal] = setCookies;
            }

            snapshot = new RedirectChainSnapshot(auditId, urlId, state.Hops.OrderBy(h => h.Ordinal).ToList())
            {
                HopSetCookies = new Dictionary<int, IReadOnlyList<string>>(state.SetCookies),
            };
        }

        if (_persistAsync is not null)
        {
            await _persistAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RedirectHop>> GetChainAsync(long auditId, long urlId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_chains.TryGetValue((auditId, urlId), out ChainState? state))
        {
            return Task.FromResult<IReadOnlyList<RedirectHop>>(Array.Empty<RedirectHop>());
        }

        lock (state.Gate)
        {
            return Task.FromResult<IReadOnlyList<RedirectHop>>(state.Hops.OrderBy(h => h.Ordinal).ToList());
        }
    }

    private static bool IsLoop(ChainState state, RedirectHop hop)
    {
        if (string.Equals(hop.FromUrl, hop.ToUrl, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (RedirectHop earlier in state.Hops)
        {
            if (earlier.Ordinal < hop.Ordinal
                && (string.Equals(earlier.FromUrl, hop.ToUrl, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(earlier.FromUrl, hop.FromUrl, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ChainState
    {
        public object Gate { get; } = new();

        public List<RedirectHop> Hops { get; } = new();

        public Dictionary<int, IReadOnlyList<string>> SetCookies { get; } = new();

        public int CookieSelfRevisits;
    }
}