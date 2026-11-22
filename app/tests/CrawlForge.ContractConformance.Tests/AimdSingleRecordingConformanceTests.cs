using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Politeness;
using Xunit;

namespace CrawlForge.ContractConformance.Tests;

/// <summary>
/// Single-recording AIMD conformance (cf-fix-aimd): the transport layer is the
/// ONE challenge-aware recording site (HttpFetcher.RecordAimdSample — the
/// engine must stop recording its own sample). Every completed fetch feeds
/// exactly one <see cref="HostResponseSample"/> across the real
/// <see cref="IAimdPolitenessController"/> boundary, carrying the injected
/// <see cref="IWafChallengeEvaluator"/>'s verdict. A duplicate observation per
/// response (the engine/fetcher double-recording defect) collapses the rate to
/// the 0.1 rps floor on latency outliers, so the count itself is the contract.
/// </summary>
public sealed class AimdSingleRecordingConformanceTests
{
    private static HttpFetcherOptions Options() => new(maxRetryAttempts: 0, retryBackoffBaseMilliseconds: 1);

    /// <summary>
    /// N responses -> exactly N RecordResponse observations, one per response,
    /// with IsChallenge reflecting the evaluator's verdict (healthy -> false,
    /// challenge -> true).
    /// </summary>
    [Fact]
    public async Task Fetcher_RecordsExactlyOneObservationPerResponse_CarryingChallengeVerdicts()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(200, "<html>welcome</html>")
            .Enqueue(200, "<html>challenge-page</html>");
        var aimd = new RecordingAimdController();
        var evaluator = new StubWafChallengeEvaluator();
        using var fetcher = new HttpFetcher(
            Options(), new HttpClient(handler), aimdController: aimd, wafChallengeEvaluator: evaluator);

        await fetcher.FetchAsync(new HttpFetchRequest("http://site.test/ok"));
        await fetcher.FetchAsync(new HttpFetchRequest("http://site.test/gate"));

        // Exactly one observation per response (N=2 -> 2), never a duplicate.
        Assert.Equal(2, aimd.Observations.Count);

        // Producer output crossing the boundary: verdicts, host and status.
        Assert.Equal("site.test", aimd.Observations[0].Host);
        Assert.Equal("site.test", aimd.Observations[1].Host);
        Assert.Equal(200, aimd.Observations[0].Sample.StatusCode);
        Assert.Equal(200, aimd.Observations[1].Sample.StatusCode);
        Assert.False(aimd.Observations[0].Sample.IsChallenge);
        Assert.True(aimd.Observations[1].Sample.IsChallenge);
        Assert.True(aimd.Observations[0].Sample.TtfbMilliseconds >= 0);
        Assert.NotEqual(default, aimd.Observations[0].Sample.ObservedAt);

        // The evaluator observed each response exactly once (per-response verdict).
        Assert.Equal(2, evaluator.Inputs.Count);
    }

    /// <summary>
    /// Scales the count contract to a mixed verdict sequence: five fetches
    /// produce five observations in fetch order, verdict per response.
    /// </summary>
    [Fact]
    public async Task Fetcher_ManyResponses_ExactlyOneObservationEachInOrder()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(200, "<html>challenge-page</html>")
            .Enqueue(200, "<html>ok one</html>")
            .Enqueue(200, "<html>ok two</html>")
            .Enqueue(200, "<html>challenge-page</html>")
            .Enqueue(200, "<html>ok three</html>");
        var aimd = new RecordingAimdController();
        var evaluator = new StubWafChallengeEvaluator();
        using var fetcher = new HttpFetcher(
            Options(), new HttpClient(handler), aimdController: aimd, wafChallengeEvaluator: evaluator);

        for (var i = 1; i <= 5; i++)
        {
            await fetcher.FetchAsync(new HttpFetchRequest($"http://site.test/p{i}"));
        }

        Assert.Equal(5, aimd.Observations.Count);
        Assert.Equal(
            new[] { true, false, false, true, false },
            aimd.Observations.Select(o => o.Sample.IsChallenge).ToArray());
        Assert.Equal(5, evaluator.Inputs.Count);
    }

    /// <summary>
    /// Feeds the real <see cref="AimdPolitenessController"/> (wrapped, not
    /// faked): its control law is the real consumer of the producer output, so
    /// single-recording is observable there too — one challenge response
    /// produces exactly one waf-soft-block multiplicative decrease (the healthy
    /// sample emits no adjustment). A duplicate challenge observation would
    /// emit a second decrease.
    /// </summary>
    [Fact]
    public async Task Fetcher_Observations_CrossRealControllerBoundaryExactlyOncePerResponse()
    {
        var sink = new RecordingAdjustmentSink();
        var real = new AimdPolitenessController(auditId: 42, adjustmentSink: sink);
        var aimd = new RecordingAimdController(real);
        var evaluator = new StubWafChallengeEvaluator();
        var handler = new StubHttpMessageHandler()
            .Enqueue(200, "<html>ok</html>")
            .Enqueue(200, "<html>challenge-page</html>");
        using var fetcher = new HttpFetcher(
            Options(), new HttpClient(handler), aimdController: aimd, wafChallengeEvaluator: evaluator);

        await fetcher.FetchAsync(new HttpFetchRequest("http://site.test/ok"));
        await fetcher.FetchAsync(new HttpFetchRequest("http://site.test/gate"));

        // One observation per response at the boundary.
        Assert.Equal(2, aimd.Observations.Count);

        // The real controller processed exactly one challenge observation.
        var adjustment = Assert.Single(sink.Adjustments);
        Assert.Equal(HostThrottleTriggerCodes.WafSoftBlock, adjustment.Trigger);
        Assert.True(adjustment.NewRate < adjustment.PreviousRate);
    }

    /// <summary>Stub transport: scripted responses per send; no network is touched.</summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public StubHttpMessageHandler Enqueue(int statusCode, string body)
        {
            _responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage((System.Net.HttpStatusCode)statusCode)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html")
                };
                return response;
            });
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted response remaining for request " + request.RequestUri);
            }

            return Task.FromResult(_responses.Dequeue()());
        }
    }

    /// <summary>
    /// Records AIMD observations fed across the boundary; optionally wraps the
    /// real <see cref="AimdPolitenessController"/> so every observation is also
    /// consumed by the production control law.
    /// </summary>
    private sealed class RecordingAimdController : IAimdPolitenessController
    {
        private readonly AimdPolitenessController? _inner;

        public RecordingAimdController(AimdPolitenessController? inner = null) => _inner = inner;

        public List<(string Host, HostResponseSample Sample)> Observations { get; } = new();

        public ValueTask WaitAsync(string host, CancellationToken cancellationToken = default) =>
            _inner is null ? ValueTask.CompletedTask : _inner.WaitAsync(host, cancellationToken);

        public void RecordResponse(string host, HostResponseSample sample)
        {
            Observations.Add((host, sample));
            _inner?.RecordResponse(host, sample);
        }

        public HostThrottleState GetState(string host) =>
            _inner?.GetState(host) ?? new HostThrottleState(
                host, CurrentRate: 2.0, CurrentConcurrency: 1, EwmaTtfbMilliseconds: 0,
                AdjustmentCount: 0, LastAdjustmentAt: null, ManualOverrideRate: null);

        public void SetManualOverride(string host, double? requestsPerSecond) =>
            _inner?.SetManualOverride(host, requestsPerSecond);

        public AimdCheckpointState CreateCheckpointState() =>
            _inner?.CreateCheckpointState() ?? new AimdCheckpointState(Array.Empty<HostThrottleState>());

        public void RestoreCheckpointState(AimdCheckpointState state) => _inner?.RestoreCheckpointState(state);
    }

    /// <summary>WAF evaluator stub: verdict derives from the observed response body.</summary>
    private sealed class StubWafChallengeEvaluator : IWafChallengeEvaluator
    {
        public List<WafEvaluationInput> Inputs { get; } = new();

        public WafEvaluationResult Evaluate(WafEvaluationInput input)
        {
            Inputs.Add(input);
            var isChallenge = input.BodySnippet?.Contains("challenge-page", StringComparison.Ordinal) == true;
            return new WafEvaluationResult(
                isChallenge,
                WafProvider.Cloudflare,
                isChallenge ? WafChallengeClass.ManagedChallenge : WafChallengeClass.Unknown,
                Array.Empty<string>(),
                "{}");
        }
    }

    /// <summary>Collects the typed host-throttle adjustments emitted by the real controller.</summary>
    private sealed class RecordingAdjustmentSink : IHostThrottleAdjustmentSink
    {
        public List<HostThrottleAdjustment> Adjustments { get; } = new();

        public void Emit(HostThrottleAdjustment adjustment) => Adjustments.Add(adjustment);
    }
}
