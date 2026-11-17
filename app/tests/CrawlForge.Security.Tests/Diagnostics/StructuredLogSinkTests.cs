using System.Text.Json;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Security;
using CrawlForge.Security.Diagnostics;
using CrawlForge.Security.Tests.TestInfrastructure;
using Xunit;

namespace CrawlForge.Security.Tests.Diagnostics;

/// <summary>
/// Local structured logging sink (FS-CRAWL-001 §10.5): JSON-line events, no
/// identity dimensions (privacy invariant), registered as purgeable Log
/// artifacts through the diagnostics purge API consumed by the settings surface.
/// </summary>
public sealed class StructuredLogSinkTests
{
    [Fact]
    public void Write_ProducesStructuredJsonLine_WithTechnicalProperties()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var sink = new LocalStructuredLogSink(registry);

        sink.Write(LocalLogLevel.Warning, "crawl host throttled", new Dictionary<string, object?>
        {
            ["auditId"] = 42,
            ["host"] = "example.com",
            ["rate"] = 2.5,
        });

        string[] lines = ReadSingleLogFile(registry);
        string line = Assert.Single(lines);
        using JsonDocument doc = JsonDocument.Parse(line);

        Assert.Equal("Warning", doc.RootElement.GetProperty("level").GetString());
        Assert.Equal("crawl host throttled", doc.RootElement.GetProperty("message").GetString());
        Assert.True(doc.RootElement.TryGetProperty("timestampUtc", out _));

        JsonElement properties = doc.RootElement.GetProperty("properties");
        Assert.Equal(42, properties.GetProperty("auditId").GetInt32());
        Assert.Equal("example.com", properties.GetProperty("host").GetString());
        Assert.Equal(2.5, properties.GetProperty("rate").GetDouble());
    }

    [Fact]
    public void Write_StripsIdentityDimensions_KeepsTechnicalDimensions()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var sink = new LocalStructuredLogSink(registry);

        sink.Write(LocalLogLevel.Information, "privacy check", new Dictionary<string, object?>
        {
            ["userName"] = "identity-user-value",
            ["userId"] = 7,
            ["machineId"] = "identity-machine-value",
            ["accountId"] = "identity-account-value",
            ["hostname"] = "identity-host-value",
            ["sid"] = "identity-sid-value",
            ["identity"] = "identity-raw-value",
            ["osUser"] = "identity-osuser-value",
            ["auditId"] = 42,
            ["host"] = "example.com",
        });

        string[] lines = ReadSingleLogFile(registry);
        string line = Assert.Single(lines);

        Assert.DoesNotContain("identity-user-value", line, StringComparison.Ordinal);
        Assert.DoesNotContain("identity-machine-value", line, StringComparison.Ordinal);
        Assert.DoesNotContain("identity-account-value", line, StringComparison.Ordinal);
        Assert.DoesNotContain("identity-host-value", line, StringComparison.Ordinal);
        Assert.DoesNotContain("identity-sid-value", line, StringComparison.Ordinal);
        Assert.DoesNotContain("identity-raw-value", line, StringComparison.Ordinal);
        Assert.DoesNotContain("identity-osuser-value", line, StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement properties = doc.RootElement.GetProperty("properties");
        Assert.Equal(2, properties.EnumerateObject().Count());
        Assert.Equal(42, properties.GetProperty("auditId").GetInt32());
        Assert.Equal("example.com", properties.GetProperty("host").GetString());
    }

    [Fact]
    public void Write_NeverAddsAmbientIdentityContext()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var sink = new LocalStructuredLogSink(registry);

        sink.Write(LocalLogLevel.Information, "ambient check");

        string[] lines = ReadSingleLogFile(registry);
        string line = Assert.Single(lines);

        if (!string.IsNullOrEmpty(Environment.UserName))
        {
            Assert.DoesNotContain(Environment.UserName, line, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Environment.MachineName, line, StringComparison.Ordinal);

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "timestampUtc", "level", "message", "properties",
        };
        using JsonDocument doc = JsonDocument.Parse(line);
        foreach (JsonProperty property in doc.RootElement.EnumerateObject())
        {
            Assert.Contains(property.Name, allowed);
        }
    }

    [Fact]
    public async Task LogFile_IsRegistered_Listable_AndPurgeable_ViaDiagnosticsPurgeApi()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var sink = new LocalStructuredLogSink(registry);
        var handler = new MinidumpCrashHandler(registry);

        sink.Write(LocalLogLevel.Information, "purge api test");

        DiagnosticArtifactDto log = Assert.Single(await handler.ListArtifactsAsync(DiagnosticArtifactKind.Log));
        Assert.Equal(DiagnosticArtifactKind.Log, log.Kind);
        Assert.True(File.Exists(log.FilePath));
        Assert.True(log.SizeBytes > 0);

        // The settings-surface purge API (ICrashDumpHandler.PurgeArtifactsAsync)
        // removes the log artifact from disk and registry.
        Assert.Equal(1, await handler.PurgeArtifactsAsync(DiagnosticArtifactKind.Log));
        Assert.False(File.Exists(log.FilePath));
        Assert.Empty(await handler.ListArtifactsAsync(null));
    }

    [Fact]
    public async Task Write_AfterPurge_RecreatesAndReregistersCurrentFile()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var sink = new LocalStructuredLogSink(registry);
        var handler = new MinidumpCrashHandler(registry);

        sink.Write(LocalLogLevel.Information, "before purge");
        DiagnosticArtifactDto original = Assert.Single(await handler.ListArtifactsAsync(DiagnosticArtifactKind.Log));
        await handler.PurgeArtifactsAsync(DiagnosticArtifactKind.Log);

        sink.Write(LocalLogLevel.Information, "after purge");

        DiagnosticArtifactDto recreated = Assert.Single(await handler.ListArtifactsAsync(DiagnosticArtifactKind.Log));
        Assert.True(File.Exists(recreated.FilePath));
        Assert.True(recreated.Id > original.Id);
        Assert.Contains("after purge", File.ReadAllText(recreated.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipleWrites_AppendToSameDayFile_RegisteredOnce()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var sink = new LocalStructuredLogSink(registry);

        sink.Write(LocalLogLevel.Information, "event one");
        sink.Write(LocalLogLevel.Warning, "event two");
        sink.Write(LocalLogLevel.Error, "event three");

        string[] lines = ReadSingleLogFile(registry);
        Assert.Equal(3, lines.Length);
        Assert.Single(await registry.ListAsync(DiagnosticArtifactKind.Log));
    }

    [Fact]
    public void Write_NullMessage_Throws()
    {
        using var temp = new TempDirectory();
        var registry = new DiagnosticArtifactRegistry(new DiagnosticsOptions(temp.RootPath));
        var sink = new LocalStructuredLogSink(registry);

        Assert.Throws<ArgumentNullException>(() => sink.Write(LocalLogLevel.Information, null!));
    }

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalStructuredLogSink(null!));
    }

    [Theory]
    [InlineData("userName", true)]
    [InlineData("USER", true)]
    [InlineData("accountId", true)]
    [InlineData("machineName", true)]
    [InlineData("identityId", true)]
    [InlineData("hostname", true)]
    [InlineData("sid", true)]
    [InlineData("auditId", false)]
    [InlineData("host", false)]
    [InlineData("phase", false)]
    [InlineData("status", false)]
    public void IdentityDimensionFilter_ClassifiesKeys(string key, bool expected)
    {
        Assert.Equal(expected, IdentityDimensionFilter.IsIdentityDimension(key));
    }

    private static string[] ReadSingleLogFile(DiagnosticArtifactRegistry registry)
    {
        string[] files = Directory.GetFiles(registry.LogDirectory, "*.jsonl");
        string file = Assert.Single(files);
        return File.ReadAllLines(file);
    }
}