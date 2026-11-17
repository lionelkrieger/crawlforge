using System.Text.Json;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Enums;
using CrawlForge.Intelligence.MarTech;
using Xunit;

namespace CrawlForge.Intelligence.Tests.MarTech;

/// <summary>
/// dataLayer inspector unit tests (FS-CRAWL-002 §14.2/§14.3): bounded event
/// capture, JS-literal tolerance, malformed payloads, optional user schema
/// validation, and the rendering-off degradation notice.
/// </summary>
public sealed class DataLayerInspectorTests
{
    private static DataLayerInspector CreateInspector(MarTechOptions? options = null) =>
        new(new ElementInventoryExtractor(), options);

    private static Task<Contracts.Intelligence.DataLayerInspectionResult> Inspect(
        string html,
        MarTechOptions? options = null,
        RenderMode mode = RenderMode.Always,
        Contracts.Entities.CrawlConfigurationDto? configuration = null)
    {
        var inspector = CreateInspector(options);
        var context = AuditContexts.Create(html: html, renderingMode: mode, configuration: configuration);
        return inspector.InspectAsync(context);
    }

    [Fact]
    public async Task WellFormedJsonPushes_AreValid_WithEventNames()
    {
        var result = await Inspect("""
            <html><body><script>
            dataLayer.push({"event": "page_view", "page": "/home"});
            dataLayer.push({"event": "purchase", "value": 42.5});
            </script></body></html>
            """);

        Assert.True(result.IsWellFormed);
        Assert.Empty(result.Issues);
        Assert.Empty(result.Findings);
        Assert.NotNull(result.SnapshotJson);
        using var snapshot = JsonDocument.Parse(result.SnapshotJson!);
        Assert.Equal(2, snapshot.RootElement.GetProperty("eventCount").GetInt32());
        Assert.Contains("page_view", result.SnapshotJson);
        Assert.Contains("purchase", result.SnapshotJson);
    }

    [Fact]
    public async Task JsLiteralConventions_AreTolerated()
    {
        var result = await Inspect("""
            <html><body><script>
            dataLayer.push({event: 'purchase', value: 42, items: ['a', 'b'],});
            dataLayer.push({event: 'callback', eventCallback: function() { return 1; }});
            </script></body></html>
            """);

        Assert.True(result.IsWellFormed);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task UnbalancedPush_EmitsMalformedWarning()
    {
        var result = await Inspect("""
            <html><body><script>
            dataLayer.push({event: 'broken'
            </script></body></html>
            """);

        Assert.False(result.IsWellFormed);
        Assert.NotEmpty(result.Issues);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(MarTechRuleKeys.DataLayerMalformed, finding.RuleKey);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(AuditCategory.AnalyticsTagging, finding.Category);
    }

    [Fact]
    public async Task UserSchema_RequiredKeyMissing_EmitsWarning()
    {
        var configuration = AuditContexts.Configuration(
            renderingMode: RenderMode.Always,
            dataLayerSchemaJson: "{\"required\":[\"event\"]}");
        var result = await Inspect("""
            <html><body><script>dataLayer.push({"page": "/home"});</script></body></html>
            """, configuration: configuration);

        Assert.False(result.IsWellFormed);
        Assert.Contains(result.Issues, issue => issue.Contains("required key 'event'"));
        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task UserSchema_TypeMismatch_EmitsWarning()
    {
        var configuration = AuditContexts.Configuration(
            renderingMode: RenderMode.Always,
            dataLayerSchemaJson: "{\"properties\":{\"value\":{\"type\":\"number\"}}}");
        var result = await Inspect("""
            <html><body><script>dataLayer.push({"event":"x","value":"not-a-number"});</script></body></html>
            """, configuration: configuration);

        Assert.False(result.IsWellFormed);
        Assert.Contains(result.Issues, issue => issue.Contains("should be number"));
    }

    [Fact]
    public async Task UserSchema_Satisfied_NoFindings()
    {
        var configuration = AuditContexts.Configuration(
            renderingMode: RenderMode.Always,
            dataLayerSchemaJson: "{\"required\":[\"event\"],\"properties\":{\"value\":{\"type\":\"number\"}}}");
        var result = await Inspect("""
            <html><body><script>dataLayer.push({"event":"purchase","value":10});</script></body></html>
            """, configuration: configuration);

        Assert.True(result.IsWellFormed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task InvalidUserSchema_IsReportedNotFatal()
    {
        var configuration = AuditContexts.Configuration(
            renderingMode: RenderMode.Always,
            dataLayerSchemaJson: "this is not json");
        var result = await Inspect("""
            <html><body><script>dataLayer.push({"event":"x"});</script></body></html>
            """, configuration: configuration);

        Assert.Contains(result.Issues, issue => issue.Contains("schema"));
    }

    [Fact]
    public async Task EventBound_TruncatesCapture_AndCountsAllPushes()
    {
        var pushes = string.Join("\n", Enumerable.Range(0, 8)
            .Select(i => $"dataLayer.push({{\"event\": \"e{i}\"}});"));
        var result = await Inspect(
            $"<html><body><script>{pushes}</script></body></html>",
            options: new MarTechOptions { DataLayerEventBound = 3 });

        Assert.True(result.IsWellFormed);
        using var snapshot = JsonDocument.Parse(result.SnapshotJson!);
        Assert.Equal(8, snapshot.RootElement.GetProperty("pushCount").GetInt32());
        Assert.Equal(3, snapshot.RootElement.GetProperty("events").GetArrayLength());
        Assert.True(snapshot.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task ConsentArrayPush_IsObservedAsCommand()
    {
        var result = await Inspect("""
            <html><body><script>
            dataLayer.push(['consent', 'default', {ad_storage: 'denied'}]);
            </script></body></html>
            """);

        Assert.True(result.IsWellFormed);
        Assert.Contains("consent", result.SnapshotJson);
    }

    [Fact]
    public async Task RenderingOff_EmitsExplicitNotice_AndSkipsInspection()
    {
        var result = await Inspect("""
            <html><body><script>dataLayer.push({"event":"x"});</script></body></html>
            """, mode: RenderMode.None);

        Assert.Null(result.SnapshotJson);
        Assert.True(result.IsWellFormed);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(MarTechRuleKeys.RequiresRendering, finding.RuleKey);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
        Assert.Contains("datalayer-inspection", finding.EvidenceJson);
    }

    [Fact]
    public async Task CategoryDisabled_ProducesNothing()
    {
        var result = await Inspect(
            """<html><body><script>dataLayer.push({"event":"x"});</script></body></html>""",
            options: new MarTechOptions { Enabled = false });
        Assert.Null(result.SnapshotJson);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task NoDataLayer_ProducesEmptyWellFormedSnapshot()
    {
        var result = await Inspect("<html><body><p>no scripts</p></body></html>");
        Assert.True(result.IsWellFormed);
        Assert.NotNull(result.SnapshotJson);
        using var snapshot = JsonDocument.Parse(result.SnapshotJson!);
        Assert.Equal(0, snapshot.RootElement.GetProperty("pushCount").GetInt32());
    }
}
