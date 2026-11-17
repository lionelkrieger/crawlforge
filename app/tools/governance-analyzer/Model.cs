namespace CrawlForge.Tools.Governance;

/// <summary>Severity of a governance finding. All negative-requirement rules are errors.</summary>
public enum GovernanceSeverity
{
    Error,
}

/// <summary>One detected violation, anchored to a source location.</summary>
public sealed record GovernanceViolation(
    string RuleId,
    GovernanceSeverity Severity,
    string File,
    int Line,
    int Column,
    string Evidence,
    string Message)
{
    /// <summary>Deterministic ordering key: file, line, column, rule, evidence.</summary>
    public static int Compare(GovernanceViolation left, GovernanceViolation right)
    {
        var byFile = string.CompareOrdinal(left.File, right.File);
        if (byFile != 0)
        {
            return byFile;
        }

        var byLine = left.Line.CompareTo(right.Line);
        if (byLine != 0)
        {
            return byLine;
        }

        var byColumn = left.Column.CompareTo(right.Column);
        if (byColumn != 0)
        {
            return byColumn;
        }

        var byRule = string.CompareOrdinal(left.RuleId, right.RuleId);
        return byRule != 0 ? byRule : string.CompareOrdinal(left.Evidence, right.Evidence);
    }
}

/// <summary>Static metadata for one rule, including its detection basis (heuristic transparency).</summary>
public sealed record GovernanceRule(
    string Id,
    string Title,
    string SpecReference,
    GovernanceSeverity Severity,
    string DetectionBasis);

/// <summary>Machine-readable analysis report. Fully deterministic for identical inputs.</summary>
public sealed record GovernanceReport(
    string Tool,
    string ToolVersion,
    string Root,
    int FilesScanned,
    IReadOnlyList<GovernanceRule> Rules,
    IReadOnlyList<GovernanceViolation> Violations,
    IReadOnlyDictionary<string, int> ViolationsByRule,
    bool Passed)
{
    public static GovernanceReport Create(
        string root,
        int filesScanned,
        IReadOnlyList<GovernanceRule> rules,
        IReadOnlyList<GovernanceViolation> violations)
    {
        var ordered = violations.OrderBy(v => v, Comparer<GovernanceViolation>.Create(GovernanceViolation.Compare)).ToList();
        var byRule = ordered
            .GroupBy(v => v.RuleId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new GovernanceReport(
            Tool: "crawlforge-governance-analyzer",
            ToolVersion: "1.0.0",
            Root: root,
            FilesScanned: filesScanned,
            Rules: rules,
            Violations: ordered,
            ViolationsByRule: byRule,
            Passed: ordered.Count == 0);
    }
}

/// <summary>Parameters for one analysis run.</summary>
public sealed record GovernanceAnalysisRequest(
    string RootDirectory,
    string? BoundaryConfigPath = null,
    IReadOnlyList<string>? RuleFilter = null);

/// <summary>
/// Local tool contract implemented by the governance analyzer (WP-00-05).
/// Later workpieces invoke the analyzer through the CLI; tests use this contract directly.
/// </summary>
public interface IGovernanceAnalyzer
{
    GovernanceReport Analyze(GovernanceAnalysisRequest request);
}
