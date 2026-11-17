using Xunit;

namespace CrawlForge.Audit.Tests.Custom;

/// <summary>
/// Boundary evidence: the custom production surface is pure offline analysis
/// code — no network, machine identity, or telemetry tokens; the JS sandbox
/// has no remote loading path (exclusions E1/E6, privacy invariant).
/// </summary>
public sealed class CustomBoundaryTests
{
    private static readonly string[] ForbiddenTokens =
    {
        "HttpClient",
        "WebClient",
        "WebRequest",
        "Sockets",
        "Dns.",
        "Environment.MachineName",
        "Environment.UserName",
        "Environment.UserDomainName",
        "ApplicationInsights",
        "TelemetryClient",
        "EventSource",
        "Process.Start",
        "Registry.",
        "AllowClr",
        "EnableModules",
    };

    [Fact]
    public void ProductionSource_ContainsNoNetworkIdentityOrTelemetryTokens()
    {
        string sourceRoot = ResolveCustomSourceRoot();
        string[] files = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string content = File.ReadAllText(file);
            foreach (string token in ForbiddenTokens)
            {
                Assert.False(
                    content.Contains(token, StringComparison.Ordinal),
                    $"Forbidden token '{token}' found in {file}.");
            }
        }
    }

    [Fact]
    public void ProductionSource_ReferencesOnlyApprovedSurfaces()
    {
        string sourceRoot = ResolveCustomSourceRoot();
        foreach (string file in Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            foreach (string line in File.ReadLines(file))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("using ", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.False(
                    trimmed.StartsWith("using CrawlForge.Crawler", StringComparison.Ordinal)
                    || trimmed.StartsWith("using CrawlForge.Data", StringComparison.Ordinal)
                    || trimmed.StartsWith("using CrawlForge.Security", StringComparison.Ordinal)
                    || trimmed.StartsWith("using CrawlForge.Rendering", StringComparison.Ordinal),
                    $"Custom production code must not reference sibling stacks: {trimmed} ({file})");
            }
        }
    }

    [Fact]
    public void SnippetRunner_HasNoRemoteScriptLoadingPath()
    {
        string sourceRoot = ResolveCustomSourceRoot();
        string runner = File.ReadAllText(Path.Combine(sourceRoot, "JintSnippetRunner.cs"));

        // The only script-loading surface is the local library dictionary.
        Assert.Contains("LocalScriptLibrary", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadString", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", runner, StringComparison.Ordinal);
    }

    private static string ResolveCustomSourceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrawlForge.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string sourceRoot = Path.Combine(directory!.FullName, "src", "backend", "CrawlForge.Audit", "Custom");
        Assert.True(Directory.Exists(sourceRoot), $"Custom source root not found at {sourceRoot}.");
        return sourceRoot;
    }
}