using CrawlForge.Audit.Performance;

namespace CrawlForge.Audit.Tests.Accessibility;

/// <summary>
/// Configurable ILabVitalsProbe test double: scripted samples, failure
/// injection, and call counting for cache-sharing assertions.
/// </summary>
public sealed class FakeLabVitalsProbe : ILabVitalsProbe
{
    private readonly LabVitalsSample _sample;
    private readonly LabProbeException? _failure;

    /// <summary>Creates a probe returning the given sample.</summary>
    public FakeLabVitalsProbe(LabVitalsSample sample)
    {
        _sample = sample;
    }

    /// <summary>Creates a probe that always fails.</summary>
    public FakeLabVitalsProbe(LabProbeException failure)
    {
        _sample = new LabVitalsSample(null, null, null, null, null, null, null);
        _failure = failure;
    }

    /// <summary>Number of collection calls received.</summary>
    public int CallCount { get; private set; }

    /// <summary>URLs requested, in order.</summary>
    public List<string> RequestedUrls { get; } = [];

    /// <inheritdoc />
    public Task<LabVitalsSample> CollectAsync(LabVitalsRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        RequestedUrls.Add(request.Url);
        if (_failure is not null)
        {
            throw _failure;
        }

        return Task.FromResult(_sample);
    }

    /// <summary>A representative good-vitals sample with coverage and composition.</summary>
    public static LabVitalsSample GoodSample() => new(
        LcpMilliseconds: 1200,
        Cls: 0.02,
        InpProxyMilliseconds: 80,
        TbtMilliseconds: 40,
        FcpMilliseconds: 600,
        Coverage: new CodeCoverageSample(
            JsUsedBytes: 400,
            JsTotalBytes: 1000,
            CssUsedBytes: 300,
            CssTotalBytes: 500,
            Entries:
            [
                new CoverageEntry("https://target.test/app.js", "js", 400, 1000),
                new CoverageEntry("https://target.test/site.css", "css", 300, 500),
            ]),
        Composition: new PageCompositionSample(120, 8, 12, 3, 4096, 11.5));

    /// <summary>A poor-vitals sample (all bands poor).</summary>
    public static LabVitalsSample PoorSample() => new(
        LcpMilliseconds: 6000,
        Cls: 0.4,
        InpProxyMilliseconds: 900,
        TbtMilliseconds: 800,
        FcpMilliseconds: 3000,
        Coverage: null,
        Composition: null);
}
