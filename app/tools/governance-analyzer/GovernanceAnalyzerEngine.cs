namespace CrawlForge.Tools.Governance;

/// <summary>
/// Governance static analyzer engine (WP-00-05): scans source under a root directory and
/// enforces the 17 clean-room negative requirements plus the network-boundary and
/// identity-dimension lints. Deterministic, fast (syntax-only), and independent of any
/// compiled state — safe to run on every workpiece while siblings are mid-build.
/// </summary>
public sealed class GovernanceAnalyzerEngine : IGovernanceAnalyzer
{
    /// <summary>File-name shapes that indicate license cache artifacts (NR-02).</summary>
    private static readonly string[] LicenseCacheExtensions = [".lic", ".cache", ".json", ".xml", ".dat", ".txt"];

    public GovernanceReport Analyze(GovernanceAnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RootDirectory))
        {
            throw new ArgumentException("Root directory must be provided.", nameof(request));
        }

        var root = Path.GetFullPath(request.RootDirectory);
        var boundary = NetworkBoundaryAllowlist.Load(request.BoundaryConfigPath);
        var files = SourceFileEnumerator.Enumerate(root);
        var violations = new List<GovernanceViolation>();

        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file.AbsolutePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Cannot read source file '{file.AbsolutePath}': {ex.Message}", ex);
            }

            EvaluateFileName(file, violations);

            switch (file.Kind)
            {
                case SourceFileKind.CSharp:
                    violations.AddRange(CSharpDetectors.Evaluate(file, CSharpFactCollector.Collect(text), boundary));
                    break;
                case SourceFileKind.TypeScript:
                case SourceFileKind.JavaScript:
                    violations.AddRange(TextDetectors.Evaluate(file, TextScanner.Scan(text), boundary));
                    break;
                case SourceFileKind.Json:
                    violations.AddRange(PackageDetectors.EvaluateJson(file, text));
                    break;
                case SourceFileKind.CsProj:
                    violations.AddRange(PackageDetectors.EvaluateCsProj(file, text));
                    break;
                default:
                    break;
            }
        }

        if (request.RuleFilter is { Count: > 0 } filter)
        {
            var allowed = new HashSet<string>(filter, StringComparer.Ordinal);
            violations.RemoveAll(v => !allowed.Contains(v.RuleId));
        }

        var rules = RuleCatalog.All;
        if (request.RuleFilter is { Count: > 0 } ruleFilter)
        {
            var allowed = new HashSet<string>(ruleFilter, StringComparer.Ordinal);
            rules = rules.Where(r => allowed.Contains(r.Id)).ToList();
        }

        return GovernanceReport.Create(root, files.Count, rules, violations);
    }

    private static void EvaluateFileName(SourceFile file, List<GovernanceViolation> violations)
    {
        var name = Path.GetFileNameWithoutExtension(file.AbsolutePath);
        var extension = Path.GetExtension(file.AbsolutePath);
        var isLicenseCache = extension.Equals(".lic", StringComparison.OrdinalIgnoreCase)
            || (LicenseCacheExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                && (name.Contains("license", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("licence", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("entitlement", StringComparison.OrdinalIgnoreCase)));
        if (isLicenseCache)
        {
            violations.Add(DetectionHelpers.Violation(
                RuleCatalog.Nr02, file, 1, 1,
                file.RelativePath,
                "License/entitlement cache artifact is prohibited."));
        }
    }
}
