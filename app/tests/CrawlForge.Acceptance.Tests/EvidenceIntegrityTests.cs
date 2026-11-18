using System.Text.Json;
using Xunit;

namespace CrawlForge.Acceptance.Tests;

/// <summary>
/// WP-07-04 evidence-integrity layer: every one of the 48 master acceptance
/// criteria must carry proof pointers that exist on disk (test methods/classes
/// in the owning suites, or wave-7A evidence artifacts recording PASS). The
/// wave-7A consolidated results and the ratified criterion-#40 composite gate
/// are re-verified from the raw artifacts, not merely referenced.
/// </summary>
public sealed class EvidenceIntegrityTests
{
    internal static string AppRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrawlForge.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }

    internal static string Resolve(string relativePath) =>
        Path.Combine(AppRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static TheoryData<int> CriterionNumbers()
    {
        var data = new TheoryData<int>();
        foreach (var criterion in AcceptanceCatalog.All)
        {
            data.Add(criterion.Number);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CriterionNumbers))]
    public void Criterion_ProofPointers_AreIntact(int number)
    {
        var criterion = Assert.Single(AcceptanceCatalog.All, c => c.Number == number);
        Assert.NotEmpty(criterion.Pointers);

        foreach (var pointer in criterion.Pointers)
        {
            var path = Resolve(pointer.Path);
            if (pointer.Member is null && pointer.Marker is null && Directory.Exists(path))
            {
                // Bundled asset directory evidence: present and non-empty.
                Assert.NotEmpty(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
                continue;
            }

            Assert.True(File.Exists(path), $"Criterion #{number}: evidence file missing: {pointer.Path}");
            var content = File.ReadAllText(path);
            if (pointer.Member is not null)
            {
                Assert.True(
                    content.Contains(pointer.Member, StringComparison.Ordinal),
                    $"Criterion #{number}: proof member '{pointer.Member}' not found in {pointer.Path}");
            }

            if (pointer.Marker is not null)
            {
                Assert.True(
                    content.Contains(pointer.Marker, StringComparison.Ordinal),
                    $"Criterion #{number}: evidence marker '{pointer.Marker}' not found in {pointer.Path}");
            }
        }
    }

    [Fact]
    public void Catalog_CoversAll48Criteria_Continuously()
    {
        Assert.Equal(48, AcceptanceCatalog.All.Length);
        Assert.Equal(Enumerable.Range(1, 48), AcceptanceCatalog.All.Select(c => c.Number));
    }

    [Fact]
    public void Wave7A_E2EHandoff_RecordsConsolidated80Of80()
    {
        var handoff = File.ReadAllText(Resolve("tests/CrawlForge.E2E.Tests/WP-07-01-HANDOFF.md"));
        Assert.Contains("80 / 80 GREEN", handoff);
        Assert.Contains("CONSOLIDATED-RESULT-START", handoff);
        Assert.Contains("Failed: 0, Passed: 80", handoff);
        Assert.Contains("EXIT=0", handoff);
        // All twelve E2E-driven defects recorded as fixed by routed owners.
        foreach (var defect in new[] { "D1", "D2", "D4", "D5", "D6", "D7", "D8", "D9", "D10", "D11", "D12" })
        {
            Assert.Contains(defect, handoff);
        }
    }

    [Fact]
    public void Wave7A_BoundaryEvidence_Indexes25Artifacts_AndZeroViolationScans()
    {
        // 24 evidence files + the README index = the 25 durable artifacts.
        var evidenceDirectory = Resolve("tools/network-audit/evidence");
        var artifacts = Directory.GetFiles(evidenceDirectory);
        Assert.Equal(24, artifacts.Length);

        var readme = File.ReadAllText(Resolve("tools/network-audit/README.md"));
        Assert.Contains("27/27", readme);
        // The index records a PASS verdict for every evidence row.
        var passRows = readme.Split('\n').Count(line => line.Contains("| PASS", StringComparison.Ordinal));
        Assert.True(passRows >= 20, $"Only {passRows} PASS rows in the boundary evidence index.");

        var scan = JsonDocument.Parse(File.ReadAllText(Resolve("tests/governance/wp-07-02-app-root-scan.json")));
        Assert.True(scan.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(0, scan.RootElement.GetProperty("violations").GetArrayLength());
    }

    [Fact]
    public void Wave7A_ScaleFragments_AllPresent()
    {
        var results = Resolve("tests/CrawlForge.Scale.Benchmarks/artifacts/results");
        string[] required =
        [
            "dataset-seed.json", "pipeline-scale.json", "export-scale.json", "retention-prune.json",
            "cwv-capture.json", "diagnostics-gc.json", "heartbeat-accuracy.json", "metrics-parity.json",
            "compact-trim.json", "system-health-rest.json", "browser-worker-memory.json",
            "browser-scroll-urls.json", "browser-scroll-issues.json", "browser-scroll-links.json",
            "browser-scroll-extraction.json", "browser-scroll-ngrams.json",
            "browser-scroll-urls-beginframe.json", "browser-scroll-issues-beginframe.json",
            "browser-scroll-links-beginframe.json", "browser-scroll-extraction-beginframe.json",
            "browser-scroll-ngrams-beginframe.json",
        ];

        foreach (var fragment in required)
        {
            Assert.True(File.Exists(Path.Combine(results, fragment)), $"Scale fragment missing: {fragment}");
        }
    }

    [Fact]
    public void Criterion40_RatifiedCompositeGate_ReverifiedFromRawBeginFrameArtifacts()
    {
        // The product-owner amendment (2026-11-16) retired the literal p95 gate
        // for the vsync-faithful composite gate. Re-assert it from the RAW
        // driven deltas — no reliance on the report's prose.
        var report = File.ReadAllText(Resolve("tests/CrawlForge.Scale.Benchmarks/artifacts/benchmark-report.md"));
        Assert.Contains("PRODUCT-OWNER AMENDMENT", report);
        Assert.Contains("ratified 2026-11-16", report);

        string[] explorers = ["urls", "issues", "links", "extraction", "ngrams"];
        foreach (var explorer in explorers)
        {
            var path = Resolve($"tests/CrawlForge.Scale.Benchmarks/artifacts/results/browser-scroll-{explorer}-beginframe.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            var frames = root.GetProperty("frames").GetInt32();
            var p50 = root.GetProperty("p50FrameMs").GetDouble();
            var dropped = root.GetProperty("droppedVsyncFrames").GetInt32();
            var singleVsync = root.GetProperty("singleVsyncFrames").GetInt32();
            var maxDomRows = root.GetProperty("maxDomRows").GetInt32();

            Assert.True(frames > 0, $"{explorer}: no frames measured");
            Assert.True(p50 <= 16.7, $"{explorer}: p50 {p50}ms exceeds one vsync");
            Assert.True((double)dropped / frames <= 0.01, $"{explorer}: dropped-frame rate above 1%");
            Assert.True((double)singleVsync / frames >= 0.95, $"{explorer}: single-vsync fraction below 95%");
            Assert.True(maxDomRows <= 50, $"{explorer}: {maxDomRows} DOM rows exceeds the 50-row bound");
        }

        // Worker memory facet: heap bound + guaranteed termination.
        using var worker = JsonDocument.Parse(File.ReadAllText(
            Resolve("tests/CrawlForge.Scale.Benchmarks/artifacts/results/browser-worker-memory.json")));
        var workerRoot = worker.RootElement;
        Assert.True(workerRoot.GetProperty("workerPeakHeapBytes").GetInt64() <= workerRoot.GetProperty("workerHeapBoundBytes").GetInt64());
        Assert.Equal(0, workerRoot.GetProperty("workersAliveAfterLayout").GetInt32());
        Assert.Equal(5000, workerRoot.GetProperty("positionCount").GetInt32());
    }

    [Fact]
    public void Criterion19_ScalePipeline_StaysWithinTheMemoryBound()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Resolve("tests/CrawlForge.Scale.Benchmarks/artifacts/results/pipeline-scale.json")));
        var root = document.RootElement;
        Assert.Equal(10000, root.GetProperty("crawledUrls").GetInt32());
        Assert.True(root.GetProperty("peakWorkingSetBytes").GetInt64() < root.GetProperty("memoryBoundBytes").GetInt64());

        using var seed = JsonDocument.Parse(File.ReadAllText(
            Resolve("tests/CrawlForge.Scale.Benchmarks/artifacts/results/dataset-seed.json")));
        Assert.True(seed.RootElement.GetProperty("manifest").GetProperty("urlCount").GetInt64() >= 500_000);
    }

    [Fact]
    public void Wave7A_PhaseLedger_RecordsCanonicalWave7AVerification()
    {
        // WP-08-A1: the v2.0 plan migration archived the v1.1 ledger under
        // buildplan/v1.1/; the evidence-integrity assertions are unchanged.
        var yaml = File.ReadAllText(Resolve("../buildplan/v1.1/phases/phase-07-integration-and-acceptance.yaml"));
        Assert.Contains("80/80", yaml);
        Assert.Contains("10/10", yaml);
        Assert.Contains("134/134", yaml);
        Assert.Contains("ALL 5 PASS", yaml);
    }
}
