using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Limits;
using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// Sample-crawl limits integration (FS-CRAWL-001 §5.1, criterion #34,
/// precedence rule 5): sample bounds become the per-audit override layer and
/// flow through the STANDARD resolver priority order — no sample-specific
/// resolution path, project/application layers untouched.
/// </summary>
public class SampleCrawlPlanTests
{
    [Fact]
    public void Plan_SampleBounds_BecomeEffectiveLimits()
    {
        var plan = SampleCrawlPlanner.Plan(new SampleCrawlSettings(MaxPages: 25, MaxDepth: 2), new UnlimitedLimitsResolver());

        Assert.Equal(25, plan.Limits.MaxUrls);
        Assert.Equal(2, plan.Limits.MaxDepth);
        Assert.Equal(25, plan.AuditOverrides.MaxUrls);
        Assert.Equal(2, plan.AuditOverrides.MaxDepth);
        Assert.Null(plan.AuditOverrides.MaxDurationSeconds);
        Assert.Null(plan.AuditOverrides.MaxConcurrency);
    }

    [Fact]
    public void Plan_AuditLayerWins_OverProjectAndApplication()
    {
        var plan = SampleCrawlPlanner.Plan(
            new SampleCrawlSettings(MaxPages: 25, MaxDepth: 2),
            new UnlimitedLimitsResolver(),
            projectDefaults: new CrawlLimitOverrides(MaxUrls: 10, MaxDepth: 1),
            applicationDefaults: new CrawlLimitOverrides(MaxUrls: 5, MaxDepth: 0));

        // Standard priority: audit overrides win (the sample bounds ARE the audit layer).
        Assert.Equal(25, plan.Limits.MaxUrls);
        Assert.Equal(2, plan.Limits.MaxDepth);
    }

    [Fact]
    public void Plan_UnsetDimensions_InheritLowerLayers()
    {
        var plan = SampleCrawlPlanner.Plan(
            new SampleCrawlSettings(MaxPages: 25, MaxDepth: 2),
            new UnlimitedLimitsResolver(),
            projectDefaults: new CrawlLimitOverrides(MaxDurationSeconds: 60, MaxConcurrency: 4),
            applicationDefaults: new CrawlLimitOverrides(MaxDurationSeconds: 300, FetchTimeoutSeconds: 15));

        // Sample bounds only set MaxUrls/MaxDepth; everything else inherits.
        Assert.Equal(60, plan.Limits.MaxDurationSeconds);
        Assert.Equal(4, plan.Limits.MaxConcurrency);
        Assert.Equal(15, plan.Limits.FetchTimeoutSeconds);
    }

    [Fact]
    public void Plan_UsesTheStandardResolver_WithExactLayers()
    {
        var recorder = new RecordingLimitsResolver();
        var project = new CrawlLimitOverrides(MaxDurationSeconds: 60);
        var application = new CrawlLimitOverrides(MaxConcurrency: 2);

        var plan = SampleCrawlPlanner.Plan(new SampleCrawlSettings(MaxPages: 7, MaxDepth: 1), recorder, project, application);

        Assert.Equal(1, recorder.Calls);
        Assert.Equal(7, recorder.LastAudit!.MaxUrls);
        Assert.Equal(1, recorder.LastAudit.MaxDepth);
        Assert.Same(project, recorder.LastProject);
        Assert.Same(application, recorder.LastApplication);
        Assert.Same(recorder.Result, plan.Limits);
    }

    [Fact]
    public void Plan_InvalidBounds_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SampleCrawlPlanner.Plan(new SampleCrawlSettings(MaxPages: 0, MaxDepth: 1), new UnlimitedLimitsResolver()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SampleCrawlPlanner.Plan(new SampleCrawlSettings(MaxPages: 1, MaxDepth: -1), new UnlimitedLimitsResolver()));
    }

    private sealed class RecordingLimitsResolver : ILimitsResolver
    {
        public ResolvedCrawlLimits Result { get; } = new(
            MaxUrls: 7, MaxDepth: 1, MaxDurationSeconds: null, MaxConcurrency: 2,
            RequestsPerSecond: double.PositiveInfinity, FetchTimeoutSeconds: 30);

        public int Calls { get; private set; }
        public CrawlLimitOverrides? LastAudit { get; private set; }
        public CrawlLimitOverrides? LastProject { get; private set; }
        public CrawlLimitOverrides? LastApplication { get; private set; }

        public ResolvedCrawlLimits Resolve(
            CrawlLimitOverrides? auditOverrides,
            CrawlLimitOverrides? projectDefaults,
            CrawlLimitOverrides? applicationDefaults)
        {
            Calls++;
            LastAudit = auditOverrides;
            LastProject = projectDefaults;
            LastApplication = applicationDefaults;
            return Result;
        }
    }
}
