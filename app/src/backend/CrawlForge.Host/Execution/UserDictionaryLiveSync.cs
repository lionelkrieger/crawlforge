using CrawlForge.Audit.ContentQuality;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Host.Hosting;

namespace CrawlForge.Host.Execution;

/// <summary>
/// D13 live wiring (criterion #13 suppression facet): feeds the persisted
/// <c>user_dictionary</c> entries into the singleton <see cref="ContentQualityAnalyzer"/>
/// the Host actually uses. The analyzer's constructor snapshot alone never saw
/// REST-persisted entries; this sync runs at startup (after migrations) and
/// after every dictionary mutation, so accepted spellings suppress spelling
/// findings live — in full audits, offline re-audits, and single-page analysis
/// alike (all share the registered analyzer instance).
/// </summary>
public sealed class UserDictionaryLiveSync
{
    private readonly ContentQualityAnalyzer _analyzer;
    private readonly IReadRepository<UserDictionaryEntryDto> _reads;

    /// <summary>Creates the sync over the live analyzer and dictionary store.</summary>
    /// <param name="analyzer">The Host's registered content-quality analyzer.</param>
    /// <param name="reads">Dictionary read repository.</param>
    public UserDictionaryLiveSync(ContentQualityAnalyzer analyzer, IReadRepository<UserDictionaryEntryDto> reads)
    {
        _analyzer = analyzer;
        _reads = reads;
    }

    /// <summary>Reloads every persisted entry into the analyzer's suppression snapshot.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the snapshot is refreshed.</returns>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var page = await _reads.ListAsync(new ListQuery(0, 100_000), cancellationToken).ConfigureAwait(false);
        var words = page.Items.Select(entry => entry.Word).ToList();
        _analyzer.UpdateUserDictionary(words);
    }
}

/// <summary>
/// Write-repository decorator (composition-only, last-wins over the Core
/// registration): every dictionary mutation through the REST surface refreshes
/// the live analyzer snapshot immediately after the persisted change commits.
/// Reads and validation stay with the locked controller; no API code changes.
/// </summary>
public sealed class UserDictionaryWriteRepository : IWriteRepository<UserDictionaryEntryDto>
{
    private readonly IWriteRepository<UserDictionaryEntryDto> _inner;
    private readonly UserDictionaryLiveSync _sync;

    /// <summary>Creates the decorator over the base repository and live sync.</summary>
    /// <param name="inner">Base write repository.</param>
    /// <param name="sync">Live analyzer sync.</param>
    public UserDictionaryWriteRepository(IWriteRepository<UserDictionaryEntryDto> inner, UserDictionaryLiveSync sync)
    {
        _inner = inner;
        _sync = sync;
    }

    /// <inheritdoc />
    public async Task<UserDictionaryEntryDto> CreateAsync(UserDictionaryEntryDto entity, CancellationToken cancellationToken = default)
    {
        var created = await _inner.CreateAsync(entity, cancellationToken).ConfigureAwait(false);
        await _sync.SyncAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <inheritdoc />
    public async Task<UserDictionaryEntryDto> UpdateAsync(UserDictionaryEntryDto entity, CancellationToken cancellationToken = default)
    {
        var updated = await _inner.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
        await _sync.SyncAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var deleted = await _inner.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (deleted)
        {
            await _sync.SyncAsync(cancellationToken).ConfigureAwait(false);
        }
        return deleted;
    }
}

/// <summary>
/// Startup participant: loads persisted dictionary entries into the analyzer
/// after schema migrations (Order -100) and before any engine start, so a
/// restart preserves suppression without user action.
/// </summary>
public sealed class UserDictionaryLifecycleParticipant : IBackendLifecycle
{
    private readonly UserDictionaryLiveSync _sync;

    /// <summary>Creates the participant.</summary>
    /// <param name="sync">Live analyzer sync.</param>
    public UserDictionaryLifecycleParticipant(UserDictionaryLiveSync sync) => _sync = sync;

    /// <inheritdoc />
    public int Order => -50;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _sync.SyncAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
