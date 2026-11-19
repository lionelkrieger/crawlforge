using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.ListMode;
using CrawlForge.Crawler.Traps;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// Unified frontier admission composition (FS-CRAWL-001 §5.1 input modes +
/// §5.14 trap throttle): sample page/depth bounds, list-discovery policy and
/// crawler-trap throttling compose in one deterministic decision point. With
/// no components configured the gate admits everything — baseline behavior.
/// </summary>
public class CrawlAdmissionGateTests
{
    private static FrontierEntry Entry(string url, int depth = 1, DiscoverySource source = DiscoverySource.Link) =>
        new(url, new Uri(url).Host, depth, 0, source);

    [Fact]
    public void NoComponents_AdmitsEverything_BaselineBehavior()
    {
        var gate = new CrawlAdmissionGate();

        Assert.True(gate.Admit(Entry("https://h.example/a")).Admit);
        Assert.True(gate.Admit(Entry("https://h.example/b", depth: 99)).Admit);
        Assert.Equal(0, gate.FetchedCount);
    }

    [Fact]
    public void SamplePageBound_StopsAdmission_EvenForSeeds()
    {
        var gate = new CrawlAdmissionGate(sampleSettings: new SampleCrawlSettings(MaxPages: 2, MaxDepth: 10));

        Assert.True(gate.Admit(Entry("https://h.example/a", source: DiscoverySource.Seed)).Admit);
        gate.RecordFetched();
        Assert.True(gate.Admit(Entry("https://h.example/b")).Admit);
        gate.RecordFetched();

        var blocked = gate.Admit(Entry("https://h.example/c", source: DiscoverySource.Seed));
        Assert.False(blocked.Admit);
        Assert.Equal(CrawlAdmissionGate.SamplePageBoundReason, blocked.SkipReason);
        Assert.Equal(2, gate.FetchedCount);
    }

    [Fact]
    public void SampleDepthBound_StopsDeepEntries()
    {
        var gate = new CrawlAdmissionGate(sampleSettings: new SampleCrawlSettings(MaxPages: 100, MaxDepth: 1));

        Assert.True(gate.Admit(Entry("https://h.example/a", depth: 0)).Admit);
        Assert.True(gate.Admit(Entry("https://h.example/b", depth: 1)).Admit);
        var blocked = gate.Admit(Entry("https://h.example/c", depth: 2));
        Assert.False(blocked.Admit);
        Assert.Equal(CrawlAdmissionGate.SampleDepthBoundReason, blocked.SkipReason);
    }

    [Fact]
    public void ListOnly_SkipsDiscovery_ButPassesSeedsAndListEntries()
    {
        var gate = new CrawlAdmissionGate(listGate: new ListDiscoveryGate(
            new ListModePolicy(ListDiscoveryMode.ListOnly, DepthCeiling: 0, DiscoveryEnabled: false, new HashSet<string>())));

        Assert.True(gate.Admit(Entry("https://h.example/seed", source: DiscoverySource.Seed)).Admit);
        Assert.True(gate.Admit(Entry("https://h.example/list", source: DiscoverySource.List)).Admit);
        var blocked = gate.Admit(Entry("https://h.example/link", source: DiscoverySource.Link));
        Assert.False(blocked.Admit);
        Assert.Equal("discovery-disabled", blocked.SkipReason);
    }

    [Fact]
    public void ListBounded_SkipsOutOfListLinks()
    {
        var listGate = new ListDiscoveryGate(new ListModePolicy(
            ListDiscoveryMode.ListBounded,
            DepthCeiling: 5,
            DiscoveryEnabled: true,
            new HashSet<string>(StringComparer.Ordinal) { "https://h.example/allowed" }));
        var gate = new CrawlAdmissionGate(listGate: listGate);

        Assert.True(gate.Admit(Entry("https://h.example/allowed")).Admit);
        var blocked = gate.Admit(Entry("https://h.example/other"));
        Assert.False(blocked.Admit);
        Assert.Equal("out-of-list", blocked.SkipReason);
    }

    [Fact]
    public void TrapThrottle_PropagatesSkipReason()
    {
        var detector = new CrawlerTrapDetector(new CrawlerTrapOptions(NearDuplicateRunLength: 1));
        var hash = CrawlerTrapDetector.HashText("dup");
        detector.Admit("https://h.example/trap?p=1");
        detector.ObserveContent("https://h.example/trap?p=1", hash);
        detector.Admit("https://h.example/trap?p=2");
        detector.ObserveContent("https://h.example/trap?p=2", hash);

        var gate = new CrawlAdmissionGate(trapDetector: detector);

        var blocked = gate.Admit(Entry("https://h.example/trap?p=3"));
        Assert.False(blocked.Admit);
        Assert.Equal(CrawlerTrapDetector.ThrottleSkipReason, blocked.SkipReason);
        Assert.True(gate.Admit(Entry("https://other.example/page")).Admit);
    }

    [Fact]
    public void Composition_SampleBoundWins_OverListAndTrapReasons()
    {
        var detector = new CrawlerTrapDetector(new CrawlerTrapOptions(NearDuplicateRunLength: 1));
        var hash = CrawlerTrapDetector.HashText("dup");
        detector.Admit("https://h.example/trap?p=1");
        detector.ObserveContent("https://h.example/trap?p=1", hash);
        detector.Admit("https://h.example/trap?p=2");
        detector.ObserveContent("https://h.example/trap?p=2", hash);

        var gate = new CrawlAdmissionGate(
            listGate: new ListDiscoveryGate(new ListModePolicy(
                ListDiscoveryMode.ListOnly, DepthCeiling: 0, DiscoveryEnabled: false, new HashSet<string>())),
            trapDetector: detector,
            sampleSettings: new SampleCrawlSettings(MaxPages: 1, MaxDepth: 0));
        gate.RecordFetched();

        var blocked = gate.Admit(Entry("https://h.example/trap?p=3", depth: 5));
        Assert.False(blocked.Admit);
        Assert.Equal(CrawlAdmissionGate.SamplePageBoundReason, blocked.SkipReason);
    }

    [Fact]
    public void InvalidSampleSettings_ThrowAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CrawlAdmissionGate(sampleSettings: new SampleCrawlSettings(MaxPages: 0, MaxDepth: 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CrawlAdmissionGate(sampleSettings: new SampleCrawlSettings(MaxPages: 1, MaxDepth: -1)));
    }

    [Fact]
    public void RecordFetched_IsThreadSafe()
    {
        var gate = new CrawlAdmissionGate(sampleSettings: new SampleCrawlSettings(MaxPages: 10_000, MaxDepth: 10));

        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 500; i++)
            {
                gate.RecordFetched();
            }
        });

        Assert.Equal(4_000, gate.FetchedCount);
    }
}
