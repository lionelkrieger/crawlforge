using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Tests.Fetching.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Transport-layer observability additions (lead-approved cf-wp-09-02 relay):
/// single challenge-aware AIMD sample via injected IWafChallengeEvaluator, and
/// per-hop Set-Cookie retention on recorder snapshots/payloads (cookie-aware
/// overload), including cookie-gate self-revisit tolerance.
/// </summary>
public sealed class HttpFetcherTransportSamplesTests
{
    private static HttpFetcherOptions Options() => new(maxRetryAttempts: 0, retryBackoffBaseMilliseconds: 1);

    [Fact]
    public async Task Aimd_WithEvaluator_SampleCarriesChallengeVerdictOnce()
    {
        var handler = new ScriptedHttpMessageHandler().Enqueue(ScriptedHttpMessageHandler.Response(200, "challenge page"));
        var aimd = new RecordingAimdController();
        var evaluator = new FakeWafChallengeEvaluator { IsChallenge = true };
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), aimdController: aimd, wafChallengeEvaluator: evaluator);

        await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/gate"));

        var (host, sample) = Assert.Single(aimd.Samples);
        Assert.Equal("site.example", host);
        Assert.True(sample.IsChallenge);
        Assert.Equal(200, sample.StatusCode);
        Assert.Equal("http://site.example/gate", evaluator.LastInput!.Url);
        Assert.Equal("challenge page", evaluator.LastInput.BodySnippet);
    }

    [Fact]
    public async Task Aimd_NullEvaluator_BaselineSampleUnchanged()
    {
        var handler = new ScriptedHttpMessageHandler().Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        var aimd = new RecordingAimdController();
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), aimdController: aimd);

        await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/"));

        Assert.False(Assert.Single(aimd.Samples).Sample.IsChallenge);
    }

    [Fact]
    public async Task Aimd_EvaluatorWithoutController_NotInvoked()
    {
        var handler = new ScriptedHttpMessageHandler().Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        var evaluator = new FakeWafChallengeEvaluator { IsChallenge = true };
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), wafChallengeEvaluator: evaluator);

        await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/"));

        Assert.Null(evaluator.LastInput);
    }

    [Fact]
    public async Task Aimd_BodySnippetBoundedTo4096()
    {
        var body = new string('x', 10_000);
        var handler = new ScriptedHttpMessageHandler().Enqueue(ScriptedHttpMessageHandler.Response(200, body));
        var aimd = new RecordingAimdController();
        var evaluator = new FakeWafChallengeEvaluator();
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), aimdController: aimd, wafChallengeEvaluator: evaluator);

        await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/"));

        Assert.Equal(4096, evaluator.LastInput!.BodySnippet!.Length);
    }

    [Fact]
    public async Task Aimd_RedirectChain_SingleSampleOnTerminalResponse()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(301, location: "http://site.example/final"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        var aimd = new RecordingAimdController();
        var evaluator = new FakeWafChallengeEvaluator();
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), aimdController: aimd, wafChallengeEvaluator: evaluator);

        await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/start"));

        Assert.Single(aimd.Samples);
        Assert.Equal("http://site.example/final", evaluator.LastInput!.Url);
        Assert.Equal(200, evaluator.LastInput.StatusCode);
    }

    [Fact]
    public async Task Recorder_CookieAwareOverload_SnapshotCarriesSetCookiePerOrdinal()
    {
        RedirectChainSnapshot? last = null;
        var recorder = new RedirectChainRecorder(10, (snapshot, _) =>
        {
            last = snapshot;
            return Task.CompletedTask;
        });

        await recorder.RecordHopAsync(1, 10, new RedirectHop(1, "http://site.example/b", "http://site.example/c", 301), setCookies: null);
        await recorder.RecordHopAsync(1, 10, new RedirectHop(0, "http://site.example/a", "http://site.example/b", 302), new[] { "sid=abc; Path=/" });

        Assert.NotNull(last);
        Assert.Equal(2, last!.Hops.Count);
        Assert.Equal(new[] { "sid=abc; Path=/" }, last.HopSetCookies[0]);
        Assert.False(last.HopSetCookies.ContainsKey(1));
    }

    [Fact]
    public async Task Recorder_InterfacePath_NoCookiesStrictLoops()
    {
        var recorder = new RedirectChainRecorder(10);
        IRedirectChainRecorder contract = recorder;

        await contract.RecordHopAsync(1, 11, new RedirectHop(0, "http://site.example/a", "http://site.example/b", 301));

        // Self-redirect WITHOUT cookies stays a loop on the interface path.
        await Assert.ThrowsAsync<RedirectChainException>(
            () => contract.RecordHopAsync(1, 12, new RedirectHop(0, "http://site.example/g", "http://site.example/g", 302)));
    }

    [Fact]
    public async Task Recorder_CookieSelfRevisit_ToleratedOnceThenLoop()
    {
        var recorder = new RedirectChainRecorder(10);
        var gate = new RedirectHop(0, "http://site.example/g", "http://site.example/g", 302);

        // First cookie-carrying self-redirect: a gate, not a loop.
        await recorder.RecordHopAsync(1, 13, gate, new[] { "fixture_gate=1; Path=/" });
        // Repeat self-redirect: loop again.
        await Assert.ThrowsAsync<RedirectChainException>(
            () => recorder.RecordHopAsync(1, 13, new RedirectHop(1, "http://site.example/g", "http://site.example/g", 302), new[] { "fixture_gate=1; Path=/" }));
        // Self-redirect without cookies: still a loop.
        await Assert.ThrowsAsync<RedirectChainException>(
            () => recorder.RecordHopAsync(1, 14, gate, setCookies: null));
        // Cross-URL loop with cookies: still a loop.
        await recorder.RecordHopAsync(1, 15, new RedirectHop(0, "http://site.example/a", "http://site.example/b", 301), new[] { "sid=1" });
        await Assert.ThrowsAsync<RedirectChainException>(
            () => recorder.RecordHopAsync(1, 15, new RedirectHop(1, "http://site.example/b", "http://site.example/a", 301), new[] { "sid=2" }));
    }

    [Fact]
    public async Task Fetcher_ConcreteRecorder_PersistsSetCookiePerHop()
    {
        var redirect = ScriptedHttpMessageHandler.Response(302, location: "http://site.example/step2");
        redirect.Headers.TryAddWithoutValidation("Set-Cookie", "sid=abc; Path=/");
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(redirect)
            .Enqueue(ScriptedHttpMessageHandler.Response(301, location: "http://site.example/final"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        var store = new InMemoryRedirectChainStore();
        var recorder = new RedirectChainRecorder(10, RedirectChainPersistence.CreatePersistHook(store));
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), recorder);

        FetchContextScope.Current = new FetchContext(1, 20);
        try
        {
            await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/start"));
        }
        finally
        {
            FetchContextScope.Current = null;
        }

        RedirectChainPayload? payload = await store.GetPayloadAsync(20);
        Assert.Equal(2, payload!.Hops.Count);
        Assert.Equal(new[] { "sid=abc; Path=/" }, payload.Hops[0].SetCookies);
        Assert.Null(payload.Hops[1].SetCookies);

        // JSON: setCookies present only on the cookie hop; canonical shape otherwise.
        var json = RedirectChainJson.SerializePayload(payload);
        using var document = JsonDocument.Parse(json);
        JsonElement hop0 = document.RootElement.GetProperty("hops")[0];
        Assert.Equal("sid=abc; Path=/", hop0.GetProperty("setCookies")[0].GetString());
        Assert.False(document.RootElement.GetProperty("hops")[1].TryGetProperty("setCookies", out _));
    }

    [Fact]
    public async Task Fetcher_InterfaceOnlyRecorder_UsesContractPath()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(301, location: "http://site.example/final"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        var recorder = new RecordingOnlyChainRecorder();
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), recorder);

        FetchContextScope.Current = new FetchContext(1, 21);
        try
        {
            await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/start"));
        }
        finally
        {
            FetchContextScope.Current = null;
        }

        Assert.Single(recorder.Hops);
        Assert.Equal("http://site.example/final", recorder.Hops[0].ToUrl);
    }

    [Fact]
    public async Task Fetcher_CookieGateWithRecorderAndJarHook_Returns200()
    {
        var gate = ScriptedHttpMessageHandler.Response(302, location: "http://site.example/cookie-gate/");
        gate.Headers.TryAddWithoutValidation("Set-Cookie", "fixture_gate=1; Path=/");
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(gate)
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        var store = new InMemoryRedirectChainStore();
        var recorder = new RedirectChainRecorder(10, RedirectChainPersistence.CreatePersistHook(store));
        var jar = new CookieJarFetchHook();
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), recorder, fetchHook: jar);

        FetchContextScope.Current = new FetchContext(1, 22);
        HttpFetchResult result;
        try
        {
            result = await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/cookie-gate/"));
        }
        finally
        {
            FetchContextScope.Current = null;
        }

        Assert.Equal(200, result.StatusCode);
        RedirectChainPayload? payload = await store.GetPayloadAsync(22);
        Assert.Single(payload!.Hops);
        Assert.Equal(302, payload.Hops[0].StatusCode);
        Assert.Equal(new[] { "fixture_gate=1; Path=/" }, payload.Hops[0].SetCookies);
    }

    [Fact]
    public void FromSnapshot_WithoutCookies_CanonicalHopShapeUnchanged()
    {
        var snapshot = new RedirectChainSnapshot(1, 30, new[] { new RedirectHop(0, "http://site.example/a", "http://site.example/b", 301) });

        var json = RedirectChainJson.SerializePayload(RedirectChainJson.FromSnapshot(snapshot));

        Assert.Equal("[{\"ordinal\":0,\"url\":\"http://site.example/a\",\"toUrl\":\"http://site.example/b\",\"statusCode\":301}]",
            JsonDocument.Parse(json).RootElement.GetProperty("hops").GetRawText());
    }

    private sealed class RecordingOnlyChainRecorder : IRedirectChainRecorder
    {
        public List<RedirectHop> Hops { get; } = new();

        public Task RecordHopAsync(long auditId, long urlId, RedirectHop hop, CancellationToken cancellationToken = default)
        {
            Hops.Add(hop);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RedirectHop>> GetChainAsync(long auditId, long urlId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RedirectHop>>(Hops);
    }
}
