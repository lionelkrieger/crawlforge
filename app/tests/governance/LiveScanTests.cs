using CrawlForge.Tools.Governance;
using Xunit;

namespace CrawlForge.Tests.Governance;

/// <summary>
/// Mirrors the workpiece verification command: the analyzer must pass the LIVE app\src
/// tree (clean Host implementation + scaffold stubs) with zero violations.
/// </summary>
public class LiveScanTests
{
    [Fact]
    public void Live_app_src_tree_passes_the_governance_gate()
    {
        var engine = new GovernanceAnalyzerEngine();
        var report = engine.Analyze(new GovernanceAnalysisRequest(TestPaths.SrcRoot));

        Assert.True(
            report.Passed,
            "Governance violations in live app\\src:\n"
            + string.Join("\n", report.Violations.Select(v => $"  {v.RuleId} {v.File}:{v.Line}:{v.Column} {v.Evidence}")));
        Assert.True(report.FilesScanned > 0, "The live scan must actually scan source files.");
    }

    [Fact]
    public void Live_app_root_tree_passes_the_governance_gate()
    {
        // WP-07-G1 / WP-07-02 verification posture: the FULL app tree (product code,
        // frontend, assets, tools, configs) with verification code and lockfiles
        // excluded by the enumerator. Zero violations required.
        var engine = new GovernanceAnalyzerEngine();
        var report = engine.Analyze(new GovernanceAnalysisRequest(TestPaths.AppRoot));

        Assert.True(
            report.Passed,
            "Governance violations in live app root:\n"
            + string.Join("\n", report.Violations.Select(v => $"  {v.RuleId} {v.File}:{v.Line}:{v.Column} {v.Evidence}")));
        Assert.True(report.FilesScanned > 0, "The live app-root scan must actually scan source files.");
    }
}
