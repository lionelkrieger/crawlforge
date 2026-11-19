using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// Pre-crawl validation over list entries (FS-CRAWL-001 §5.1 "surface blockers
/// before work begins"; FS-CRAWL-002 §10.2): bounded deterministic sample,
/// invalid entries skipped with counts, blockers surfaced for UI-10, probing
/// exclusively through the locked validator.
/// </summary>
public class ListPreCrawlProbeTests
{
    private sealed class ScriptedValidator : IPreCrawlValidator
    {
        private readonly Dictionary<string, PreCrawlValidationResult> _results = new(StringComparer.Ordinal);

        public List<string> Probed { get; } = [];

        public ScriptedValidator Script(string url, PreCrawlValidationResult result)
        {
            _results[url] = result;
            return this;
        }

        public Task<PreCrawlValidationResult> ValidateAsync(string startUrl, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Probed.Add(startUrl);
            return Task.FromResult(_results.TryGetValue(startUrl, out var result)
                ? result
                : new PreCrawlValidationResult(true, [], startUrl, 200));
        }
    }

    private static PreCrawlValidationResult Blocked(params string[] issues) =>
        new(false, issues, null, null);

    [Fact]
    public async Task Probe_BoundedSample_ProbesFirstNInOrder()
    {
        var urls = Enumerable.Range(0, 25).Select(i => $"https://h.example/page/{i}").ToList();
        var validator = new ScriptedValidator();
        var probe = new ListPreCrawlProbe(validator, sampleSize: 10);

        var report = await probe.ProbeAsync(urls);

        Assert.Equal(10, report.ProbedCount);
        Assert.True(report.SampleBoundReached);
        Assert.Equal(10, report.ValidCount);
        Assert.Equal(0, report.BlockedCount);
        Assert.False(report.HasBlockers);
        Assert.Equal(urls.Take(10), validator.Probed);
        Assert.Equal(urls.Take(10), report.Entries.Select(e => e.Url));
    }

    [Fact]
    public async Task Probe_Blockers_AreSurfacedWithIssues()
    {
        var validator = new ScriptedValidator()
            .Script("https://h.example/login-wall", Blocked("Redirects to a login page.", "Requires authentication."))
            .Script("https://h.example/dead", Blocked("Connection refused."));
        var probe = new ListPreCrawlProbe(validator);

        var report = await probe.ProbeAsync(["https://h.example/ok", "https://h.example/login-wall", "https://h.example/dead"]);

        Assert.True(report.HasBlockers);
        Assert.Equal(2, report.BlockedCount);
        Assert.Equal(1, report.ValidCount);
        Assert.Equal(3, report.ProbedCount);
        Assert.False(report.SampleBoundReached);
        Assert.Equal(["Redirects to a login page.", "Requires authentication."], report.Entries[1].Issues);
        Assert.Null(report.Entries[1].FinalUrl);
        Assert.Equal(200, report.Entries[0].StatusCode);
    }

    [Fact]
    public async Task Probe_InvalidEntries_AreSkippedAndCounted()
    {
        var validator = new ScriptedValidator();
        var probe = new ListPreCrawlProbe(validator);

        var report = await probe.ProbeAsync(["not a url", "https://h.example/ok", "ftp://h.example/x", "  "]);

        Assert.Equal(3, report.SkippedInvalidCount); // includes the whitespace-only entry
        Assert.Equal(1, report.ProbedCount);
        Assert.Equal(["https://h.example/ok"], validator.Probed);
    }

    [Fact]
    public async Task Probe_EmptyList_IsEmptyReport()
    {
        var report = await new ListPreCrawlProbe(new ScriptedValidator()).ProbeAsync([]);

        Assert.Empty(report.Entries);
        Assert.Equal(0, report.ProbedCount);
        Assert.False(report.HasBlockers);
        Assert.False(report.SampleBoundReached);
    }

    [Fact]
    public async Task Probe_InvalidEntries_DoNotConsumeTheSampleBound()
    {
        var urls = new List<string> { "bad-1", "bad-2" };
        urls.AddRange(Enumerable.Range(0, 12).Select(i => $"https://h.example/p/{i}"));
        var validator = new ScriptedValidator();
        var probe = new ListPreCrawlProbe(validator, sampleSize: 10);

        var report = await probe.ProbeAsync(urls);

        Assert.Equal(2, report.SkippedInvalidCount);
        Assert.Equal(10, report.ProbedCount);
        Assert.True(report.SampleBoundReached);
    }

    [Fact]
    public async Task Probe_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var probe = new ListPreCrawlProbe(new ScriptedValidator());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.ProbeAsync(["https://h.example/a"], cts.Token));
    }

    [Fact]
    public void Probe_InvalidSampleSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListPreCrawlProbe(new ScriptedValidator(), sampleSize: 0));
    }

    [Fact]
    public async Task Probe_DefaultSampleSize_IsTen()
    {
        var urls = Enumerable.Range(0, 11).Select(i => $"https://h.example/p/{i}").ToList();
        var report = await new ListPreCrawlProbe(new ScriptedValidator()).ProbeAsync(urls);

        Assert.Equal(ListPreCrawlProbe.DefaultSampleSize, report.ProbedCount);
        Assert.True(report.SampleBoundReached);
    }
}
