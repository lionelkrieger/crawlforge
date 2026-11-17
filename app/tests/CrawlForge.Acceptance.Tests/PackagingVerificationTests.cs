using System.Diagnostics;
using System.Text.Json;
using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Acceptance.Tests;

/// <summary>
/// WP-07-04 offline packaging verification. The shared fixture runs
/// packaging/verify-packaging.ps1 exactly once (Release publish of the real
/// composition root + asset bundling + licensing absence + update-feed absence
/// + offline first launch from the PUBLISHED layout + local Electron binary
/// resolution). The facts then re-verify the critical invariants independently
/// against the published layout, not only against the script's report.
/// </summary>
public sealed class PackagingVerificationTests
{
    private static readonly Lazy<Task<PackagingRun>> Run = new(() => Task.Run(RunScriptAsync));

    private sealed record PackagingRun(int ExitCode, string Output, JsonDocument Report)
    {
        public bool AllPassed => Report.RootElement.GetProperty("allPassed").GetBoolean();
    }

    private static async Task<PackagingRun> RunScriptAsync()
    {
        var script = EvidenceIntegrityTests.Resolve("packaging/verify-packaging.ps1");
        var info = new ProcessStartInfo
        {
            FileName = "pwsh",
            ArgumentList = { "-NoProfile", "-File", script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var reportPath = EvidenceIntegrityTests.Resolve("packaging/artifacts/packaging-report.json");
        Assert.True(File.Exists(reportPath), $"Packaging report missing. Script exit {process.ExitCode}\n{stdout}\n{stderr}");
        var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        return new PackagingRun(process.ExitCode, stdout + stderr, report);
    }

    [Fact]
    public async Task PackagingScript_Completes_AndEveryCheckPasses()
    {
        var run = await Run.Value;
        Assert.True(run.ExitCode == 0, $"verify-packaging.ps1 exit {run.ExitCode}\n{run.Output}");
        Assert.True(run.AllPassed, run.Output);
        foreach (var check in run.Report.RootElement.GetProperty("checks").EnumerateArray())
        {
            Assert.True(check.GetProperty("passed").GetBoolean(),
                $"Packaging check failed: {check.GetProperty("name").GetString()} — {check.GetProperty("detail").GetString()}");
        }
    }

    [Fact]
    public async Task PublishedLayout_BundlesEveryRequiredAssetFamily()
    {
        var run = await Run.Value;
        Assert.True(run.AllPassed, run.Output);

        // Independent re-verification against the published layout.
        var publishAssets = Path.Combine(
            run.Report.RootElement.GetProperty("publishDirectory").GetString()!, "assets");
        string[] families =
        [
            "waf-signatures", "stopwords", "dictionaries", "font-metrics", "tech-fingerprints",
            "bot-cidr", "ai-agent-profiles", "onnx-model", "export-templates", "plugin-schema"
        ];
        foreach (var family in families)
        {
            var directory = Path.Combine(publishAssets, family);
            Assert.True(Directory.Exists(directory), $"Asset family not bundled: {family}");
            Assert.NotEmpty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task PublishedBuild_ShipsNoLicensingManifest_OrAccountBoundUpdateFeed()
    {
        var run = await Run.Value;
        Assert.True(run.AllPassed, run.Output);

        var publishDirectory = run.Report.RootElement.GetProperty("publishDirectory").GetString()!;
        var manifests = Directory.GetFiles(publishDirectory, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).ToLowerInvariant() is var name &&
                (name.StartsWith("license-manifest") || name.StartsWith("licence-manifest") ||
                 name.StartsWith("entitlement") || name.StartsWith("activation-key")))
            .ToList();
        Assert.Empty(manifests);

        // No product configuration carries an account-bound update endpoint.
        foreach (var json in Directory.GetFiles(publishDirectory, "*.json"))
        {
            var content = await File.ReadAllTextAsync(json);
            Assert.DoesNotContain("\"updateFeed\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"updateUrl\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"accountUrl\"", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task FirstLaunch_OfflineInitialization_CompletesFromThePublishedLayout()
    {
        var run = await Run.Value;
        var check = run.Report.RootElement.GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "first-launch-offline-init");
        Assert.True(check.GetProperty("passed").GetBoolean(), check.GetProperty("detail").GetString());
        // The proof ran the PUBLISHED executable against a fresh data directory:
        // migrations + settings defaults + hub readiness, all local.
        Assert.Contains("health=200", check.GetProperty("detail").GetString());
        Assert.Contains("settings=200", check.GetProperty("detail").GetString());
    }

    [Fact]
    public void ChromiumRenderingRuntime_ResolvesFromLocalSourcesOnly()
    {
        // The product resolver probes packaged assets → user-configured path →
        // local Chrome/Edge installs. It NEVER downloads a browser or driver.
        var resolver = new LocalBrowserResolver();
        var path = resolver.Resolve();
        Assert.True(File.Exists(path));
        Assert.True(
            path.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("chromium", StringComparison.OrdinalIgnoreCase),
            $"Unexpected rendering binary: {path}");

        // A missing user-configured path is a LOCAL configuration error — the
        // resolver must not fall back to any remote source.
        var missing = new LocalBrowserResolver(new BrowserResolutionOptions
        {
            UserConfiguredExecutablePath = Path.Combine(Path.GetTempPath(), "cf-missing-browser-" + Guid.NewGuid().ToString("N"), "chrome.exe")
        });
        Assert.Throws<BrowserResolutionException>(() => missing.Resolve());
    }

    [Fact]
    public async Task ElectronShellBinary_IsConfiguredLocalOnly_WithNoRuntimeFetch()
    {
        var run = await Run.Value;
        var electron = run.Report.RootElement.GetProperty("electron");
        Assert.True(electron.GetProperty("noRuntimeFetch").GetBoolean());
        var mode = electron.GetProperty("mode").GetString();
        Assert.Contains(mode, new[] { "override-env", "bundled-vendor", "system-path", "not-bundled" });

        // The vendor drop-in mechanism + no-fetch policy are documented.
        Assert.True(File.Exists(EvidenceIntegrityTests.Resolve("packaging/electron/resolve-electron-binary.ps1")));
        Assert.True(File.Exists(EvidenceIntegrityTests.Resolve("packaging/electron/README.md")));
        Assert.True(File.Exists(EvidenceIntegrityTests.Resolve("packaging/README.md")));
    }
}
