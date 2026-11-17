using CrawlForge.Tools.Governance;
using Xunit;

namespace CrawlForge.Tests.Governance;

/// <summary>
/// Criterion #2 static evidence: for EVERY governance rule, a planted-violation fixture
/// fires the rule and a clean fixture passes. Fixtures live under tests\governance only —
/// violations are never planted under app\src.
/// </summary>
public class FixtureTests
{
    public static TheoryData<string> RuleDirectories()
    {
        var data = new TheoryData<string>();
        foreach (var directory in Directory.GetDirectories(TestPaths.FixturesRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(directory)!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RuleDirectories))]
    public void Rule_fires_on_every_planted_violation_and_passes_every_clean_fixture(string ruleId)
    {
        var ruleDirectory = Path.Combine(TestPaths.FixturesRoot, ruleId);
        var engine = new GovernanceAnalyzerEngine();
        var report = engine.Analyze(new GovernanceAnalysisRequest(ruleDirectory));

        var violationFiles = RelativeFiles(ruleDirectory, "violation.*");
        var cleanFiles = RelativeFiles(ruleDirectory, "clean.*");
        Assert.NotEmpty(violationFiles);
        Assert.NotEmpty(cleanFiles);

        foreach (var file in violationFiles)
        {
            Assert.True(
                report.Violations.Any(v => string.Equals(v.RuleId, ruleId, StringComparison.Ordinal)
                    && string.Equals(v.File, file, StringComparison.Ordinal)),
                $"Rule {ruleId} did not fire on planted violation '{file}'. "
                + $"Found: [{string.Join(", ", report.Violations.Select(v => $"{v.RuleId}@{v.File}:{v.Line}"))}]");
        }

        foreach (var file in cleanFiles)
        {
            var onFile = report.Violations.Where(v => string.Equals(v.File, file, StringComparison.Ordinal)).ToList();
            Assert.True(
                onFile.Count == 0,
                $"Clean fixture '{file}' produced violations: "
                + $"[{string.Join(", ", onFile.Select(v => $"{v.RuleId}:{v.Line} {v.Evidence}"))}]");
        }
    }

    [Fact]
    public void Fixture_suite_covers_exactly_the_full_rule_catalog()
    {
        var fixtureRules = Directory.GetDirectories(TestPaths.FixturesRoot)
            .Select(directory => Path.GetFileName(directory)!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var catalogRules = RuleCatalog.All.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(catalogRules, fixtureRules);
        Assert.Equal(20, catalogRules.Count);
    }

    private static List<string> RelativeFiles(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
}
