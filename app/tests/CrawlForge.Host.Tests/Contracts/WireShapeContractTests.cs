using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Host.Execution;
using Xunit;

namespace CrawlForge.Host.Tests.Contracts;

/// <summary>
/// HOTFIX-01 wire-shape conformance. Each test uses the REAL producer on one
/// side of the boundary and the REAL consumer (parser / hub adapter) on the
/// other, so a shape change in either layer breaks the build.
/// </summary>
public sealed class WireShapeContractTests
{
    private static ResolvedCrawlLimits UnlimitedLimits => new(
        MaxUrls: null,
        MaxDepth: null,
        MaxDurationSeconds: null,
        MaxConcurrency: 8,
        RequestsPerSecond: double.PositiveInfinity,
        FetchTimeoutSeconds: 60);

    [Fact]
    public void RestPolitenessPayload_IsParsedByEngineWithoutFallback()
    {
        // Producer: the API's real serializer used by configuration create/update.
        var wire = new AimdSettingsWire(2.0, 0.5, 0.5, 5, 0.2);
        var payload = ProjectWireMapper.SerializeAimd(wire);

        // Consumer: the engine's real parser.
        var parsed = AimdSettingsParser.Parse(payload);

        Assert.Equal(AimdPolitenessShape.Nested, parsed.Shape);
        Assert.Empty(parsed.Fallbacks);
        Assert.Equal(2.0, parsed.Settings.InitialRate);
        Assert.Equal(0.5, parsed.Settings.AdditiveIncrease);
        Assert.Equal(0.5, parsed.Settings.MultiplicativeDecrease);
        Assert.Equal(0.2, parsed.Settings.EwmaSmoothing);

        var options = AimdControllerOptions.FromSettings(parsed.Settings, UnlimitedLimits);
        options.Validate(); // previously threw ArgumentOutOfRangeException(InitialRate=0)
    }

    [Fact]
    public void LegacyFlatPolitenessPayload_IsStillParsed()
    {
        var payload = "{\"InitialRate\":10.0,\"AdditiveIncrease\":0.5,\"MultiplicativeDecrease\":0.5,\"EwmaSmoothing\":0.2,\"MinRate\":0.1,\"MaxConcurrency\":4}";

        var parsed = AimdSettingsParser.Parse(payload);

        Assert.Equal(AimdPolitenessShape.Flat, parsed.Shape);
        Assert.Empty(parsed.Fallbacks);
        Assert.Equal(10.0, parsed.Settings.InitialRate);
        Assert.Equal(0.1, parsed.Settings.MinRate);
        Assert.Equal(4, parsed.Settings.MaxConcurrency);
    }

    [Theory]
    [InlineData("{\"aimd\":{\"initialRateR0\":0,\"alpha\":-1,\"beta\":1.5,\"ewmaTtfbWeight\":0}}")]
    [InlineData("not json")]
    public void InvalidPolitenessPayload_FallsBackToDefaultsAndReportsDiagnostic(string payload)
    {
        var parsed = AimdSettingsParser.Parse(payload);

        Assert.NotEmpty(parsed.Fallbacks);
        Assert.Equal(AimdSettingsParser.DefaultInitialRate, parsed.Settings.InitialRate);
        Assert.Equal(AimdSettingsParser.DefaultAdditiveIncrease, parsed.Settings.AdditiveIncrease);
        Assert.Equal(AimdSettingsParser.DefaultMultiplicativeDecrease, parsed.Settings.MultiplicativeDecrease);
        Assert.Equal(AimdSettingsParser.DefaultEwmaSmoothing, parsed.Settings.EwmaSmoothing);

        var options = AimdControllerOptions.FromSettings(parsed.Settings, UnlimitedLimits);
        options.Validate(); // an invalid stored row must never fail an audit at start
    }

    [Fact]
    public void HostThrottleHubPayload_UsesFrontendContractFieldNames()
    {
        // Producer: the real hub adapter used by every audit.
        var hub = new CapturingRealtimeHub();
        var sink = new HubHostThrottleSink(hub);
        sink.Emit(new HostThrottleAdjustment(
            AuditId: 7,
            Host: "example.test",
            Trigger: "additive-increase",
            PreviousRate: 1.0,
            NewRate: 1.5,
            PreviousConcurrency: 1,
            NewConcurrency: 2,
            TtfbSampleMilliseconds: 120.5,
            RollingBaselineMilliseconds: 110.25,
            Timestamp: DateTimeOffset.UtcNow));

        // Consumer contract: contracts/hub/events.ts HostThrottlePayload.
        var emitted = Assert.Single(hub.Events);
        using var document = JsonDocument.Parse(emitted.PayloadJson);
        var root = document.RootElement;

        Assert.Equal(7, root.GetProperty("auditId").GetInt64());
        Assert.Equal("example.test", root.GetProperty("host").GetString());
        Assert.Equal(1.0, root.GetProperty("rateBefore").GetDouble());
        Assert.Equal(1.5, root.GetProperty("rateAfter").GetDouble());
        Assert.Equal(1, root.GetProperty("concurrencyBefore").GetInt32());
        Assert.Equal(2, root.GetProperty("concurrencyAfter").GetInt32());
        Assert.Equal(120.5, root.GetProperty("ttfbSampleMs").GetDouble());
        Assert.Equal(110.25, root.GetProperty("rollingBaselineRate").GetDouble());

        // Obsolete names must be gone: they left ttfbEwmaMs undefined in the renderer.
        Assert.False(root.TryGetProperty("previousRate", out _));
        Assert.False(root.TryGetProperty("newRate", out _));
        Assert.False(root.TryGetProperty("ttfbSampleMilliseconds", out _));
        Assert.False(root.TryGetProperty("rollingBaselineMilliseconds", out _));
    }

    private sealed class CapturingRealtimeHub : IRealtimeHub
    {
        public List<HubEvent> Events { get; } = [];

        public Task PublishAsync(HubEvent hubEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(hubEvent);
            return Task.CompletedTask;
        }
    }
}
