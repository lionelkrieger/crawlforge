using System.Net.Http;
using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Tests.Fetching.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// HTTP fetcher mechanics (FS-CRAWL-001 §5.4): timeouts, retries, guards,
/// redirect following with loop/cap detection, TTFB/AIMD observables, signing
/// application, and status classification — all over a fake transport.
/// </summary>
public sealed class HttpFetcherTests
{
    [Fact]
    public async Task Fetch_Success_ReturnsStatusHeadersBodyAndTiming()
    {
        var handler = new ScriptedHttpMessageHandler();
        HttpResponseMessage response = ScriptedHttpMessageHandler.Response(200, "<html>ok</html>", "text/html");
        response.Headers.TryAddWithoutValidation("X-Test", "value");
        handler.Enqueue(response);
        using var fetcher = CreateFetcher(handler);

        HttpFetchResult result = await fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/page"));

        Assert.Equal("http://target.example.com/page", result.FinalUrl);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("<html>ok</html>", Encoding.UTF8.GetString(result.Body.Span));
        Assert.Equal("text/html; charset=utf-8", result.Headers["Content-Type"]);
        Assert.Equal("value", result.Headers["X-Test"]);
        Assert.True(result.Ttfb >= TimeSpan.Zero);
        Assert.True(result.TotalDuration >= result.Ttfb);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(200, CrawlStatusClass.Success)]
    [InlineData(404, CrawlStatusClass.ClientError)]
    [InlineData(500, CrawlStatusClass.ServerError)]
    [InlineData(503, CrawlStatusClass.ServerError)]
    public async Task Fetch_HttpStatuses_AreReturnedAndClassified(int status, CrawlStatusClass expected)
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(status, "body"));
        using var fetcher = CreateFetcher(handler);

        HttpFetchResult result = await fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/"));

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(expected, FetchStatusClassifier.Classify(result.StatusCode));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Fetch_TransientTransportFailure_RetriesWithinBudgetThenSucceeds()
    {
        var handler = new ScriptedHttpMessageHandler()
            .EnqueueException(new HttpRequestException(HttpRequestError.ConnectionError))
            .EnqueueException(new HttpRequestException(HttpRequestError.ConnectionError))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "recovered"));
        using var fetcher = CreateFetcher(handler, new HttpFetcherOptions(maxRetryAttempts: 2, retryBackoffBaseMilliseconds: 1));

        HttpFetchResult result = await fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Fetch_TransientFailure_ExhaustedBudget_ThrowsClassified()
    {
        var handler = new ScriptedHttpMessageHandler()
            .EnqueueException(new HttpRequestException(HttpRequestError.ConnectionError))
            .EnqueueException(new HttpRequestException(HttpRequestError.ConnectionError))
            .EnqueueException(new HttpRequestException(HttpRequestError.ConnectionError));
        using var fetcher = CreateFetcher(handler, new HttpFetcherOptions(maxRetryAttempts: 2, retryBackoffBaseMilliseconds: 1));

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/")));

        Assert.Equal(FetchFailureKind.Network, ex.Kind);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Fetch_NameResolutionFailure_Classified()
    {
        var handler = new ScriptedHttpMessageHandler()
            .EnqueueException(new HttpRequestException(HttpRequestError.NameResolutionError));
        using var fetcher = CreateFetcher(handler, new HttpFetcherOptions(maxRetryAttempts: 0));

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://missing.example.com/")));

        Assert.Equal(FetchFailureKind.NameResolution, ex.Kind);
    }

    [Fact]
    public async Task Fetch_TlsFailure_ClassifiedAndNotRetried()
    {
        var handler = new ScriptedHttpMessageHandler()
            .EnqueueException(new HttpRequestException(HttpRequestError.SecureConnectionError));
        using var fetcher = CreateFetcher(handler, new HttpFetcherOptions(maxRetryAttempts: 2, retryBackoffBaseMilliseconds: 1));

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("https://bad-cert.example.com/")));

        Assert.Equal(FetchFailureKind.Tls, ex.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Fetch_Timeout_Classified()
    {
        var handler = new ScriptedHttpMessageHandler { DelayMilliseconds = 5000 }
            .Enqueue(ScriptedHttpMessageHandler.Response(200));
        using var fetcher = CreateFetcher(handler, new HttpFetcherOptions(defaultTimeoutSeconds: 1, maxRetryAttempts: 0));

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://slow.example.com/")));

        Assert.Equal(FetchFailureKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Fetch_PerRequestTimeoutOverride_Applies()
    {
        var handler = new ScriptedHttpMessageHandler { DelayMilliseconds = 5000 }
            .Enqueue(ScriptedHttpMessageHandler.Response(200));
        using var fetcher = CreateFetcher(handler, new HttpFetcherOptions(defaultTimeoutSeconds: 30, maxRetryAttempts: 0));

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://slow.example.com/", TimeoutSeconds: 1)));

        Assert.Equal(FetchFailureKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Fetch_DeclaredOversize_ThrowsOversize()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, new string('x', 128)));
        using var fetcher = CreateFetcher(handler, new HttpFetcherOptions(maxResponseBytes: 64, maxRetryAttempts: 0));

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/big")));

        Assert.Equal(FetchFailureKind.Oversize, ex.Kind);
    }

    [Fact]
    public async Task Fetch_StreamedOversize_ThrowsOversize()
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[128])),
        };
        var handler = new ScriptedHttpMessageHandler().Enqueue(response);
        using var fetcher = CreateFetcher(handler, new HttpFetcherOptions(maxResponseBytes: 64, maxRetryAttempts: 0));

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/stream")));

        Assert.Equal(FetchFailureKind.Oversize, ex.Kind);
    }

    [Fact]
    public async Task Fetch_HeadRequest_ReturnsNoBody()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ignored-body"));
        using var fetcher = CreateFetcher(handler);

        HttpFetchResult result = await fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/", Method: "HEAD"));

        Assert.True(result.Body.IsEmpty);
        Assert.Equal("HEAD", handler.Requests[0].Method.Method);
    }

    [Fact]
    public async Task Fetch_RedirectChain_FollowedToFinalUrl()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(301, location: "http://target.example.com/b"))
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "/c"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "final"));
        using var fetcher = CreateFetcher(handler);

        HttpFetchResult result = await fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/a"));

        Assert.Equal("http://target.example.com/c", result.FinalUrl);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Fetch_RedirectLoop_ThrowsClassified()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "http://target.example.com/b"))
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "http://target.example.com/a"));
        using var fetcher = CreateFetcher(handler);

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/a")));

        Assert.Equal(FetchFailureKind.RedirectLoop, ex.Kind);
    }

    [Fact]
    public async Task Fetch_RedirectChainCap_ThrowsClassified()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "http://target.example.com/1"))
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "http://target.example.com/2"))
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "http://target.example.com/3"));
        using var fetcher = CreateFetcher(handler, new HttpFetcherOptions(maxRedirectHops: 2));

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/0")));

        Assert.Equal(FetchFailureKind.RedirectChainExceeded, ex.Kind);
    }

    [Fact]
    public async Task Fetch_FollowRedirectsFalse_ReturnsRedirectResponse()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "http://target.example.com/next"));
        using var fetcher = CreateFetcher(handler);

        HttpFetchResult result = await fetcher.FetchAsync(
            new HttpFetchRequest("http://target.example.com/", FollowRedirects: false));

        Assert.Equal(302, result.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Fetch_RedirectToNonHttpScheme_NotFollowed()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "ftp://target.example.com/file"));
        using var fetcher = CreateFetcher(handler);

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/")));

        Assert.Equal(FetchFailureKind.Other, ex.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Fetch_303_SwitchesPostToGet()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(303, location: "http://target.example.com/result"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "done"));
        using var fetcher = CreateFetcher(handler);

        await fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/submit", Method: "POST"));

        Assert.Equal("POST", handler.Requests[0].Method.Method);
        Assert.Equal("GET", handler.Requests[1].Method.Method);
    }

    [Fact]
    public async Task Fetch_WithFetchContext_RecordsEveryHop()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(301, location: "http://target.example.com/b"))
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "http://target.example.com/c"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "final"));
        var recorder = new RedirectChainRecorder();
        using var fetcher = CreateFetcher(handler, redirectRecorder: recorder);

        FetchContextScope.Current = new FetchContext(7, 42);
        try
        {
            await fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/a"));
        }
        finally
        {
            FetchContextScope.Current = null;
        }

        IReadOnlyList<RedirectHop> chain = await recorder.GetChainAsync(7, 42);
        Assert.Equal(2, chain.Count);
        Assert.Equal(new RedirectHop(0, "http://target.example.com/a", "http://target.example.com/b", 301), chain[0]);
        Assert.Equal(new RedirectHop(1, "http://target.example.com/b", "http://target.example.com/c", 302), chain[1]);
    }

    [Fact]
    public async Task Fetch_WithoutFetchContext_RecordsNoHops()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "http://target.example.com/b"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "final"));
        var recorder = new RedirectChainRecorder();
        using var fetcher = CreateFetcher(handler, redirectRecorder: recorder);

        await fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/a"));

        Assert.Empty(await recorder.GetChainAsync(7, 42));
    }

    [Fact]
    public async Task Fetch_FeedsAimdObservable_WithTtfbAndStatus()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        var aimd = new RecordingAimdController();
        using var fetcher = CreateFetcher(handler, aimdController: aimd);

        HttpFetchResult result = await fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/page"));

        (string host, HostResponseSample sample) = Assert.Single(aimd.Samples);
        Assert.Equal("target.example.com", host);
        Assert.Equal(200, sample.StatusCode);
        Assert.False(sample.IsChallenge);
        Assert.Equal(result.Ttfb.TotalMilliseconds, sample.TtfbMilliseconds);
        Assert.True(sample.TtfbMilliseconds >= 0);
    }

    [Fact]
    public async Task Fetch_SigningApplied_ToInScopeRequests()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        var signingHandler = new SignedRequestHandler(uri => uri.Host == "in.example.com");
        var secret = new SignedRequestSecret("key-1", "signature-value", SignedRequestHandler.StaticAlgorithm, "X-Signature");
        using var fetcher = CreateFetcher(handler, signedRequestHandler: signingHandler, signingSecret: secret);

        await fetcher.FetchAsync(new HttpFetchRequest("http://in.example.com/page"));

        Assert.Equal("signature-value", string.Join(",", handler.Requests[0].Headers.GetValues("X-Signature")));
    }

    [Fact]
    public async Task Fetch_SigningNotApplied_ToOutOfScopeRequests()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        var signingHandler = new SignedRequestHandler(uri => uri.Host == "in.example.com");
        var secret = new SignedRequestSecret("key-1", "signature-value", SignedRequestHandler.StaticAlgorithm, "X-Signature");
        using var fetcher = CreateFetcher(handler, signedRequestHandler: signingHandler, signingSecret: secret);

        await fetcher.FetchAsync(new HttpFetchRequest("http://out.example.com/page"));

        Assert.False(handler.Requests[0].Headers.Contains("X-Signature"));
    }

    [Fact]
    public async Task Fetch_CustomHeadersCookiesAndUserAgent_AreSent()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        using var fetcher = CreateFetcher(handler);
        var request = new HttpFetchRequest(
            "http://target.example.com/",
            Headers: new Dictionary<string, string> { ["Accept-Language"] = "en", ["User-Agent"] = "CustomAgent/1.0" },
            Cookies: new Dictionary<string, string> { ["session"] = "abc", ["pref"] = "dark" });

        await fetcher.FetchAsync(request);

        HttpRequestMessage sent = handler.Requests[0];
        Assert.Equal("en", string.Join(",", sent.Headers.GetValues("Accept-Language")));
        Assert.Equal("CustomAgent/1.0", string.Join(",", sent.Headers.GetValues("User-Agent")));
        string cookie = string.Join(",", sent.Headers.GetValues("Cookie"));
        Assert.Contains("session=abc", cookie, StringComparison.Ordinal);
        Assert.Contains("pref=dark", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_DefaultUserAgent_AppliedWhenAbsent()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        using var fetcher = CreateFetcher(handler, new HttpFetcherOptions(userAgent: "TestAgent/9.9"));

        await fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/"));

        Assert.Equal("TestAgent/9.9", string.Join(",", handler.Requests[0].Headers.GetValues("User-Agent")));
    }

    [Theory]
    [InlineData("ftp://target.example.com/")]
    [InlineData("file:///C:/local.txt")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public async Task Fetch_InvalidUrl_Throws(string url)
    {
        var handler = new ScriptedHttpMessageHandler();
        using var fetcher = CreateFetcher(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => fetcher.FetchAsync(new HttpFetchRequest(url)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Fetch_PreCancelledToken_PropagatesCancellation()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200));
        using var fetcher = CreateFetcher(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://target.example.com/"), cts.Token));
    }

    [Fact]
    public void Constructor_SigningSecretWithoutHandler_Throws()
    {
        var handler = new ScriptedHttpMessageHandler();
        using var client = new HttpClient(handler);
        var secret = new SignedRequestSecret("k", "s", "static", "X-Sig");

        Assert.Throws<ArgumentException>(() => new HttpFetcher(new HttpFetcherOptions(), client, signingSecret: secret));
    }

    [Fact]
    public void Options_InvalidBounds_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpFetcherOptions(defaultTimeoutSeconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpFetcherOptions(maxRetryAttempts: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpFetcherOptions(retryBackoffBaseMilliseconds: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpFetcherOptions(maxResponseBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpFetcherOptions(maxRedirectHops: -1));
        Assert.Throws<ArgumentException>(() => new HttpFetcherOptions(userAgent: " "));
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(200, false)]
    [InlineData(404, false)]
    public void StatusClassifier_IdentifiesSignatureRejections(int status, bool expected)
    {
        Assert.Equal(expected, FetchStatusClassifier.IsSignatureRejection(status));
    }

    private static HttpFetcher CreateFetcher(
        ScriptedHttpMessageHandler handler,
        HttpFetcherOptions? options = null,
        IRedirectChainRecorder? redirectRecorder = null,
        IAimdPolitenessController? aimdController = null,
        ISignedRequestHandler? signedRequestHandler = null,
        SignedRequestSecret? signingSecret = null)
    {
        var client = new HttpClient(handler);
        return new HttpFetcher(
            options ?? new HttpFetcherOptions(retryBackoffBaseMilliseconds: 1),
            client,
            redirectRecorder,
            aimdController,
            signedRequestHandler,
            signingSecret);
    }
}