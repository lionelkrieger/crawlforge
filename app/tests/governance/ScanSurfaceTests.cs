using CrawlForge.Tools.Governance;
using Xunit;

namespace CrawlForge.Tests.Governance;

/// <summary>
/// WP-07-G1 scan-surface amendment proofs, BOTH directions:
/// (1) planted violations under product surface (src/) are STILL detected, including
///     under the wave-gate posture (--root src);
/// (2) verification-code canaries under tests/ and dependency lockfiles are ignored;
/// (3) the live app-root scan passes with zero violations.
/// No rule is weakened: the same planted payload is detected or ignored purely by path.
/// </summary>
public sealed class ScanSurfaceTests : IDisposable
{
    private const string PlantedViolation = """
        using System;
        using Microsoft.Win32;

        namespace Planted;

        public static class IdentityProbe
        {
            public static string ReadMachineGuid()
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
            }
        }
        """;

    private readonly string _scratch = TestPaths.CreateScratchDirectory();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort scratch cleanup.
        }
    }

    [Fact]
    public void PlantedViolation_UnderSrc_IsStillDetected_AppRootPosture()
    {
        WriteFile("src/backend/Probe/IdentityProbe.cs", PlantedViolation);

        var report = new GovernanceAnalyzerEngine().Analyze(new GovernanceAnalysisRequest(_scratch));

        Assert.Contains(report.Violations, v => v.RuleId == "NR-01");
        Assert.False(report.Passed);
    }

    [Fact]
    public void PlantedViolation_UnderSrc_IsStillDetected_WaveGatePosture()
    {
        WriteFile("src/backend/Probe/IdentityProbe.cs", PlantedViolation);

        // Wave-gate posture: root = src directly.
        var report = new GovernanceAnalyzerEngine().Analyze(
            new GovernanceAnalysisRequest(Path.Combine(_scratch, "src")));

        Assert.Contains(report.Violations, v => v.RuleId == "NR-01");
        Assert.False(report.Passed);
    }

    [Fact]
    public void IdenticalCanary_UnderTests_IsIgnored()
    {
        WriteFile("tests/CrawlForge.Something.Tests/IdentityCanary.cs", PlantedViolation);

        var report = new GovernanceAnalyzerEngine().Analyze(new GovernanceAnalysisRequest(_scratch));

        Assert.True(report.Passed, "Verification code is not product surface (WP-07-G1).");
        Assert.Equal(0, report.FilesScanned);
    }

    [Fact]
    public void LockfileMetadata_IsIgnored_ForEveryLockfileName()
    {
        const string lockfileNoise = """
            {
              "name": "sample",
              "lockfileVersion": 3,
              "packages": {
                "node_modules/left-pad": { "version": "1.0.0", "license": "MIT" },
                "node_modules/other": { "version": "2.0.0", "license": "Apache-2.0" }
              }
            }
            """;
        WriteFile("package-lock.json", lockfileNoise);
        WriteFile("npm-shrinkwrap.json", lockfileNoise);
        WriteFile("packages.lock.json", lockfileNoise);
        WriteFile("yarn.lock", lockfileNoise);
        WriteFile("pnpm-lock.yaml", lockfileNoise);

        var report = new GovernanceAnalyzerEngine().Analyze(new GovernanceAnalysisRequest(_scratch));

        Assert.True(report.Passed, "Dependency lockfiles are build metadata, not product licensing (WP-07-G1).");
        Assert.Equal(0, report.FilesScanned);
    }

    [Fact]
    public void ProductSurfaceBesideTestsAndLockfiles_RemainsFullyEnforced()
    {
        // Mixed tree: the exclusion must not mask a real product violation sitting
        // next to ignored verification code and lockfiles.
        WriteFile("package-lock.json", """{ "packages": { "node_modules/x": { "license": "MIT" } } }""");
        WriteFile("tests/Canary/Probe.cs", PlantedViolation);
        WriteFile("src/backend/Real/Probe.cs", PlantedViolation);

        var report = new GovernanceAnalyzerEngine().Analyze(new GovernanceAnalysisRequest(_scratch));

        // Every detection comes from the product file only (the planted payload trips
        // several NR-01 detectors); nothing from tests/ or the lockfile is reported.
        Assert.NotEmpty(report.Violations);
        Assert.All(report.Violations, v =>
        {
            Assert.Equal("NR-01", v.RuleId);
            Assert.Contains("src/backend/Real/Probe.cs", v.File.Replace('\\', '/'));
        });
        Assert.False(report.Passed);
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_scratch, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
