using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Xunit;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Provider-switch orchestration contract (criterion #18): ordered forward switch
/// (connection-test gate → archive export → schema migration + ledger verification
/// → archive import), abort-without-side-effects on a failed gate, revert via
/// archive import, and identical-report-output parity comparison.
/// </summary>
public sealed class ProviderSwitchTests
{
    private static (ProviderSwitchOrchestrator Orchestrator, FakeDbProvider SourceProvider, FakeDbProvider TargetProvider, FakeMigrator SourceMigrator, FakeMigrator TargetMigrator, FakeArchiveService SourceArchive, FakeArchiveService TargetArchive, CallSequence Sequence) BuildSwitch()
    {
        var sequence = new CallSequence();
        var sourceProvider = new FakeDbProvider { Name = "sourceProvider", Dialect = "sqlite", Kind = CrawlForge.Contracts.Enums.DatabaseProviderKind.Sqlite, Sequence = sequence };
        var targetProvider = new FakeDbProvider { Name = "targetProvider", Dialect = "mysql", Sequence = sequence };
        var sourceMigrator = new FakeMigrator { Name = "sourceMigrator", Dialect = "sqlite", Sequence = sequence };
        var targetMigrator = new FakeMigrator { Name = "targetMigrator", Dialect = "mysql", Sequence = sequence };
        var sourceArchive = new FakeArchiveService { Name = "sourceArchive", Sequence = sequence };
        var targetArchive = new FakeArchiveService { Name = "targetArchive", Sequence = sequence };

        var orchestrator = new ProviderSwitchOrchestrator(
            new ProviderSwitchEndpoint(sourceProvider, sourceMigrator, sourceArchive),
            new ProviderSwitchEndpoint(targetProvider, targetMigrator, targetArchive));

        return (orchestrator, sourceProvider, targetProvider, sourceMigrator, targetMigrator, sourceArchive, targetArchive, sequence);
    }

    /// <summary>The forward switch runs test → export → migrate → ledger check → import, in order.</summary>
    [Fact]
    public async Task Forward_Switch_Runs_The_Full_Ordered_Pipeline()
    {
        var switchContext = BuildSwitch();

        ProviderSwitchResult result = await switchContext.Orchestrator.SwitchAsync(projectId: 7);

        Assert.True(result.Success);
        Assert.Null(result.FailureReason);
        Assert.Equal(40, result.SchemaVersion);
        Assert.Equal(new ProjectArchiveImportResult(7, 2, 10), result.ImportResult);
        Assert.Equal(
            new[]
            {
                "targetProvider.TestConnection",
                "sourceArchive.Export",
                "targetMigrator.Migrate",
                "targetMigrator.GetAppliedMigrations",
                "targetArchive.Import",
            },
            switchContext.Sequence.Entries);
        Assert.Equal(7, switchContext.SourceArchive.LastExportProjectId);
        Assert.Equal(switchContext.SourceArchive.ExportPayload, switchContext.TargetArchive.LastImportBytes);
    }

    /// <summary>A failed connection test aborts the switch before any export, migration, or import.</summary>
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Failed_Connection_Test_Aborts_Without_Side_Effects(bool reachable, bool authenticated, bool compatible)
    {
        var switchContext = BuildSwitch();
        switchContext.TargetProvider.TestResult = new DbConnectionTestResult(reachable, authenticated, compatible, "verdict detail");

        ProviderSwitchResult result = await switchContext.Orchestrator.SwitchAsync(projectId: 7);

        Assert.False(result.Success);
        Assert.Contains("verdict detail", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, result.SchemaVersion);
        Assert.Null(result.ImportResult);
        Assert.Equal(new[] { "targetProvider.TestConnection" }, switchContext.Sequence.Entries);
        Assert.Empty(switchContext.SourceArchive.Calls);
        Assert.Empty(switchContext.TargetMigrator.Calls);
        Assert.Empty(switchContext.TargetArchive.Calls);
    }

    /// <summary>An empty target ledger after migration fails the switch before import.</summary>
    [Fact]
    public async Task Empty_Target_Ledger_Fails_Before_Import()
    {
        var switchContext = BuildSwitch();
        switchContext.TargetMigrator.LedgerRows = 0;

        ProviderSwitchResult result = await switchContext.Orchestrator.SwitchAsync(projectId: 7);

        Assert.False(result.Success);
        Assert.Contains("ledger", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(switchContext.TargetArchive.Calls);
    }

    /// <summary>A ledger row from another dialect fails the switch before import.</summary>
    [Fact]
    public async Task Foreign_Dialect_Ledger_Fails_Before_Import()
    {
        var switchContext = BuildSwitch();
        switchContext.TargetMigrator.OverrideLedgerDialect = "sqlite";

        ProviderSwitchResult result = await switchContext.Orchestrator.SwitchAsync(projectId: 7);

        Assert.False(result.Success);
        Assert.Contains("dialect", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(switchContext.TargetArchive.Calls);
    }

    /// <summary>The reported schema version is the maximum applied ledger version.</summary>
    [Fact]
    public async Task Schema_Version_Is_Maximum_Ledger_Version()
    {
        var switchContext = BuildSwitch();
        switchContext.TargetMigrator.LedgerRows = 3;
        switchContext.TargetMigrator.Version = 40;

        ProviderSwitchResult result = await switchContext.Orchestrator.SwitchAsync(projectId: 7);

        Assert.True(result.Success);
        Assert.Equal(40, result.SchemaVersion);
    }

    /// <summary>Revert imports the archive into the embedded provider after migrating it; no connection test is needed.</summary>
    [Fact]
    public async Task Revert_Imports_Archive_Into_Embedded_Provider()
    {
        var switchContext = BuildSwitch();
        using var archive = new MemoryStream([9, 8, 7]);

        ProviderSwitchResult result = await switchContext.Orchestrator.RevertAsync(archive);

        Assert.True(result.Success);
        Assert.Equal(40, result.SchemaVersion);
        Assert.Equal(new ProjectArchiveImportResult(7, 2, 10), result.ImportResult);
        Assert.Equal(
            new[] { "sourceMigrator.Migrate", "sourceArchive.Import", "sourceMigrator.GetCurrentVersion" },
            switchContext.Sequence.Entries);
        Assert.Equal(new byte[] { 9, 8, 7 }, switchContext.SourceArchive.LastImportBytes);
        Assert.Empty(switchContext.TargetProvider.Calls);
        Assert.Empty(switchContext.TargetMigrator.Calls);
        Assert.Empty(switchContext.TargetArchive.Calls);
    }

    /// <summary>Identical metric and score read models produce an identical-report verdict.</summary>
    [Fact]
    public void Identical_Reports_Are_Identical()
    {
        MetricSummaryRow[] metrics =
        [
            new("waf-blocks", "{}", 3),
            new("url-count", "{\"status\":\"2xx\"}", 120),
        ];
        AuditScoreRow[] scores =
        [
            new("overall", 87.5, "{\"findings\":4}"),
            new("indexability", 92, "{\"findings\":1}"),
        ];

        ReportParityResult result = ProviderSwitchOrchestrator.CompareReports(metrics, metrics, scores, scores);

        Assert.True(result.Identical);
        Assert.Empty(result.Differences);
        Assert.Equal(2, result.MetricsCompared);
        Assert.Equal(2, result.ScoresCompared);
    }

    /// <summary>Parity comparison is order-independent.</summary>
    [Fact]
    public void Parity_Is_Order_Independent()
    {
        MetricSummaryRow[] source =
        [
            new("a", "{}", 1),
            new("b", "{}", 2),
        ];
        MetricSummaryRow[] target =
        [
            new("b", "{}", 2),
            new("a", "{}", 1),
        ];

        ReportParityResult result = ProviderSwitchOrchestrator.CompareReports(source, target, [], []);

        Assert.True(result.Identical);
    }

    /// <summary>Missing, extra, and mismatched rows are reported as differences.</summary>
    [Fact]
    public void Differences_Are_Enumerated()
    {
        MetricSummaryRow[] sourceMetrics =
        [
            new("missing-on-target", "{}", 1),
            new("value-mismatch", "{}", 1),
        ];
        MetricSummaryRow[] targetMetrics =
        [
            new("value-mismatch", "{}", 2),
            new("extra-on-target", "{}", 5),
        ];
        AuditScoreRow[] sourceScores = [new("overall", 80, "{}")];
        AuditScoreRow[] targetScores = [new("overall", 81, "{}")];

        ReportParityResult result = ProviderSwitchOrchestrator.CompareReports(sourceMetrics, targetMetrics, sourceScores, targetScores);

        Assert.False(result.Identical);
        Assert.Equal(4, result.Differences.Count);
        Assert.Contains(result.Differences, d => d.Contains("missing-on-target", StringComparison.Ordinal));
        Assert.Contains(result.Differences, d => d.Contains("extra-on-target", StringComparison.Ordinal));
        Assert.Contains(result.Differences, d => d.Contains("value-mismatch", StringComparison.Ordinal));
        Assert.Contains(result.Differences, d => d.Contains("overall", StringComparison.Ordinal));
    }

    /// <summary>Duplicate logical keys on either side break parity.</summary>
    [Fact]
    public void Duplicate_Rows_Break_Parity()
    {
        MetricSummaryRow[] duplicated =
        [
            new("a", "{}", 1),
            new("a", "{}", 1),
        ];

        ReportParityResult result = ProviderSwitchOrchestrator.CompareReports(duplicated, [new MetricSummaryRow("a", "{}", 1)], [], []);

        Assert.False(result.Identical);
        Assert.Contains(result.Differences, d => d.Contains("Duplicate", StringComparison.Ordinal));
    }

    /// <summary>Report rows are read with provider-neutral SQL and mapped to the parity DTOs.</summary>
    [Fact]
    public async Task Report_Rows_Are_Read_With_Provider_Neutral_Sql()
    {
        using var connection = new FakeDbConnection();
        connection.MetricRows =
        [
            new("waf-blocks", "{}", 3),
            new("url-count", "{\"status\":\"2xx\"}", 120),
        ];
        // Raw audit_scores rows in the locked catalog shape (DEFECT D10):
        // nullable integer category + the four contributing count columns.
        connection.ScoreRows =
        [
            new FakeScoreRawRow((int)AuditCategory.Content, 87.5, 1, 2, 3, 4),
            new FakeScoreRawRow(null, 90, 1, 2, 3, 4),
        ];

        (IReadOnlyList<MetricSummaryRow> metrics, IReadOnlyList<AuditScoreRow> scores) =
            await ProviderSwitchOrchestrator.ReadReportRowsAsync(connection, auditId: 42);

        Assert.Equal(connection.MetricRows, metrics);
        Assert.Equal(2, scores.Count);
        Assert.Equal(new AuditScoreRow("Content", 87.5, "{\"errorCount\":1,\"warningCount\":2,\"noticeCount\":3,\"passCount\":4}"), scores[0]);
        Assert.Equal(new AuditScoreRow("overall", 90, "{\"errorCount\":1,\"warningCount\":2,\"noticeCount\":3,\"passCount\":4}"), scores[1]);
        Assert.Equal(2, connection.CommandTexts.Count);
        Assert.Contains("metric_summaries", connection.CommandTexts[0], StringComparison.Ordinal);
        Assert.Contains("audit_scores", connection.CommandTexts[1], StringComparison.Ordinal);
        // D10: the query reads the real count columns, never a nonexistent JSON column.
        Assert.Contains("error_count", connection.CommandTexts[1], StringComparison.Ordinal);
        Assert.Contains("warning_count", connection.CommandTexts[1], StringComparison.Ordinal);
        Assert.Contains("notice_count", connection.CommandTexts[1], StringComparison.Ordinal);
        Assert.Contains("pass_count", connection.CommandTexts[1], StringComparison.Ordinal);
        Assert.DoesNotContain("contributing_counts_json", connection.CommandTexts[1], StringComparison.Ordinal);
        Assert.All(connection.CommandTexts, sql => Assert.Contains("@auditId", sql, StringComparison.Ordinal));
    }

    /// <summary>Rows read from two providers compare through the same parity contract.</summary>
    [Fact]
    public async Task Read_Then_Compare_Proves_Identical_Output()
    {
        using var source = new FakeDbConnection();
        using var target = new FakeDbConnection();
        source.MetricRows = [new("waf-blocks", "{}", 3)];
        target.MetricRows = [new("waf-blocks", "{}", 3)];
        source.ScoreRows =
        [
            new FakeScoreRawRow((int)AuditCategory.Content, 87.5, 1, 2, 3, 4),
            new FakeScoreRawRow(null, 90, 1, 2, 3, 4),
        ];
        target.ScoreRows =
        [
            new FakeScoreRawRow((int)AuditCategory.Content, 87.5, 1, 2, 3, 4),
            new FakeScoreRawRow(null, 90, 1, 2, 3, 4),
        ];

        (IReadOnlyList<MetricSummaryRow> sourceMetrics, IReadOnlyList<AuditScoreRow> sourceScores) =
            await ProviderSwitchOrchestrator.ReadReportRowsAsync(source, auditId: 42);
        (IReadOnlyList<MetricSummaryRow> targetMetrics, IReadOnlyList<AuditScoreRow> targetScores) =
            await ProviderSwitchOrchestrator.ReadReportRowsAsync(target, auditId: 42);

        ReportParityResult result = ProviderSwitchOrchestrator.CompareReports(sourceMetrics, targetMetrics, sourceScores, targetScores);

        Assert.True(result.Identical);
    }
}
