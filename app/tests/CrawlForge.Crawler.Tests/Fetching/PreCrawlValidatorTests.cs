using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Tests.Fetching.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Pre-crawl start-URL validation (FS-CRAWL-001 §5.1): protocol, redirect,
/// TLS, and login-gate detection; WAF challenges on seeds surface as blockers
/// (FS-CRAWL-002 §2.5). Probes run through the same fetch pipeline.
/// </summary>
public sealed class PreCrawlValidatorTests
{
    [Fact]
    public async Task Validate_Reachable200Seed_IsValid()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "<html>home</html>"));
        PreCrawlValidator validator = CreateValidator(handler);

        PreCrawlValidationResult result = await validator.ValidateAsync("http://target.example.com/");

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.Equal("http://target.example.com/", result.FinalUrl);
        Assert.Equal(200, result.StatusCode);
    }

    [Theory]
    [InlineData("ftp://target.example.com/")]
    [InlineData("file:///C:/local.html")]
    [InlineData("/relative/only")]
    [InlineData("")]
    public async Task Validate_NonHttpProtocol_Rejected(string url)
    {
        var handler = new ScriptedHttpMessageHandler();
        PreCrawlValidator validator = CreateValidator(handler);

        PreCrawlValidationResult result = await validator.ValidateAsync(url);

        Assert.False(result.IsValid);
        Assert.Single(result.Issues);
        Assert.Contains("http", result.Issues[0], StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.FinalUrl);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Validate_DnsFailure_SurfacesBlocker()
    {
        var handler = new ScriptedHttpMessageHandler()
            .EnqueueException(new HttpRequestException(HttpRequestError.NameResolutionError));
        PreCrawlValidator validator = CreateValidator(handler, new HttpFetcherOptions(maxRetryAttempts: 0));

        PreCrawlValidationResult result = await validator.ValidateAsync("http://missing.example.com/");

        Assert.False(result.IsValid);
        Assert.Contains("DNS", result.Issues[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_TlsFailure_SurfacesBlocker()
    {
        var handler = new ScriptedHttpMessageHandler()
            .EnqueueException(new HttpRequestException(HttpRequestError.SecureConnectionError));
        PreCrawlValidator validator = CreateValidator(handler, new HttpFetcherOptions(maxRetryAttempts: 0));

        PreCrawlValidationResult result = await validator.ValidateAsync("https://bad-cert.example.com/");

        Assert.False(result.IsValid);
        Assert.Contains("TLS", result.Issues[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_ProbeTimeout_SurfacesBlocker()
    {
        var handler = new ScriptedHttpMessageHandler()
            .EnqueueException(new TaskCanceledException("simulated timeout"));
        PreCrawlValidator validator = CreateValidator(handler, new HttpFetcherOptions(maxRetryAttempts: 0));

        PreCrawlValidationResult result = await validator.ValidateAsync("http://slow.example.com/");

        Assert.False(result.IsValid);
        Assert.Contains("timed out", result.Issues[0], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Validate_LoginGate_SurfacesBlocker(int status)
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(status, "login required"));
        PreCrawlValidator validator = CreateValidator(handler);

        PreCrawlValidationResult result = await validator.ValidateAsync("http://target.example.com/");

        Assert.False(result.IsValid);
        Assert.Contains("Login gate", result.Issues[0], StringComparison.Ordinal);
        Assert.Equal(status, result.StatusCode);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public async Task Validate_ErrorStatus_SurfacesBlocker(int status)
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(status, "error"));
        PreCrawlValidator validator = CreateValidator(handler);

        PreCrawlValidationResult result = await validator.ValidateAsync("http://target.example.com/");

        Assert.False(result.IsValid);
        Assert.Contains($"HTTP {status}", result.Issues[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_RedirectingSeed_ReportsFinalUrlAndCrossHostIssue()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(301, location: "http://www.target.example.com/"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "home"));
        PreCrawlValidator validator = CreateValidator(handler);

        PreCrawlValidationResult result = await validator.ValidateAsync("http://target.example.com/");

        Assert.True(result.IsValid);
        Assert.Equal("http://www.target.example.com/", result.FinalUrl);
        Assert.Contains(result.Issues, i => i.Contains("different host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_HttpsDowngrade_IsBlocker()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "http://target.example.com/plain"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "plain"));
        PreCrawlValidator validator = CreateValidator(handler);

        PreCrawlValidationResult result = await validator.ValidateAsync("https://target.example.com/");

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("downgrades HTTPS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_WafChallengeOnSeed_IsBlocker()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "<html>challenge page</html>"));
        var evaluator = new FakeWafChallengeEvaluator { IsChallenge = true };
        PreCrawlValidator validator = CreateValidator(handler, challengeEvaluator: evaluator);

        PreCrawlValidationResult result = await validator.ValidateAsync("http://target.example.com/");

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("WAF challenge", StringComparison.Ordinal));
        Assert.NotNull(evaluator.LastInput);
        Assert.Equal(200, evaluator.LastInput!.StatusCode);
        Assert.Contains("challenge page", evaluator.LastInput.BodySnippet!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_CleanResponseWithEvaluator_IsValid()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "<html>clean</html>"));
        var evaluator = new FakeWafChallengeEvaluator { IsChallenge = false };
        PreCrawlValidator validator = CreateValidator(handler, challengeEvaluator: evaluator);

        PreCrawlValidationResult result = await validator.ValidateAsync("http://target.example.com/");

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Validate_RedirectLoop_SurfacesBlocker()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "http://target.example.com/b"))
            .Enqueue(ScriptedHttpMessageHandler.Response(302, location: "http://target.example.com/a"));
        PreCrawlValidator validator = CreateValidator(handler);

        PreCrawlValidationResult result = await validator.ValidateAsync("http://target.example.com/a");

        Assert.False(result.IsValid);
        Assert.Contains("loop", result.Issues[0], StringComparison.OrdinalIgnoreCase);
    }

    private static PreCrawlValidator CreateValidator(
        ScriptedHttpMessageHandler handler,
        HttpFetcherOptions? options = null,
        FakeWafChallengeEvaluator? challengeEvaluator = null)
    {
        var client = new HttpClient(handler);
        var fetcher = new HttpFetcher(options ?? new HttpFetcherOptions(retryBackoffBaseMilliseconds: 1), client);
        return new PreCrawlValidator(fetcher, challengeEvaluator);
    }
}