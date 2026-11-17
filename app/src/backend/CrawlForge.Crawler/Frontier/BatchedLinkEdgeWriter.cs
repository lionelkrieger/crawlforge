using System.Data.Common;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Frontier;

/// <summary>
/// One link discovered during parsing, emitted toward <c>link_edges</c>
/// (FS-CRAWL-001 §5.8). Internal/external classification, anchor text, and relation
/// flags travel with the edge; URL identifiers are resolved at persistence time.
/// </summary>
/// <param name="SourceCanonicalUrl">Canonical URL of the declaring document.</param>
/// <param name="TargetCanonicalUrl">Canonical URL of the link target.</param>
/// <param name="AnchorText">Anchor text, when present.</param>
/// <param name="RelationFlags">Observed relation flags (nofollow, sponsored, ugc…).</param>
/// <param name="IsInternal">Whether the target is internal to the crawl scope.</param>
public sealed record DiscoveredLink(
    string SourceCanonicalUrl,
    string TargetCanonicalUrl,
    string? AnchorText,
    LinkRelationFlags RelationFlags,
    bool IsInternal);

/// <summary>
/// Link discovery emission contract: parsers emit discovered links here; the sink
/// owns batching and persistence. Implementations must stay bounded-memory.
/// </summary>
public interface ILinkDiscoverySink
{
    /// <summary>Emits one discovered link (buffered until the batch threshold).</summary>
    /// <param name="link">The discovered link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the link is accepted.</returns>
    ValueTask EmitAsync(DiscoveredLink link, CancellationToken cancellationToken = default);

    /// <summary>Flushes any buffered links to storage.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the flush is durable.</returns>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Storage-during-crawl batching for link edges (FS-CRAWL-001 §5.8): discovered
/// links accumulate in a bounded buffer and are written in transactional batches
/// through provider-agnostic parameterized SQL over the locked
/// <see cref="IDbProvider"/> contract — never the full dataset in memory
/// (precedence rule 7). External targets carry a null URL reference; internal
/// targets resolve through the injected canonical-URL→identifier lookup.
/// </summary>
public sealed class BatchedLinkEdgeWriter : ILinkDiscoverySink, IAsyncDisposable
{
    private readonly IDbProvider _provider;
    private readonly long _auditId;
    private readonly Func<string, long?> _urlIdResolver;
    private readonly int _batchSize;
    private readonly List<PendingEdge> _buffer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the batched writer.</summary>
    /// <param name="provider">The configured database provider.</param>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="urlIdResolver">Resolves canonical URLs to url_records identifiers (null when unknown).</param>
    /// <param name="batchSize">Maximum buffered edges per transactional batch.</param>
    public BatchedLinkEdgeWriter(IDbProvider provider, long auditId, Func<string, long?> urlIdResolver, int batchSize = 500)
    {
        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "The batch size must be positive.");
        }

        _provider = provider;
        _auditId = auditId;
        _urlIdResolver = urlIdResolver;
        _batchSize = batchSize;
        _buffer = new List<PendingEdge>(batchSize);
    }

    /// <summary>Total edges durably written so far.</summary>
    public long EdgesWritten { get; private set; }

    /// <inheritdoc />
    public async ValueTask EmitAsync(DiscoveredLink link, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sourceId = _urlIdResolver(link.SourceCanonicalUrl)
                ?? throw new InvalidOperationException($"Source URL '{link.SourceCanonicalUrl}' has no url_records row.");
            long? targetId = link.IsInternal ? _urlIdResolver(link.TargetCanonicalUrl) : null;

            _buffer.Add(new PendingEdge(sourceId, link.TargetCanonicalUrl, targetId, link.AnchorText, (long)link.RelationFlags, link.IsInternal));
            if (_buffer.Count >= _batchSize)
            {
                await FlushCoreAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await FlushCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var edge in _buffer)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO link_edges (audit_id, source_url_id, canonical_target_url, target_url_id, anchor_text, relation_flags, is_internal) " +
                "VALUES (@audit, @source, @targetUrl, @targetId, @anchor, @flags, @internal)";
            AddParameter(command, "@audit", _auditId);
            AddParameter(command, "@source", edge.SourceUrlId);
            AddParameter(command, "@targetUrl", edge.CanonicalTargetUrl);
            AddParameter(command, "@targetId", (object?)edge.TargetUrlId ?? DBNull.Value);
            AddParameter(command, "@anchor", (object?)edge.AnchorText ?? DBNull.Value);
            AddParameter(command, "@flags", edge.RelationFlags);
            AddParameter(command, "@internal", edge.IsInternal ? 1L : 0L);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        EdgesWritten += _buffer.Count;
        _buffer.Clear();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await FlushAsync();
        _gate.Dispose();
    }

    private sealed record PendingEdge(
        long SourceUrlId,
        string CanonicalTargetUrl,
        long? TargetUrlId,
        string? AnchorText,
        long RelationFlags,
        bool IsInternal);
}
