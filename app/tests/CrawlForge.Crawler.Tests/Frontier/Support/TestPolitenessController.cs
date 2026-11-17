using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Tests.Frontier.Support;

/// <summary>
/// Builder-owned test double for the locked <see cref="IAimdPolitenessController"/>
/// contract (the production implementation belongs to the politeness workpiece).
/// Records every interaction so frontier/checkpoint tests can assert delegation.
/// </summary>
public sealed class TestPolitenessController : IAimdPolitenessController
{
    private readonly List<string> _waitedHosts = [];
    private readonly List<HostResponseSample> _samples = [];
    private readonly object _gate = new();

    /// <summary>Hosts passed to WaitAsync, in call order.</summary>
    public IReadOnlyList<string> WaitedHosts
    {
        get
        {
            lock (_gate)
            {
                return _waitedHosts.ToList();
            }
        }
    }

    /// <summary>Response samples recorded through RecordResponse.</summary>
    public IReadOnlyList<HostResponseSample> Samples
    {
        get
        {
            lock (_gate)
            {
                return _samples.ToList();
            }
        }
    }

    /// <summary>The state most recently passed to RestoreCheckpointState.</summary>
    public AimdCheckpointState? RestoredState { get; private set; }

    /// <summary>Hosts reported by CreateCheckpointState.</summary>
    public IReadOnlyList<HostThrottleState> CheckpointHosts { get; set; } = [];

    /// <summary>Optional artificial delay applied inside WaitAsync.</summary>
    public TimeSpan WaitDelay { get; set; } = TimeSpan.Zero;

    /// <inheritdoc />
    public async ValueTask WaitAsync(string host, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _waitedHosts.Add(host);
        }

        if (WaitDelay > TimeSpan.Zero)
        {
            await Task.Delay(WaitDelay, cancellationToken);
        }
    }

    /// <inheritdoc />
    public void RecordResponse(string host, HostResponseSample sample)
    {
        lock (_gate)
        {
            _samples.Add(sample);
        }
    }

    /// <inheritdoc />
    public HostThrottleState GetState(string host) =>
        new(host, CurrentRate: 2.0, CurrentConcurrency: 1, EwmaTtfbMilliseconds: 120, AdjustmentCount: 0, LastAdjustmentAt: null, ManualOverrideRate: null);

    /// <inheritdoc />
    public void SetManualOverride(string host, double? requestsPerSecond)
    {
        // Test double: overrides are not modeled.
    }

    /// <inheritdoc />
    public AimdCheckpointState CreateCheckpointState() => new(CheckpointHosts);

    /// <inheritdoc />
    public void RestoreCheckpointState(AimdCheckpointState state) => RestoredState = state;
}
