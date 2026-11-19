using System.Net;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// WP-10-A1 integration (D-021): the additive capture-mode configuration fields
/// and URL-row capture provenance round-trip over the REAL SQLite stack —
/// migration v5 columns, EntityMapping auto-map, EnumWireNames wire tokens,
/// wire records and mapper. Producer output feeds the real consumer (the
/// cross-layer conformance rule): REST create/update → repository row → REST
/// read, plus the URL explorer projection.
/// </summary>
public sealed class CaptureConfigurationRouteTests
{
    private static ConfigurationCreateRequest CreateRequest(
        string? captureMode = null,
        CaptureSettingsWire? captureSettings = null,
        RedirectCheckSettingsWire? redirectChecks = null) =>
        new(
            "Capture config",
            null,
            new[] { "https://example.com/" },
            null, null, null, null, null,
            null, null, null, null,
            null, null,
            captureMode, captureSettings, redirectChecks);

    [Fact]
    public async Task CaptureFields_RoundTrip_ThroughRealSqlite()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations",
            CreateRequest(
                captureMode: "dual",
                captureSettings: new CaptureSettingsWire(
                    AdaptiveSampleSizePerGroup: 5,
                    MaterialChangeWordCountThreshold: 0.35,
                    DualStorageWarningThresholdBytes: 2_147_483_648,
                    RenderWaitQuietMs: 750,
                    RenderWaitHardCapMs: 30_000,
                    RenderingPoolSize: 3,
                    PatternOverrides: new[]
                    {
                        new CapturePatternOverrideWire("/blog/*", "rendered"),
                        new CapturePatternOverrideWire("/assets/*", "non-rendered"),
                    }),
                redirectChecks: new RedirectCheckSettingsWire(CrossHost: true, TemporaryStatus: false, NonIndexableTarget: true, HttpsDowngrade: false)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadDataAsync<AuditConfigurationWire>();
        Assert.Equal("dual", created.CaptureMode);

        // GET round-trip: every knob and the hyphenated pattern token survive.
        var read = await (await host.Client.GetAsync($"/api/configurations/{created.Id}")).ReadDataAsync<AuditConfigurationWire>();
        Assert.Equal("dual", read.CaptureMode);
        Assert.NotNull(read.CaptureSettings);
        Assert.Equal(5, read.CaptureSettings!.AdaptiveSampleSizePerGroup);
        Assert.Equal(0.35, read.CaptureSettings.MaterialChangeWordCountThreshold);
        Assert.Equal(2_147_483_648, read.CaptureSettings.DualStorageWarningThresholdBytes);
        Assert.Equal(750, read.CaptureSettings.RenderWaitQuietMs);
        Assert.Equal(30_000, read.CaptureSettings.RenderWaitHardCapMs);
        Assert.Equal(3, read.CaptureSettings.RenderingPoolSize);
        Assert.Equal(2, read.CaptureSettings.PatternOverrides!.Count);
        Assert.Equal("rendered", read.CaptureSettings.PatternOverrides[0].Capture);
        Assert.Equal("non-rendered", read.CaptureSettings.PatternOverrides[1].Capture);
        Assert.NotNull(read.RedirectChecks);
        Assert.True(read.RedirectChecks!.CrossHost);
        Assert.False(read.RedirectChecks.TemporaryStatus);
        Assert.True(read.RedirectChecks.NonIndexableTarget);
        Assert.False(read.RedirectChecks.HttpsDowngrade);

        // The persisted DTO carries the typed documents (migration v5 columns).
        var stored = await host.Get<IReadRepository<CrawlConfigurationDto>>().GetByIdAsync(long.Parse(created.Id));
        Assert.Equal(CaptureMode.Dual, stored!.CaptureMode);
        Assert.Equal(5, stored.CaptureSettings!.AdaptiveSampleSizePerGroup);
        Assert.Equal(PatternCaptureMode.NonRendered, stored.CaptureSettings.PatternOverrides![1].Capture);
        Assert.False(stored.RedirectChecks!.TemporaryStatus);
        Assert.False(stored.RedirectChecks.HttpsDowngrade);
    }

    [Fact]
    public async Task CaptureFields_Absent_StayNullBaseline()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var created = await (await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations", CreateRequest())).ReadDataAsync<AuditConfigurationWire>();

        var read = await (await host.Client.GetAsync($"/api/configurations/{created.Id}")).ReadDataAsync<AuditConfigurationWire>();
        Assert.Null(read.CaptureMode);
        Assert.Null(read.CaptureSettings);
        Assert.Null(read.RedirectChecks);

        var stored = await host.Get<IReadRepository<CrawlConfigurationDto>>().GetByIdAsync(long.Parse(created.Id));
        Assert.Null(stored!.CaptureMode);
        Assert.Null(stored.CaptureSettings);
        Assert.Null(stored.RedirectChecks);
    }

    [Fact]
    public async Task Put_CaptureSubset_KeepsStoredDocuments_AndEmptyDocumentsTakeDefaults()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();
        var created = await (await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations",
            CreateRequest(
                captureMode: "js",
                captureSettings: new CaptureSettingsWire(AdaptiveSampleSizePerGroup: 3),
                redirectChecks: new RedirectCheckSettingsWire(HttpsDowngrade: false)))).ReadDataAsync<AuditConfigurationWire>();

        // Mode-only subset: settings and checks documents stay stored.
        var modeOnly = await host.Client.PutJsonAsync($"/api/configurations/{created.Id}",
            new ConfigurationUpdateRequest(null, null, null, null, null, null, null, null, null, null, null, null, null, null, "adaptive"));
        Assert.Equal(HttpStatusCode.OK, modeOnly.StatusCode);
        var read = await (await host.Client.GetAsync($"/api/configurations/{created.Id}")).ReadDataAsync<AuditConfigurationWire>();
        Assert.Equal("adaptive", read.CaptureMode);
        Assert.Equal(3, read.CaptureSettings!.AdaptiveSampleSizePerGroup);
        Assert.False(read.RedirectChecks!.HttpsDowngrade);

        // Empty settings document: absent knobs take the engine defaults.
        var emptySettings = await host.Client.PutJsonAsync($"/api/configurations/{created.Id}",
            new ConfigurationUpdateRequest(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, new CaptureSettingsWire()));
        Assert.Equal(HttpStatusCode.OK, emptySettings.StatusCode);
        read = await (await host.Client.GetAsync($"/api/configurations/{created.Id}")).ReadDataAsync<AuditConfigurationWire>();
        Assert.Equal(4, read.CaptureSettings!.AdaptiveSampleSizePerGroup);
        Assert.Equal(0.20, read.CaptureSettings.MaterialChangeWordCountThreshold);
        Assert.Equal(1_073_741_824, read.CaptureSettings.DualStorageWarningThresholdBytes);
        Assert.Equal(500, read.CaptureSettings.RenderWaitQuietMs);
        Assert.Equal(20_000, read.CaptureSettings.RenderWaitHardCapMs);
        Assert.Null(read.CaptureSettings.RenderingPoolSize);
        Assert.Null(read.CaptureSettings.PatternOverrides);
        // The stored checks document survived the settings-only update.
        Assert.False(read.RedirectChecks!.HttpsDowngrade);

        // Empty checks document: all four CR-029 toggles default ON.
        var emptyChecks = await host.Client.PutJsonAsync($"/api/configurations/{created.Id}",
            new ConfigurationUpdateRequest(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, new RedirectCheckSettingsWire()));
        Assert.Equal(HttpStatusCode.OK, emptyChecks.StatusCode);
        read = await (await host.Client.GetAsync($"/api/configurations/{created.Id}")).ReadDataAsync<AuditConfigurationWire>();
        Assert.True(read.RedirectChecks!.CrossHost);
        Assert.True(read.RedirectChecks.TemporaryStatus);
        Assert.True(read.RedirectChecks.NonIndexableTarget);
        Assert.True(read.RedirectChecks.HttpsDowngrade);
    }

    [Fact]
    public async Task InvalidCaptureTokens_AndKnobs_ReturnFieldErrors()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, _) = await host.SeedProjectAsync();

        var badMode = await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations", CreateRequest(captureMode: "render"));
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);

        var badPattern = await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations",
            CreateRequest(captureSettings: new CaptureSettingsWire(
                PatternOverrides: new[] { new CapturePatternOverrideWire("/blog/*", "js") })));
        Assert.Equal(HttpStatusCode.BadRequest, badPattern.StatusCode);

        var badKnob = await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations",
            CreateRequest(captureSettings: new CaptureSettingsWire(RenderWaitQuietMs: -1)));
        Assert.Equal(HttpStatusCode.BadRequest, badKnob.StatusCode);

        // The locked hyphenated tokens ARE accepted (case-insensitive binding).
        var good = await host.Client.PostJsonAsync(
            $"/api/projects/{project.Id}/configurations", CreateRequest(captureMode: "non-js"));
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        Assert.Equal("non-js", (await good.ReadDataAsync<AuditConfigurationWire>()).CaptureMode);
    }

    [Fact]
    public async Task UrlRows_CarryCaptureAndDiscoverySource()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        await host.SeedUrlAsync(audit.Id, "https://example.com/");
        var rendered = await host.SeedUrlAsync(audit.Id, "https://example.com/js-only");
        await host.Get<IWriteRepository<UrlRecordDto>>().UpdateAsync(
            rendered with { DiscoverySource = DiscoverySource.Render, CaptureSource = CaptureSource.Both });

        var items = await (await host.Client.GetAsync($"/api/audits/{audit.Id}/urls")).ReadDataAsync<List<UrlRecordSummaryWire>>();
        Assert.Equal(2, items.Count);

        var baseline = items.Single(u => u.CanonicalUrl == "https://example.com/");
        Assert.Null(baseline.CaptureSource);
        Assert.Equal("seed", baseline.DiscoverySource);

        var jsOnly = items.Single(u => u.CanonicalUrl == "https://example.com/js-only");
        Assert.Equal("both", jsOnly.CaptureSource);
        Assert.Equal("render", jsOnly.DiscoverySource);
    }
}