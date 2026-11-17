using Xunit;

namespace CrawlForge.Audit.Tests.Indexability;

/// <summary>
/// Boundary evidence: the indexability production surface is pure analysis
/// code — no network, machine identity, or telemetry tokens — so every rule
/// re-runs offline from stored artifacts (FS-CRAWL-001 §9.6).
/// </summary>
public sealed class IndexabilityBoundaryTests
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
    };

    [Fact]
    public void ProductionSource_ContainsNoNetworkIdentityOrTelemetryTokens()
    {
        string sourceRoot = ResolveIndexabilitySourceRoot();
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
    public void ProductionSource_ReferencesOnlyContractsAndBcl()
    {
        string sourceRoot = ResolveIndexabilitySourceRoot();
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
                    $"Production analyzer must not reference sibling stacks: {trimmed} ({file})");
            }
        }
    }

    private static string ResolveIndexabilitySourceRoot()
    {
        // Walk up from the test binary to the solution root, then into the
        // owned production folder (solution file: CrawlForge.sln).
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrawlForge.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string sourceRoot = Path.Combine(directory!.FullName, "src", "backend", "CrawlForge.Audit", "Indexability");
        Assert.True(Directory.Exists(sourceRoot), $"Indexability source root not found at {sourceRoot}.");
        return sourceRoot;
    }
}