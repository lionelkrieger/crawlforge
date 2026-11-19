using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Element group name for persisted redirect chains. Defined locally (the
/// locked <c>Contracts.Constants.ElementGroups</c> is not editable mid-phase);
/// <c>url_elements</c> is the generic per-URL fact store and new element groups
/// arrive additively (schema catalog: url_elements; precedent: the
/// <c>header_directives</c> fetch-level group written by AuditPipeline).
/// Consumed by the REST URL-detail mapper (WP-09-06) to fill
/// <c>UrlDetailWire.RedirectChain</c>.
/// </summary>
public static class RedirectChainElementGroup
{
    /// <summary>The <c>url_elements.element_group</c> value holding one URL's redirect chain.</summary>
    public const string GroupName = "redirect-chain";
}

/// <summary>
/// One persisted redirect hop. <see cref="Url"/> is the URL that issued the
/// redirect (the hop source); <see cref="ToUrl"/> is its Location target.
/// JSON shape (camelCase): <c>{"ordinal":0,"url":"…","toUrl":"…","statusCode":301}</c>.
/// This is the canonical hop shape shared by storage payload and finding
/// evidence; the REST wire maps it to <c>RedirectHopWire(Url, StatusCode, Location)</c>.
/// </summary>
/// <param name="Ordinal">Zero-based hop position.</param>
/// <param name="Url">URL that issued the redirect (hop source).</param>
/// <param name="ToUrl">Redirect target, when known.</param>
/// <param name="StatusCode">Redirect status code (3xx).</param>
public sealed record RedirectChainHopPayload(int Ordinal, string Url, string? ToUrl, int StatusCode);

/// <summary>
/// Persisted redirect-chain payload for one URL (stored as the
/// <c>redirect-chain</c> element row's <c>payload_json</c>).
/// <see cref="LoopDetected"/> / <see cref="ChainCapExceeded"/> mark chains
/// terminated by a policy violation; the retained hops are the observed
/// prefix (the violating hop itself is rejected by the recorder and lives in
/// the finding evidence). <see cref="FinalUrl"/> / <see cref="FinalStatusCode"/>
/// are the terminal fetch facts, filled by the engine at fetch completion.
/// </summary>
/// <param name="Hops">Observed hops in hop order.</param>
/// <param name="FinalUrl">Terminal URL after redirects, when the fetch completed.</param>
/// <param name="FinalStatusCode">Terminal HTTP status, when the fetch completed.</param>
/// <param name="LoopDetected">Whether the chain was terminated by a redirect loop.</param>
/// <param name="ChainCapExceeded">Whether the chain was terminated by the hop ceiling.</param>
public sealed record RedirectChainPayload(
    IReadOnlyList<RedirectChainHopPayload> Hops,
    string? FinalUrl,
    int? FinalStatusCode,
    bool LoopDetected,
    bool ChainCapExceeded);

/// <summary>
/// Canonical JSON serialization for redirect chains (storage payload and
/// finding evidence share the hop shape). camelCase, deterministic, tolerant
/// deserialization: null/empty/malformed payloads read as absent, never throw.
/// </summary>
public static class RedirectChainJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Serializes hops to the canonical evidence array shape.</summary>
    /// <param name="hops">Recorded hops (any order; serialized as given).</param>
    /// <returns>JSON array of <c>{ordinal,url,toUrl,statusCode}</c> objects.</returns>
    public static string SerializeHops(IReadOnlyList<RedirectHop> hops)
    {
        ArgumentNullException.ThrowIfNull(hops);
        return JsonSerializer.Serialize(
            hops.Select(h => new RedirectChainHopPayload(h.Ordinal, h.FromUrl, h.ToUrl, h.StatusCode)).ToList(),
            Options);
    }

    /// <summary>Serializes a full storage payload.</summary>
    /// <param name="payload">The payload.</param>
    /// <returns>JSON object with hops, finalUrl, finalStatusCode, loopDetected, chainCapExceeded.</returns>
    public static string SerializePayload(RedirectChainPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Deserializes a storage payload tolerantly.</summary>
    /// <param name="json">Stored payload JSON, possibly null/empty/malformed.</param>
    /// <returns>The payload, or null when absent or unreadable.</returns>
    public static RedirectChainPayload? DeserializePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RedirectChainPayload>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Converts a recorder snapshot to a storage payload (terminal facts unknown at hop time).</summary>
    /// <param name="snapshot">Full-chain snapshot emitted by the recorder.</param>
    /// <returns>The payload with hops in ordinal order and violation flags false.</returns>
    public static RedirectChainPayload FromSnapshot(RedirectChainSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new RedirectChainPayload(
            snapshot.Hops.OrderBy(h => h.Ordinal)
                .Select(h => new RedirectChainHopPayload(h.Ordinal, h.FromUrl, h.ToUrl, h.StatusCode))
                .ToList(),
            FinalUrl: null,
            FinalStatusCode: null,
            LoopDetected: false,
            ChainCapExceeded: false);
    }

    /// <summary>Reconstructs contract hops from a storage payload.</summary>
    /// <param name="payload">The stored payload.</param>
    /// <returns>Hops in ordinal order (missing targets become empty strings).</returns>
    public static IReadOnlyList<RedirectHop> ToHops(RedirectChainPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.Hops.OrderBy(h => h.Ordinal)
            .Select(h => new RedirectHop(h.Ordinal, h.Url, h.ToUrl ?? string.Empty, h.StatusCode))
            .ToList();
    }
}

/// <summary>
/// Durable per-URL redirect-chain storage (FS-CRAWL-001 §5.4: every hop is
/// recorded so full chains and loops can be reconstructed; §5.8 incremental
/// persistence). WP-09-01 mechanism (lead-approved): one <c>url_elements</c>
/// row per URL under the additive <see cref="RedirectChainElementGroup.GroupName"/>
/// group — zero schema migration, zero DTO change, zero Contracts edit.
/// Retention follows the URL record (url_elements is pruned with its URL).
/// </summary>
public interface IRedirectChainStore
{
    /// <summary>
    /// Upserts the chain carried by a recorder snapshot (the persistAsync hook
    /// path: called after every recorded hop with the full chain so far).
    /// </summary>
    /// <param name="snapshot">Full-chain snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the chain is durable.</returns>
    Task SaveChainAsync(RedirectChainSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a complete chain outcome (engine fetch-completion path: adds
    /// terminal facts and loop/cap violation flags to the stored payload).
    /// </summary>
    /// <param name="urlId">url_records identifier owning the chain.</param>
    /// <param name="payload">The complete payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the outcome is durable.</returns>
    Task SaveOutcomeAsync(long urlId, RedirectChainPayload payload, CancellationToken cancellationToken = default);

    /// <summary>Reads the stored chain payload of a URL.</summary>
    /// <param name="urlId">url_records identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The payload, or null when the URL has no recorded chain.</returns>
    Task<RedirectChainPayload?> GetPayloadAsync(long urlId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IRedirectChainStore"/> over the locked url_elements repositories
/// (one row per URL, group <c>redirect-chain</c>, ordinal 0). Upserts are
/// serialized through a gate so concurrent hop snapshots of the same URL
/// never create duplicate rows. Thread-safe; intended as a singleton.
/// </summary>
public sealed class UrlElementRedirectChainStore : IRedirectChainStore
{
    private readonly IReadRepository<UrlElementDto> _reads;
    private readonly IWriteRepository<UrlElementDto> _writes;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store over the locked repository contracts.</summary>
    /// <param name="reads">url_elements read repository.</param>
    /// <param name="writes">url_elements write repository.</param>
    public UrlElementRedirectChainStore(IReadRepository<UrlElementDto> reads, IWriteRepository<UrlElementDto> writes)
    {
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
    }

    /// <inheritdoc />
    public Task SaveChainAsync(RedirectChainSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return SaveOutcomeAsync(snapshot.UrlId, RedirectChainJson.FromSnapshot(snapshot), cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveOutcomeAsync(long urlId, RedirectChainPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var json = RedirectChainJson.SerializePayload(payload);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await FindRowAsync(urlId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                await _writes.CreateAsync(
                    new UrlElementDto(0, urlId, RedirectChainElementGroup.GroupName, json, Ordinal: 0),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _writes.UpdateAsync(
                    existing with { PayloadJson = json },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RedirectChainPayload?> GetPayloadAsync(long urlId, CancellationToken cancellationToken = default)
    {
        var row = await FindRowAsync(urlId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : RedirectChainJson.DeserializePayload(row.PayloadJson);
    }

    private async Task<UrlElementDto?> FindRowAsync(long urlId, CancellationToken cancellationToken)
    {
        var page = await _reads.ListAsync(new ListQuery(
            Offset: 0,
            Limit: 1,
            Filters: new Dictionary<string, string>
            {
                ["urlId"] = urlId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["elementGroup"] = RedirectChainElementGroup.GroupName,
            },
            SortBy: "Ordinal"), cancellationToken).ConfigureAwait(false);
        return page.Items.Count > 0 ? page.Items[0] : null;
    }
}

/// <summary>
/// Composition helpers binding the recorder's optional persistAsync hook to
/// durable storage, and building fetch-completion outcomes. The engine
/// (WP-09-11 wiring) constructs the recorder with
/// <c>RedirectChainPersistence.CreatePersistHook(store)</c> and calls
/// <see cref="BuildOutcome"/> + <c>store.SaveOutcomeAsync</c> at fetch
/// completion (including loop/cap failure paths).
/// </summary>
public static class RedirectChainPersistence
{
    /// <summary>
    /// Creates the recorder persistence hook: every recorded hop emits the
    /// full chain snapshot to the store (hop-by-hop durable persistence).
    /// </summary>
    /// <param name="store">Durable chain store.</param>
    /// <returns>The hook delegate for the <see cref="RedirectChainRecorder"/> constructor.</returns>
    public static Func<RedirectChainSnapshot, CancellationToken, Task> CreatePersistHook(IRedirectChainStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return (snapshot, cancellationToken) => store.SaveChainAsync(snapshot, cancellationToken);
    }

    /// <summary>
    /// Builds the terminal chain outcome for fetch completion.
    /// </summary>
    /// <param name="chain">Observed hops (the retained prefix on violations).</param>
    /// <param name="finalUrl">Terminal URL, when the fetch completed.</param>
    /// <param name="finalStatusCode">Terminal HTTP status, when the fetch completed.</param>
    /// <param name="violation">Chain violation that terminated the fetch, when any.</param>
    /// <returns>The payload with violation flags set accordingly.</returns>
    public static RedirectChainPayload BuildOutcome(
        IReadOnlyList<RedirectHop> chain,
        string? finalUrl,
        int? finalStatusCode,
        RedirectChainViolation? violation = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        return new RedirectChainPayload(
            chain.OrderBy(h => h.Ordinal)
                .Select(h => new RedirectChainHopPayload(h.Ordinal, h.FromUrl, h.ToUrl, h.StatusCode))
                .ToList(),
            finalUrl,
            finalStatusCode,
            LoopDetected: violation == RedirectChainViolation.Loop,
            ChainCapExceeded: violation == RedirectChainViolation.ChainCapExceeded);
    }
}
