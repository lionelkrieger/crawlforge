using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Tests.Fetching.TestSupport;

/// <summary>Records AIMD observables fed by the fetcher.</summary>
internal sealed class RecordingAimdController : IAimdPolitenessController
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HostThrottleState> _states = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Host, HostResponseSample Sample)> Samples { get; } = new();

    public ValueTask WaitAsync(string host, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public void RecordResponse(string host, HostResponseSample sample)
    {
        lock (_gate)
        {
            Samples.Add((host, sample));
        }
    }

    public HostThrottleState GetState(string host)
    {
        lock (_gate)
        {
            return GetStateCore(host);
        }
    }

    public void SetManualOverride(string host, double? requestsPerSecond)
    {
        lock (_gate)
        {
            _states[host] = GetStateCore(host) with { ManualOverrideRate = requestsPerSecond };
        }
    }

    public AimdCheckpointState CreateCheckpointState()
    {
        lock (_gate)
        {
            return new AimdCheckpointState(_states.Values.ToList());
        }
    }

    public void RestoreCheckpointState(AimdCheckpointState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _states.Clear();
            foreach (HostThrottleState hostState in state.Hosts)
            {
                _states[hostState.Host] = hostState;
            }
        }
    }

    private HostThrottleState GetStateCore(string host) =>
        _states.TryGetValue(host, out HostThrottleState? state)
            ? state
            : new HostThrottleState(host, CurrentRate: 2.0, CurrentConcurrency: 1, EwmaTtfbMilliseconds: 0, AdjustmentCount: 0, LastAdjustmentAt: null, ManualOverrideRate: null);
}

/// <summary>
/// Test double for the wave-2B intercepting proxy contract: returns a fixed
/// endpoint and records StartAsync invocations.
/// </summary>
internal sealed class FakeLoopbackInterceptingProxy : ILoopbackInterceptingProxy
{
    private readonly ProxyEndpoint _endpoint;
    private int _startCalls;

    public FakeLoopbackInterceptingProxy(ProxyEndpoint endpoint)
    {
        _endpoint = endpoint;
    }

    public int StartCalls => Volatile.Read(ref _startCalls);

    public ProxyOptions? LastOptions { get; private set; }

    public Task<ProxyEndpoint> StartAsync(ProxyOptions options, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _startCalls);
        LastOptions = options;
        return Task.FromResult(_endpoint);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ProxyCapturedRequest>> DrainCapturedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProxyCapturedRequest>>(Array.Empty<ProxyCapturedRequest>());
}

/// <summary>Test double for the WAF fingerprinting pass contract.</summary>
internal sealed class FakeWafChallengeEvaluator : IWafChallengeEvaluator
{
    public bool IsChallenge { get; set; }

    public WafProvider Provider { get; set; } = WafProvider.Cloudflare;

    public WafEvaluationInput? LastInput { get; private set; }

    public WafEvaluationResult Evaluate(WafEvaluationInput input)
    {
        LastInput = input;
        return new WafEvaluationResult(
            IsChallenge,
            Provider,
            IsChallenge ? WafChallengeClass.Interstitial : WafChallengeClass.Unknown,
            Array.Empty<string>(),
            "{}");
    }
}

/// <summary>
/// Records the override layers presented to limits resolution and merges them
/// in the fixed priority order (audit → project → application → built-in), so
/// tests can prove sample bounds flow through the standard path untouched.
/// </summary>
internal sealed class RecordingLimitsResolver : ILimitsResolver
{
    public List<(CrawlLimitOverrides? Audit, CrawlLimitOverrides? Project, CrawlLimitOverrides? Application)> Calls { get; } = new();

    public ResolvedCrawlLimits Resolve(
        CrawlLimitOverrides? auditOverrides,
        CrawlLimitOverrides? projectDefaults,
        CrawlLimitOverrides? applicationDefaults)
    {
        Calls.Add((auditOverrides, projectDefaults, applicationDefaults));
        return new ResolvedCrawlLimits(
            MaxUrls: auditOverrides?.MaxUrls ?? projectDefaults?.MaxUrls ?? applicationDefaults?.MaxUrls,
            MaxDepth: auditOverrides?.MaxDepth ?? projectDefaults?.MaxDepth ?? applicationDefaults?.MaxDepth,
            MaxDurationSeconds: auditOverrides?.MaxDurationSeconds ?? projectDefaults?.MaxDurationSeconds ?? applicationDefaults?.MaxDurationSeconds,
            MaxConcurrency: auditOverrides?.MaxConcurrency ?? projectDefaults?.MaxConcurrency ?? applicationDefaults?.MaxConcurrency ?? 4,
            RequestsPerSecond: auditOverrides?.RequestsPerSecond ?? projectDefaults?.RequestsPerSecond ?? applicationDefaults?.RequestsPerSecond ?? 2.0,
            FetchTimeoutSeconds: auditOverrides?.FetchTimeoutSeconds ?? projectDefaults?.FetchTimeoutSeconds ?? applicationDefaults?.FetchTimeoutSeconds ?? 30);
    }
}