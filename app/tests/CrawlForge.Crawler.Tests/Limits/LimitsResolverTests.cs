using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Limits;
using Xunit;

namespace CrawlForge.Crawler.Tests.Limits;

/// <summary>
/// Criterion #4 evidence: the fixed priority chain (per-audit → project →
/// application → built-in unlimited), unlimited fresh-audit ceilings, and the
/// §4.3 validation invariants of the local limits resolver.
/// </summary>
public sealed class LimitsResolverTests
{
    [Fact]
    public void FreshAuditWithoutOverridesResolvesUnlimitedCeilings()
    {
        var resolver = new UnlimitedLimitsResolver();

        var resolved = resolver.Resolve(null, null, null);

        Assert.Null(resolved.MaxUrls);
        Assert.Null(resolved.MaxDepth);
        Assert.Null(resolved.MaxDurationSeconds);
        Assert.True(double.IsPositiveInfinity(resolved.RequestsPerSecond));
        Assert.InRange(resolved.MaxConcurrency, 1, 64);
        Assert.Equal(30, resolved.FetchTimeoutSeconds);
    }

    [Fact]
    public void FreshAuditProfileHasUnlimitedTotalAndPerDomainCeilings()
    {
        var resolver = new UnlimitedLimitsResolver();

        var profile = resolver.ResolveProfile(null, null, null);

        Assert.Null(profile.MaxUrls);
        Assert.Null(profile.PerDomainMaxUrls);
        Assert.Null(profile.MaxDepth);
        Assert.Null(profile.MaxDurationSeconds);
        Assert.True(double.IsPositiveInfinity(profile.RequestsPerSecond));
        Assert.Equal(DomainCeilingMode.RegisteredDomain, profile.DomainMode);
        Assert.Equal(ThroughputScope.PerHost, profile.ThroughputScope);
        Assert.True(profile.RetryBudget >= 0);
        Assert.True(profile.RedirectHopCeiling > 0);
        Assert.True(profile.ResponseSizeBytes > 0);
    }

    [Fact]
    public void AuditOverridesBeatProjectAndApplicationDefaults()
    {
        var resolver = new UnlimitedLimitsResolver();
        var audit = new CrawlLimitOverrides(
            MaxUrls: 10, MaxDepth: 1, MaxDurationSeconds: 60,
            MaxConcurrency: 2, RequestsPerSecond: 1.5, FetchTimeoutSeconds: 10);
        var project = new CrawlLimitOverrides(
            MaxUrls: 20, MaxDepth: 2, MaxDurationSeconds: 120,
            MaxConcurrency: 4, RequestsPerSecond: 2.5, FetchTimeoutSeconds: 20);
        var application = new CrawlLimitOverrides(
            MaxUrls: 30, MaxDepth: 3, MaxDurationSeconds: 180,
            MaxConcurrency: 8, RequestsPerSecond: 3.5, FetchTimeoutSeconds: 30);

        var resolved = resolver.Resolve(audit, project, application);

        Assert.Equal(10, resolved.MaxUrls);
        Assert.Equal(1, resolved.MaxDepth);
        Assert.Equal(60, resolved.MaxDurationSeconds);
        Assert.Equal(2, resolved.MaxConcurrency);
        Assert.Equal(1.5, resolved.RequestsPerSecond);
        Assert.Equal(10, resolved.FetchTimeoutSeconds);
    }

    [Fact]
    public void ProjectDefaultsBeatApplicationDefaults()
    {
        var resolver = new UnlimitedLimitsResolver();
        var project = new CrawlLimitOverrides(
            MaxUrls: 20, MaxDepth: 2, MaxDurationSeconds: 120,
            MaxConcurrency: 4, RequestsPerSecond: 2.5, FetchTimeoutSeconds: 20);
        var application = new CrawlLimitOverrides(
            MaxUrls: 30, MaxDepth: 3, MaxDurationSeconds: 180,
            MaxConcurrency: 8, RequestsPerSecond: 3.5, FetchTimeoutSeconds: 30);

        var resolved = resolver.Resolve(null, project, application);

        Assert.Equal(20, resolved.MaxUrls);
        Assert.Equal(2, resolved.MaxDepth);
        Assert.Equal(120, resolved.MaxDurationSeconds);
        Assert.Equal(4, resolved.MaxConcurrency);
        Assert.Equal(2.5, resolved.RequestsPerSecond);
        Assert.Equal(20, resolved.FetchTimeoutSeconds);
    }

    [Fact]
    public void ApplicationDefaultsBeatBuiltInProfile()
    {
        var resolver = new UnlimitedLimitsResolver();
        var application = new CrawlLimitOverrides(
            MaxUrls: 30, MaxDepth: 3, MaxDurationSeconds: 180,
            MaxConcurrency: 8, RequestsPerSecond: 3.5, FetchTimeoutSeconds: 45);

        var resolved = resolver.Resolve(null, null, application);

        Assert.Equal(30, resolved.MaxUrls);
        Assert.Equal(3, resolved.MaxDepth);
        Assert.Equal(180, resolved.MaxDurationSeconds);
        Assert.Equal(8, resolved.MaxConcurrency);
        Assert.Equal(3.5, resolved.RequestsPerSecond);
        Assert.Equal(45, resolved.FetchTimeoutSeconds);
    }

    [Fact]
    public void PartialOverridesInheritPerFieldAcrossLayers()
    {
        var resolver = new UnlimitedLimitsResolver();
        var audit = new CrawlLimitOverrides(MaxUrls: 5);
        var project = new CrawlLimitOverrides(MaxDepth: 7);
        var application = new CrawlLimitOverrides(FetchTimeoutSeconds: 90);

        var resolved = resolver.Resolve(audit, project, application);

        Assert.Equal(5, resolved.MaxUrls);
        Assert.Equal(7, resolved.MaxDepth);
        Assert.Equal(90, resolved.FetchTimeoutSeconds);
        Assert.Null(resolved.MaxDurationSeconds);
        Assert.True(double.IsPositiveInfinity(resolved.RequestsPerSecond));
    }

    [Fact]
    public void ExplicitUnlimitedRateOverrideIsAccepted()
    {
        var resolver = new UnlimitedLimitsResolver();
        var audit = new CrawlLimitOverrides(RequestsPerSecond: double.PositiveInfinity);

        var resolved = resolver.Resolve(audit, null, null);

        Assert.True(double.IsPositiveInfinity(resolved.RequestsPerSecond));
    }

    [Fact]
    public void ZeroDepthIsValid()
    {
        var resolver = new UnlimitedLimitsResolver();
        var audit = new CrawlLimitOverrides(MaxDepth: 0);

        var resolved = resolver.Resolve(audit, null, null);

        Assert.Equal(0, resolved.MaxDepth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTotalCeilingIsRejected(long maxUrls)
    {
        var resolver = new UnlimitedLimitsResolver();
        var audit = new CrawlLimitOverrides(MaxUrls: maxUrls);

        var error = Assert.Throws<LimitValidationException>(() => resolver.Resolve(audit, null, null));

        Assert.Equal("per-audit", error.Layer);
        Assert.Equal(nameof(CrawlLimitOverrides.MaxUrls), error.Field);
    }

    [Fact]
    public void NegativeDepthIsRejected()
    {
        var resolver = new UnlimitedLimitsResolver();
        var audit = new CrawlLimitOverrides(MaxDepth: -1);

        var error = Assert.Throws<LimitValidationException>(() => resolver.Resolve(audit, null, null));

        Assert.Equal(nameof(CrawlLimitOverrides.MaxDepth), error.Field);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveDurationIsRejected(int seconds)
    {
        var resolver = new UnlimitedLimitsResolver();
        var audit = new CrawlLimitOverrides(MaxDurationSeconds: seconds);

        var error = Assert.Throws<LimitValidationException>(() => resolver.Resolve(audit, null, null));

        Assert.Equal(nameof(CrawlLimitOverrides.MaxDurationSeconds), error.Field);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(65)]
    public void ConcurrencyOutsideSafetyRangeIsRejected(int concurrency)
    {
        var resolver = new UnlimitedLimitsResolver();
        var audit = new CrawlLimitOverrides(MaxConcurrency: concurrency);

        var error = Assert.Throws<LimitValidationException>(() => resolver.Resolve(audit, null, null));

        Assert.Equal(nameof(CrawlLimitOverrides.MaxConcurrency), error.Field);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidThroughputRateIsRejected(double rate)
    {
        var resolver = new UnlimitedLimitsResolver();
        var audit = new CrawlLimitOverrides(RequestsPerSecond: rate);

        var error = Assert.Throws<LimitValidationException>(() => resolver.Resolve(audit, null, null));

        Assert.Equal(nameof(CrawlLimitOverrides.RequestsPerSecond), error.Field);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void NonPositiveTimeoutIsRejected(int seconds)
    {
        var resolver = new UnlimitedLimitsResolver();
        var audit = new CrawlLimitOverrides(FetchTimeoutSeconds: seconds);

        var error = Assert.Throws<LimitValidationException>(() => resolver.Resolve(audit, null, null));

        Assert.Equal(nameof(CrawlLimitOverrides.FetchTimeoutSeconds), error.Field);
    }

    [Fact]
    public void InvalidProjectDefaultIsRejectedWithLayerName()
    {
        var resolver = new UnlimitedLimitsResolver();
        var project = new CrawlLimitOverrides(MaxUrls: 0);

        var error = Assert.Throws<LimitValidationException>(() => resolver.Resolve(null, project, null));

        Assert.Equal("project-default", error.Layer);
    }

    [Fact]
    public void InvalidApplicationDefaultIsRejectedWithLayerName()
    {
        var resolver = new UnlimitedLimitsResolver();
        var application = new CrawlLimitOverrides(MaxConcurrency: 0);

        var error = Assert.Throws<LimitValidationException>(() => resolver.Resolve(null, null, application));

        Assert.Equal("application-default", error.Layer);
    }

    [Fact]
    public void BuiltInConcurrencyScalesWithProcessorsWithinSafetyCap()
    {
        var capped = new UnlimitedLimitsResolver(new LimitsResolverOptions { ConcurrencySafetyCap = 2 });
        var uncapped = new UnlimitedLimitsResolver(new LimitsResolverOptions { ConcurrencySafetyCap = 10_000, ConcurrencyProcessorFactor = 3 });

        var cappedLimits = capped.Resolve(null, null, null);
        var uncappedLimits = uncapped.Resolve(null, null, null);

        Assert.InRange(cappedLimits.MaxConcurrency, 1, 2);
        Assert.Equal(Math.Clamp(Environment.ProcessorCount * 3, 1, 10_000), uncappedLimits.MaxConcurrency);
    }

    [Fact]
    public void ResolverIsPureAndSnapshotsAreStable()
    {
        var resolver = new UnlimitedLimitsResolver();
        var audit = new CrawlLimitOverrides(MaxUrls: 42);

        var first = resolver.Resolve(audit, null, null);
        var second = resolver.Resolve(audit, null, null);
        var laterChanged = resolver.Resolve(new CrawlLimitOverrides(MaxUrls: 99), null, null);

        Assert.Equal(first, second);
        Assert.Equal(42, first.MaxUrls);
        Assert.Equal(99, laterChanged.MaxUrls);
    }

    [Fact]
    public void BuiltInProfileMatchesResolvedDefaults()
    {
        var options = new LimitsResolverOptions { DefaultFetchTimeoutSeconds = 45, DefaultRetryBudget = 3 };
        var resolver = new UnlimitedLimitsResolver(options);

        var profile = resolver.ResolveProfile(null, null, null);
        var builtIn = UnlimitedLimitsResolver.BuiltInProfile(options);

        Assert.Equal(builtIn, profile);
    }

    [Fact]
    public void InvalidResolverOptionsAreRejected()
    {
        var options = new LimitsResolverOptions { ConcurrencySafetyCap = 0 };

        Assert.Throws<LimitValidationException>(() => new UnlimitedLimitsResolver(options));
    }
}
