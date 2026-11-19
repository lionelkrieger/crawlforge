using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Redirect Migration Matrix Validator (FS-CRAWL-002 §10.3, criterion #43).
/// Every Source_URL is fetched hop-by-hop through the locked
/// <see cref="IHttpFetcher"/> contract with the full redirect chain recorded;
/// the final destination and the final page's declared canonical are compared
/// against Expected_Target_URL under canonicalized comparison with configurable
/// strictness (exact | normalized via the §10.5 host mapping).
/// <para>
/// Verdict precedence — one primary verdict per row, first failure wins
/// (§10.3); all evidence is retained regardless:
/// 1. chain-exceeded (hop ceiling or loop), 2. final-non-200,
/// 3. target-mismatch, 4. canonical-mismatch, 5. pass.
/// </para>
/// Row verdicts persist as findings and as <c>redirect-matrix</c> verdict
/// aggregates in <c>metric_summaries</c> (§8.1); validated rows are emitted to
/// the hub as throttled <c>matrix-row-validated</c> batches (§10.8). Outbound
/// traffic goes only to the user-configured matrix URLs via the fetcher.
/// </summary>
public sealed class RedirectMatrixValidator : IRedirectMatrixValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly int[] RedirectStatusCodes = [301, 302, 303, 307, 308];

    private readonly IHttpFetcher _fetcher;
    private readonly IDbProvider _provider;
    private readonly IUrlCanonicalizer _canonicalizer;
    private readonly IRealtimeHub _hub;
    private readonly ListModeOptions _options;
    private readonly HostMapper _hostMapper;

    /// <summary>Creates the validator over the locked contracts.</summary>
    /// <param name="fetcher">Locked HTTP fetcher (user-configured targets only).</param>
    /// <param name="provider">Configured database provider for findings and aggregates.</param>
    /// <param name="canonicalizer">Locked URL canonicalizer (§5.2 comparison).</param>
    /// <param name="hub">Locked realtime hub receiving matrix-row-validated batches.</param>
    /// <param name="options">List Mode options (hop ceiling, strictness, host mapping); null uses the baseline.</param>
    public RedirectMatrixValidator(
        IHttpFetcher fetcher,
        IDbProvider provider,
        IUrlCanonicalizer canonicalizer,
        IRealtimeHub hub,
        ListModeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(canonicalizer);
        ArgumentNullException.ThrowIfNull(hub);

        _fetcher = fetcher;
        _provider = provider;
        _canonicalizer = canonicalizer;
        _hub = hub;
        _options = options ?? ListModeOptions.Default;
        _hostMapper = new HostMapper(_options.HostMappings);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RedirectMatrixRowResult>> ValidateAsync(
        long auditId,
        IReadOnlyList<RedirectMappingDto> mappings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        cancellationToken.ThrowIfCancellationRequested();

        var ordered = mappings.OrderBy(m => m.Ordinal).ToList();
        var results = new List<RedirectMatrixRowResult>(ordered.Count);
        var finalStatuses = new List<int>(ordered.Count);
        var transportErrors = new List<string?>(ordered.Count);

        foreach (var mapping in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var walk = await WalkChainAsync(mapping.SourceUrl, cancellationToken).ConfigureAwait(false);
            var canonical = walk.Final is null
                ? null
                : CanonicalLinkExtractor.Extract(walk.Final.FinalUrl, walk.Final.Headers, walk.Final.Body);
            var verdict = EvaluateVerdict(mapping, walk, canonical);

            results.Add(new RedirectMatrixRowResult(
                mapping,
                verdict,
                walk.Final?.FinalUrl,
                walk.Chain.Count,
                walk.Chain,
                canonical));
            finalStatuses.Add(walk.Final?.StatusCode ?? 0);
            transportErrors.Add(walk.TransportError);
        }

        await PersistResultsAsync(auditId, results, finalStatuses, transportErrors, cancellationToken).ConfigureAwait(false);
        await RedirectMatrixResults.RefreshAggregatesAsync(_provider, auditId, results, cancellationToken).ConfigureAwait(false);
        await EmitBatchesAsync(auditId, results, cancellationToken).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Evaluates the five-tier verdict precedence of §10.3 (first failure wins):
    /// chain-exceeded → final-non-200 → target-mismatch → canonical-mismatch → pass.
    /// </summary>
    private RedirectMatrixVerdict EvaluateVerdict(RedirectMappingDto mapping, ChainWalk walk, string? canonical)
    {
        // Tier 1: hop ceiling exceeded or loop detected.
        if (walk.Exceeded)
        {
            return RedirectMatrixVerdict.ChainExceeded;
        }

        // Tier 2: final status outside 2xx (transport failures carry status 0).
        if (walk.Final is null || walk.Final.StatusCode < 200 || walk.Final.StatusCode >= 300)
        {
            return RedirectMatrixVerdict.FinalNon200;
        }

        // Tier 3: final destination differs from the expected target.
        if (!TargetsEqual(walk.Final.FinalUrl, mapping.ExpectedTargetUrl))
        {
            return RedirectMatrixVerdict.TargetMismatch;
        }

        // Tier 4: the final page declares a canonical differing from the expected target.
        if (canonical is not null && !TargetsEqual(canonical, mapping.ExpectedTargetUrl))
        {
            return RedirectMatrixVerdict.CanonicalMismatch;
        }

        // Tier 5: all checks satisfied.
        return RedirectMatrixVerdict.Pass;
    }

    private bool TargetsEqual(string observedUrl, string expectedUrl)
    {
        var observedCanonical = _canonicalizer.Canonicalize(observedUrl);
        var expectedCanonical = _canonicalizer.Canonicalize(expectedUrl);

        return _options.Strictness switch
        {
            MatrixComparisonStrictness.Normalized => _hostMapper.NormalizedEquals(observedCanonical, expectedCanonical),
            _ => string.Equals(observedCanonical, expectedCanonical, StringComparison.Ordinal),
        };
    }

    private async Task<ChainWalk> WalkChainAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        var chain = new List<RedirectHop>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { sourceUrl };
        var current = sourceUrl;
        HttpFetchResult? final = null;
        var exceeded = false;
        string? transportError = null;
        var hopLimit = Math.Max(1, _options.RedirectHopLimit);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpFetchResult result;
            try
            {
                result = await _fetcher.FetchAsync(
                    new HttpFetchRequest(current, FollowRedirects: false),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transport failure: no final response was observed; the row
                // verdicts final-non-200 with the failure retained as evidence.
                transportError = ex.Message;
                break;
            }

            if (RedirectStatusCodes.Contains(result.StatusCode) &&
                TryGetLocation(result.Headers, out var location) &&
                Uri.TryCreate(new Uri(current, UriKind.Absolute), location, out var targetUri))
            {
                var target = targetUri.ToString();
                chain.Add(new RedirectHop(chain.Count, current, target, result.StatusCode));

                if (chain.Count > hopLimit || !seen.Add(target))
                {
                    exceeded = true;
                    final = result;
                    break;
                }

                current = target;
                continue;
            }

            final = result;
            break;
        }

        return new ChainWalk(chain, final, exceeded, transportError);
    }

    private static bool TryGetLocation(IReadOnlyDictionary<string, string> headers, out string location)
    {
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Location", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                location = value;
                return true;
            }
        }

        location = string.Empty;
        return false;
    }

    private async Task PersistResultsAsync(
        long auditId,
        List<RedirectMatrixRowResult> results,
        List<int> finalStatuses,
        List<string?> transportErrors,
        CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        // D4 (WP-06-01 reopen): replace the prior matrix finding set ONCE per
        // validation pass — re-validation never duplicates findings, and the
        // whole-pass delete keeps every row of the current pass intact.
        await RedirectMatrixResults.DeleteFindingsAsync(connection, transaction, auditId, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var canonicalSource = _canonicalizer.Canonicalize(result.Mapping.SourceUrl);
            var urlId = await RedirectMatrixResults.FindUrlIdAsync(connection, auditId, canonicalSource, cancellationToken).ConfigureAwait(false);
            await RedirectMatrixResults.PersistFindingAsync(
                connection, transaction, auditId, result, urlId, finalStatuses[i], transportErrors[i], cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private async Task EmitBatchesAsync(long auditId, List<RedirectMatrixRowResult> results, CancellationToken cancellationToken)
    {
        // §10.8: batches of (URL identifier, source URL, verdict), throttled at
        // most once per second. The throttle metadata rides on every event so
        // the hub coalesces rapid batches per audit.
        for (var offset = 0; offset < results.Count; offset += MatrixHubContract.BatchSize)
        {
            var batch = results.Skip(offset).Take(MatrixHubContract.BatchSize).ToList();
            var items = new List<MatrixRowValidatedItem>(batch.Count);

            await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var result in batch)
            {
                var canonicalSource = _canonicalizer.Canonicalize(result.Mapping.SourceUrl);
                var urlId = await RedirectMatrixResults.FindUrlIdAsync(connection, auditId, canonicalSource, cancellationToken).ConfigureAwait(false);

                // G-MATRIX-VERDICT component-side fix: the frozen frontend entry
                // (events.ts MatrixRowValidatedEntry) requires string EntityId
                // urlId and a string verdict; the verdict vocabulary is the
                // §10.3/§10.6 dimension name, shared with the metric aggregates
                // and the REST results surface. Absent URL records map to the
                // empty string, matching the REST mapper.
                items.Add(new MatrixRowValidatedItem(
                    urlId > 0 ? urlId.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    result.Mapping.SourceUrl,
                    MatrixVerdictNames.Of(result.Verdict)));
            }

            var payload = new MatrixRowValidatedPayload(auditId.ToString(CultureInfo.InvariantCulture), items);
            await _hub.PublishAsync(
                new HubEvent(
                    MatrixHubContract.EventName,
                    JsonSerializer.Serialize(payload, JsonOptions),
                    MatrixHubContract.ThrottleKey(auditId),
                    MatrixHubContract.MaxEmissionInterval),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record ChainWalk(
        IReadOnlyList<RedirectHop> Chain,
        HttpFetchResult? Final,
        bool Exceeded,
        string? TransportError);
}
