using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrawlForge.Tools.Governance;

/// <summary>
/// CLI entry point. Usage:
///   governance-analyzer --root &lt;dir&gt; [--out &lt;file&gt;] [--fail-on-violation]
///                       [--boundary-config &lt;file&gt;] [--rules NR-01,NR-E7,...]
///
/// The machine-readable JSON report goes to stdout (or --out). A short human summary goes
/// to stderr so stdout stays pipe-safe. Exit codes: 0 = pass (or violations without
/// --fail-on-violation), 1 = violations with --fail-on-violation, 2 = usage/IO error.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static int Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine(CliOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.Out.WriteLine(CliOptions.Usage);
            return 0;
        }

        GovernanceReport report;
        try
        {
            var engine = new GovernanceAnalyzerEngine();
            report = engine.Analyze(new GovernanceAnalysisRequest(
                options.Root!,
                options.BoundaryConfigPath,
                options.RuleFilter));
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        var json = JsonSerializer.Serialize(report, ReportJsonOptions);
        if (options.OutputPath is { } outputPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(outputPath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"error: cannot write report '{outputPath}': {ex.Message}");
                return 2;
            }
        }
        else
        {
            Console.Out.WriteLine(json);
        }

        Console.Error.WriteLine(
            $"governance-analyzer: {report.FilesScanned} files scanned, "
            + $"{report.Violations.Count} violation(s) across {report.ViolationsByRule.Count} rule(s); "
            + (report.Passed ? "PASS" : "FAIL"));

        return report.Passed || !options.FailOnViolation ? 0 : 1;
    }
}

/// <summary>Parsed command-line options.</summary>
public sealed record CliOptions(
    string? Root,
    string? OutputPath,
    bool FailOnViolation,
    string? BoundaryConfigPath,
    IReadOnlyList<string>? RuleFilter,
    bool ShowHelp)
{
    public const string Usage =
        """
        CrawlForge Governance Static Analyzer (clean-room negative-requirement verifier)

        Usage:
          governance-analyzer --root <dir> [options]

        Options:
          --root <dir>             Source root to scan (required).
          --out <file>             Write the JSON report to a file instead of stdout.
          --fail-on-violation      Exit 1 when any violation is found (CI gate mode).
          --boundary-config <file> Override the approved network-boundary allowlist JSON.
          --rules <id,id,...>      Only report the listed rule ids (e.g. NR-01,NR-E7,NET-02).
          --help                   Show this help.

        Rules: NR-01..NR-08 (FS-CRAWL-001 §3 rows 1-8), NR-E1..NR-E9 (FS-CRAWL-002 §1.6),
        NET-01 (non-loopback listeners), NET-02 (outbound boundary), ID-01 (identity dimensions).

        Exit codes: 0 pass, 1 violations with --fail-on-violation, 2 usage/IO error.
        """;

    public static CliOptions Parse(string[] args)
    {
        string? root = null;
        string? output = null;
        string? boundaryConfig = null;
        IReadOnlyList<string>? ruleFilter = null;
        var failOnViolation = false;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                case "--fail-on-violation":
                    failOnViolation = true;
                    break;
                case "--root":
                    root = NextValue(args, ref i, arg);
                    break;
                case "--out":
                    output = NextValue(args, ref i, arg);
                    break;
                case "--boundary-config":
                    boundaryConfig = NextValue(args, ref i, arg);
                    break;
                case "--rules":
                    var value = NextValue(args, ref i, arg);
                    ruleFilter = value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
        }

        if (!showHelp && string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("--root <dir> is required.");
        }

        return new CliOptions(root, output, failOnViolation, boundaryConfig, ruleFilter, showHelp);
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Option '{option}' requires a value.");
        }

        index++;
        return args[index];
    }
}
