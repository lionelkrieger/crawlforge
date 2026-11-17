using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Crawler.Canonicalization;

namespace CrawlForge.Crawler.Frontier;

/// <summary>
/// Crawl progress counters checkpointed with the frontier (FS-CRAWL-001 §5.7:
/// queued, fetched, errors, blocked; skipped/redirected additive).
/// </summary>
/// <param name="Queued">URLs enqueued to date.</param>
/// <param name="Fetched">URLs fetched to date.</param>
/// <param name="Errors">Fetch errors to date.</param>
/// <param name="Blocked">Blocked URLs to date.</param>
/// <param name="Skipped">Skipped URLs to date.</param>
/// <param name="Redirected">Redirected URLs to date.</param>
public sealed record CrawlCounters(long Queued, long Fetched, long Errors, long Blocked, long Skipped, long Redirected)
{
    /// <summary>All-zero counters.</summary>
    public static CrawlCounters Zero { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Rehydrated resume state returned by <see cref="CrawlCheckpointManager.ResumeAsync"/>.
/// </summary>
/// <param name="Frontier">The restored frontier snapshot (already applied).</param>
/// <param name="SeenSetReference">Seen-set file reference (already rehydrated).</param>
/// <param name="Counters">Restored progress counters.</param>
/// <param name="AimdStateJson">Serialized AIMD controller state, when present (already restored).</param>
/// <param name="SavedAt">Checkpoint timestamp (UTC).</param>
public sealed record CrawlResumeState(
    FrontierSnapshot Frontier,
    string SeenSetReference,
    CrawlCounters Counters,
    string? AimdStateJson,
    DateTimeOffset SavedAt);

/// <summary>
/// Checkpoint lifecycle coordinator (FS-CRAWL-001 §5.7): periodic checkpoints
/// during runs, an explicit checkpoint on pause, and crash recovery that resumes
/// from the last durable checkpoint with frontier order, seen-set, and counters
/// preserved (criterion #6). AIMD controller state is captured/restored through
/// the locked <see cref="IAimdPolitenessController"/> contract only.
/// </summary>
public sealed class CrawlCheckpointManager : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IPriorityFrontier _frontier;
    private readonly PersistentSeenSet _seenSet;
    private readonly ICrawlCheckpointService _checkpoints;
    private readonly IAimdPolitenessController? _politeness;
    private readonly TimeSpan _periodicInterval;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private Timer? _periodicTimer;
    private long _auditId;
    private Func<CrawlCounters>? _countersAccessor;
    private int _periodicWriteInFlight;

    /// <summary>Creates the coordinator.</summary>
    /// <param name="frontier">The frontier to snapshot.</param>
    /// <param name="seenSet">The persistent seen-set to reference.</param>
    /// <param name="checkpoints">The checkpoint persistence service.</param>
    /// <param name="politeness">Optional AIMD controller whose state rides the additive payload column.</param>
    /// <param name="periodicInterval">Periodic checkpoint cadence; defaults to 30 seconds.</param>
    public CrawlCheckpointManager(
        IPriorityFrontier frontier,
        PersistentSeenSet seenSet,
        ICrawlCheckpointService checkpoints,
        IAimdPolitenessController? politeness = null,
        TimeSpan? periodicInterval = null)
    {
        _frontier = frontier;
        _seenSet = seenSet;
        _checkpoints = checkpoints;
        _politeness = politeness;
        _periodicInterval = periodicInterval ?? TimeSpan.FromSeconds(30);
        if (_periodicInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(periodicInterval), "The periodic interval must be positive.");
        }
    }

    /// <summary>The last error observed by a periodic checkpoint write, if any (diagnostics).</summary>
    public Exception? LastPeriodicError { get; private set; }

    /// <summary>Starts periodic checkpointing for an audit.</summary>
    /// <param name="auditId">The running audit.</param>
    /// <param name="countersAccessor">Reads the live counters at checkpoint time.</param>
    public void StartPeriodicCheckpoints(long auditId, Func<CrawlCounters> countersAccessor)
    {
        StopPeriodicTimer();
        _auditId = auditId;
        _countersAccessor = countersAccessor;
        _periodicTimer = new Timer(_ => _ = PeriodicWriteAsync(), null, _periodicInterval, _periodicInterval);
    }

    /// <summary>Stops periodic checkpointing and waits for any in-flight write.</summary>
    /// <returns>A task that completes when periodic writes have quiesced.</returns>
    public async Task StopPeriodicCheckpointsAsync()
    {
        StopPeriodicTimer();
        await _writeGate.WaitAsync();
        _writeGate.Release();
    }

    /// <summary>
    /// Writes the on-pause checkpoint: frontier snapshot, flushed seen-set reference,
    /// counters, and AIMD state — durable before the pause completes (§5.7).
    /// </summary>
    /// <param name="auditId">The pausing audit.</param>
    /// <param name="counters">Current progress counters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted checkpoint row.</returns>
    public Task<CrawlCheckpointDto> CheckpointOnPauseAsync(long auditId, CrawlCounters counters, CancellationToken cancellationToken = default) =>
        WriteCheckpointAsync(auditId, counters, cancellationToken);

    /// <summary>Writes a checkpoint immediately (periodic path uses the same core).</summary>
    /// <param name="auditId">The running audit.</param>
    /// <param name="counters">Current progress counters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted checkpoint row.</returns>
    public async Task<CrawlCheckpointDto> WriteCheckpointAsync(long auditId, CrawlCounters counters, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = _frontier.CreateSnapshot();
            _seenSet.Flush();

            string? aimdJson = null;
            if (_politeness is not null)
            {
                aimdJson = JsonSerializer.Serialize(_politeness.CreateCheckpointState(), JsonOptions);
            }

            var checkpoint = new CrawlCheckpointDto(
                0,
                auditId,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                _seenSet.Reference,
                JsonSerializer.Serialize(counters, JsonOptions),
                DateTimeOffset.UtcNow)
            {
                AimdStateJson = aimdJson
            };

            await _checkpoints.SaveAsync(checkpoint, cancellationToken);
            return checkpoint;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Crash recovery / resume (criterion #6): loads the latest durable checkpoint,
    /// restores frontier order, rehydrates the seen-set from its reference, restores
    /// counters and AIMD state. Returns null when the audit has no checkpoint.
    /// </summary>
    /// <param name="auditId">The audit to resume.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rehydrated resume state, or null.</returns>
    public async Task<CrawlResumeState?> ResumeAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var latest = await _checkpoints.LoadLatestAsync(auditId, cancellationToken);
        if (latest is null)
        {
            return null;
        }

        var snapshot = JsonSerializer.Deserialize<FrontierSnapshot>(latest.SerializedFrontier, JsonOptions)
            ?? throw new InvalidDataException("Checkpoint frontier payload is unreadable.");
        var counters = JsonSerializer.Deserialize<CrawlCounters>(latest.CountersJson, JsonOptions)
            ?? throw new InvalidDataException("Checkpoint counters payload is unreadable.");

        _frontier.Restore(snapshot);

        var seenReference = latest.SeenSetReference ?? _seenSet.Reference;
        if (!string.Equals(_seenSet.Reference, seenReference, StringComparison.Ordinal))
        {
            _seenSet.RebuildFrom(seenReference);
        }

        if (latest.AimdStateJson is not null && _politeness is not null)
        {
            var aimdState = JsonSerializer.Deserialize<AimdCheckpointState>(latest.AimdStateJson, JsonOptions)
                ?? throw new InvalidDataException("Checkpoint AIMD payload is unreadable.");
            _politeness.RestoreCheckpointState(aimdState);
        }

        return new CrawlResumeState(snapshot, seenReference, counters, latest.AimdStateJson, latest.SavedAt);
    }

    private async Task PeriodicWriteAsync()
    {
        if (Interlocked.CompareExchange(ref _periodicWriteInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var accessor = _countersAccessor;
            if (accessor is not null)
            {
                await WriteCheckpointAsync(_auditId, accessor(), CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LastPeriodicError = ex;
        }
        finally
        {
            Interlocked.Exchange(ref _periodicWriteInFlight, 0);
        }
    }

    private void StopPeriodicTimer()
    {
        _periodicTimer?.Dispose();
        _periodicTimer = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopPeriodicCheckpointsAsync();
        _writeGate.Dispose();
    }
}
