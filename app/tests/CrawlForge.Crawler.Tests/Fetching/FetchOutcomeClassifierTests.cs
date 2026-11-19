using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Fetching;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Fetch outcome classification (probe-2 defect G27): redirect sources are
/// classified 3xx with their chain, never the terminal 200; loop/cap
/// violations keep the observed prefix; challenge semantics unchanged for
/// non-redirect responses.
/// </summary>
public sealed class FetchOutcomeClassifierTests
{
    private const string Original = "http://site.example/a";
    private const string Middle = "http://site.example/b";
    private const string Final = "http://site.example/c";

    private static HttpFetchResult Result(int status, string finalUrl) =>
        new(finalUrl, status, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero);

    private static RedirectHop[] Chain() => new[]
    {
        new RedirectHop(0, Original, Middle, 301),
        new RedirectHop(1, Middle, Final, 302),
    };

    [Fact]
    public void Classify_RedirectSource_Is3xxWithChainNotTerminal200()
    {
        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.Classify(Original, Result(200, Final), Chain());

        Assert.Equal(CrawlStatusClass.Redirect, outcome.StatusClass);
        Assert.Equal(301, outcome.SourceStatusCode);
        Assert.Equal(Middle, outcome.RedirectTargetUrl);
        Assert.Equal(Final, outcome.FinalUrl);
        Assert.Equal(200, outcome.FinalStatusCode);
        Assert.True(outcome.IsRedirectSource);
        Assert.Equal(2, outcome.Chain.Count);
    }

    [Theory]
    [InlineData(200, CrawlStatusClass.Success)]
    [InlineData(404, CrawlStatusClass.ClientError)]
    [InlineData(500, CrawlStatusClass.ServerError)]
    public void Classify_NoChain_StatusClassUnchanged(int status, CrawlStatusClass expected)
    {
        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.Classify(Original, Result(status, Original), Array.Empty<RedirectHop>());

        Assert.Equal(expected, outcome.StatusClass);
        Assert.False(outcome.IsRedirectSource);
        Assert.Equal(status, outcome.FinalStatusCode);
        Assert.Equal(status, outcome.SourceStatusCode);
    }

    [Fact]
    public void Classify_ChallengeWithoutRedirect_Blocked()
    {
        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.Classify(Original, Result(200, Original), Array.Empty<RedirectHop>(), isChallenge: true);

        Assert.Equal(CrawlStatusClass.Blocked, outcome.StatusClass);
    }

    [Fact]
    public void Classify_ChallengeWithRedirect_RedirectWins()
    {
        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.Classify(Original, Result(200, Final), Chain(), isChallenge: true);

        Assert.Equal(CrawlStatusClass.Redirect, outcome.StatusClass);
        Assert.True(outcome.IsRedirectSource);
    }

    [Fact]
    public void ClassifyFailure_RedirectLoop_KeepsObservedPrefix()
    {
        var partial = new[] { new RedirectHop(0, Original, Middle, 301) };
        var failure = new FetchFailedException(FetchFailureKind.RedirectLoop, "loop");

        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.ClassifyFailure(Original, failure, partial);

        Assert.Equal(CrawlStatusClass.Redirect, outcome.StatusClass);
        Assert.Equal(301, outcome.SourceStatusCode);
        Assert.Equal(Middle, outcome.RedirectTargetUrl);
        Assert.Single(outcome.Chain);
    }

    [Fact]
    public void ClassifyFailure_Network_FetchError()
    {
        var failure = new FetchFailedException(FetchFailureKind.Network, "connection refused");

        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.ClassifyFailure(Original, failure, Array.Empty<RedirectHop>());

        Assert.Equal(CrawlStatusClass.FetchError, outcome.StatusClass);
        Assert.False(outcome.IsRedirectSource);
        Assert.Empty(outcome.Chain);
    }

    [Fact]
    public void ClassifyViolation_Loop_RedirectWithPrefix()
    {
        var partial = new[] { new RedirectHop(0, Original, Middle, 301) };

        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.ClassifyViolation(Original, RedirectChainViolation.Loop, partial);

        Assert.Equal(CrawlStatusClass.Redirect, outcome.StatusClass);
        Assert.Equal(301, outcome.SourceStatusCode);
        Assert.Single(outcome.Chain);
    }

    [Fact]
    public void ClassifyViolation_ChainCap_RedirectWithPrefix()
    {
        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.ClassifyViolation(Original, RedirectChainViolation.ChainCapExceeded, Chain());

        Assert.Equal(CrawlStatusClass.Redirect, outcome.StatusClass);
        Assert.Equal(2, outcome.Chain.Count);
    }

    [Fact]
    public void ClassifyViolation_Null_FetchError()
    {
        ClassifiedFetchOutcome outcome = FetchOutcomeClassifier.ClassifyViolation(Original, null, Array.Empty<RedirectHop>());

        Assert.Equal(CrawlStatusClass.FetchError, outcome.StatusClass);
    }
}
