using System.Text;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Shingle-based near-duplicate detector (FS-CRAWL-001 §7.5): overlapping
/// token shingles over scoped visible text, Jaccard set similarity, and
/// candidate generation through an inverted shingle index so only pairs
/// sharing at least one shingle are compared. Reads stored bodies through the
/// locked content-store contracts — never re-fetches (FS-CRAWL-001 §7.2).
/// </summary>
public sealed class ShingleNearDuplicateDetector : IShingleNearDuplicateDetector
{
    private const int PageSize = 200;

    private readonly IReadRepository<ContentArtifactDto> _artifacts;
    private readonly IContentStore _contentStore;
    private readonly ITolerantDomParser _parser;
    private readonly IContentScopeResolver? _scopeResolver;
    private readonly ShingleOptions _options;

    /// <summary>Creates the detector over the locked persistence contracts.</summary>
    /// <param name="artifacts">Content-artifact index (audit-scoped enumeration).</param>
    /// <param name="contentStore">Compressed body store.</param>
    /// <param name="parser">Tolerant DOM parser for stored bodies.</param>
    /// <param name="scopeResolver">Optional scope resolver; when supplied with a scope, shingles use scoped text.</param>
    /// <param name="options">Shingle options.</param>
    public ShingleNearDuplicateDetector(
        IReadRepository<ContentArtifactDto> artifacts,
        IContentStore contentStore,
        ITolerantDomParser parser,
        IContentScopeResolver? scopeResolver = null,
        ShingleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(contentStore);
        ArgumentNullException.ThrowIfNull(parser);
        _artifacts = artifacts;
        _contentStore = contentStore;
        _parser = parser;
        _scopeResolver = scopeResolver;
        _options = options ?? ShingleOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ShingleSize, 1);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<NearDuplicatePair>> FindNearDuplicatesAsync(
        long auditId,
        double similarityThreshold,
        CancellationToken cancellationToken = default) =>
        FindNearDuplicatesAsync(auditId, similarityThreshold, scope: null, cancellationToken);

    /// <summary>
    /// Scope-aware overload: when a scope and resolver are supplied, shingles
    /// are built from the Scoped Visible Text Tree (FS-CRAWL-002 §4.3);
    /// otherwise the full visible text is the baseline.
    /// </summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="similarityThreshold">Minimum similarity to report (0–1).</param>
    /// <param name="scope">Content scope, or null for baseline full text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Near-duplicate pairs above the threshold.</returns>
    public async Task<IReadOnlyList<NearDuplicatePair>> FindNearDuplicatesAsync(
        long auditId,
        double similarityThreshold,
        ContentScopeDefinition? scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(similarityThreshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(similarityThreshold, 1.0);

        var shingleSets = new Dictionary<long, HashSet<ulong>>();
        var offset = 0;
        while (shingleSets.Count < _options.MaxUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = new ListQuery(
                Offset: offset,
                Limit: PageSize,
                Filters: new Dictionary<string, string>
                {
                    ["auditId"] = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["kind"] = ((int)ContentArtifactKind.Body).ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                SortBy: "urlId");
            var page = await _artifacts.ListAsync(query, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (var artifact in page.Items)
            {
                if (shingleSets.Count >= _options.MaxUrls)
                {
                    break;
                }

                if (shingleSets.ContainsKey(artifact.UrlId))
                {
                    continue;
                }

                var text = await ReadVisibleTextAsync(artifact, scope, cancellationToken).ConfigureAwait(false);
                var shingles = BuildShingles(text);
                if (shingles.Count > 0)
                {
                    shingleSets[artifact.UrlId] = shingles;
                }
            }

            offset += page.Items.Count;
            if (offset >= page.TotalCount)
            {
                break;
            }
        }

        return ComparePairs(shingleSets, similarityThreshold);
    }

    /// <summary>Builds the overlapping token-shingle set of a text (k-gram hashes).</summary>
    /// <param name="text">Visible text.</param>
    /// <returns>Hashed shingle set.</returns>
    public HashSet<ulong> BuildShingles(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bounded = text.Length > _options.MaxCharacters ? text[.._options.MaxCharacters] : text;
        var tokens = Tokenize(bounded);
        var shingles = new HashSet<ulong>();
        var k = _options.ShingleSize;
        if (tokens.Count < k)
        {
            if (tokens.Count > 0)
            {
                shingles.Add(HashTokens(tokens, 0, tokens.Count));
            }

            return shingles;
        }

        for (var i = 0; i <= tokens.Count - k; i++)
        {
            shingles.Add(HashTokens(tokens, i, k));
        }

        return shingles;
    }

    private async Task<string> ReadVisibleTextAsync(ContentArtifactDto artifact, ContentScopeDefinition? scope, CancellationToken cancellationToken)
    {
        await using var stream = await _contentStore.OpenReadAsync(artifact.StorageKey, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var document = _parser.Parse(html, artifact.StorageKey);
        if (scope is not null && _scopeResolver is not null)
        {
            return _scopeResolver.Resolve(document, scope).Text;
        }

        return VisibleTextExtractor.ExtractVisibleText(document);
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }

        return tokens;
    }

    private static ulong HashTokens(List<string> tokens, int start, int count)
    {
        // FNV-1a over the token run with a separator byte — deterministic and
        // collision-resistant enough for shingle set membership.
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        for (var i = start; i < start + count; i++)
        {
            foreach (var c in tokens[i])
            {
                hash = (hash ^ c) * prime;
            }

            hash = (hash ^ 0x1F) * prime;
        }

        return hash;
    }

    private static IReadOnlyList<NearDuplicatePair> ComparePairs(
        Dictionary<long, HashSet<ulong>> shingleSets,
        double threshold)
    {
        // Inverted index: shingle → URL list. Only pairs sharing a shingle can
        // have non-zero Jaccard similarity, so candidate generation is bounded
        // by actual overlap rather than all pairs.
        var index = new Dictionary<ulong, List<long>>();
        foreach (var (urlId, shingles) in shingleSets)
        {
            foreach (var shingle in shingles)
            {
                if (!index.TryGetValue(shingle, out var list))
                {
                    list = [];
                    index[shingle] = list;
                }

                list.Add(urlId);
            }
        }

        var candidates = new HashSet<(long, long)>();
        foreach (var list in index.Values)
        {
            if (list.Count < 2)
            {
                continue;
            }

            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var a = Math.Min(list[i], list[j]);
                    var b = Math.Max(list[i], list[j]);
                    candidates.Add((a, b));
                }
            }
        }

        var pairs = new List<NearDuplicatePair>();
        foreach (var (a, b) in candidates.OrderBy(p => p.Item1).ThenBy(p => p.Item2))
        {
            var setA = shingleSets[a];
            var setB = shingleSets[b];
            var intersection = 0;
            var (small, large) = setA.Count <= setB.Count ? (setA, setB) : (setB, setA);
            foreach (var shingle in small)
            {
                if (large.Contains(shingle))
                {
                    intersection++;
                }
            }

            var union = setA.Count + setB.Count - intersection;
            var similarity = union == 0 ? 0d : (double)intersection / union;
            if (similarity >= threshold)
            {
                pairs.Add(new NearDuplicatePair(a, b, similarity));
            }
        }

        return pairs;
    }
}
