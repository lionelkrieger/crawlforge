using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Mcp;

/// <summary>
/// Automation action log over the shared repositories (FS-CRAWL-001 §11.5,
/// criterion #23): every local agent invocation is recorded, user-viewable
/// through <see cref="ListAsync"/>, and purgeable through <see cref="PurgeAsync"/>.
/// Entries carry no identity dimensions (privacy invariant).
/// </summary>
public sealed class AutomationActionLog : IAutomationActionLog
{
    private readonly IReadRepository<AutomationActionLogDto> _read;
    private readonly IWriteRepository<AutomationActionLogDto> _write;

    /// <summary>Creates the log over the shared repositories.</summary>
    /// <param name="read">Read repository for automation_action_log.</param>
    /// <param name="write">Write repository for automation_action_log.</param>
    public AutomationActionLog(
        IReadRepository<AutomationActionLogDto> read,
        IWriteRepository<AutomationActionLogDto> write)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

    /// <inheritdoc/>
    public async Task LogAsync(AutomationActionEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _write.CreateAsync(new AutomationActionLogDto(
            0,
            DateTimeOffset.UtcNow,
            entry.Transport,
            entry.TargetName,
            entry.ArgumentsSummary,
            entry.Outcome), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<PagedResult<AutomationActionLogDto>> ListAsync(ListQuery query, CancellationToken cancellationToken = default) =>
        _read.ListAsync(query, cancellationToken);

    /// <inheritdoc/>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var purged = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _read.ListAsync(new ListQuery(Offset: 0, Limit: 500), cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0)
            {
                return purged;
            }

            foreach (var row in page.Items)
            {
                if (await _write.DeleteAsync(row.Id, cancellationToken).ConfigureAwait(false))
                {
                    purged++;
                }
            }
        }
    }
}
