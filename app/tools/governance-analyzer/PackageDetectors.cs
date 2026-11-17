using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrawlForge.Tools.Governance;

/// <summary>
/// Dependency-surface detection: banned NuGet PackageReference entries in .csproj files and
/// banned npm dependency keys in package.json. Build scripts and tooling settings are NOT
/// inspected (build-time tooling is out of scope); only declared runtime dependencies are.
/// Generic JSON files are scanned for vendor endpoints and identity-shaped keys.
/// </summary>
internal static class PackageDetectors
{
    private static readonly Regex PackageReferenceRegex = new(
        @"<PackageReference\s+Include\s*=\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex JsonPairRegex = new(
        @"""([A-Za-z0-9_\-]+)""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    private static readonly string[] DependencySections =
    [
        "dependencies", "devDependencies", "optionalDependencies", "peerDependencies",
    ];

    /// <summary>
    /// Standard package.json metadata keys. The npm "license" field is SPDX metadata about
    /// the package itself, not a licensing client (NR-02 targets license state machines).
    /// </summary>
    private static readonly HashSet<string> PackageJsonMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "version", "license", "licenses", "description", "author", "contributors", "repository",
        "keywords", "private", "type", "main", "module", "exports", "scripts", "engines", "os", "cpu",
        "sideEffects", "files", "workspaces", "packageManager", "bugs", "homepage", "funding", "types",
        "typings", "bin", "man", "directories", "dist", "publishConfig", "config", "browser", "imports",
    };

    public static List<GovernanceViolation> EvaluateCsProj(SourceFile file, string text)
    {
        var violations = new List<GovernanceViolation>();
        foreach (Match match in PackageReferenceRegex.Matches(text))
        {
            var package = match.Groups[1].Value;
            var (line, column) = LineColumn(text, match.Index);
            foreach (var (name, ruleId, isPrefix) in PatternLists.BannedNuGetPackages)
            {
                var matches = isPrefix
                    ? package.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                        && (package.Length == name.Length || package[name.Length] == '.')
                    : string.Equals(package, name, StringComparison.OrdinalIgnoreCase);
                if (matches)
                {
                    violations.Add(DetectionHelpers.Violation(
                        ruleId, file, line, column,
                        $"PackageReference {package}",
                        $"NuGet package '{package}' is a prohibited SDK surface for {ruleId}."));
                }
            }
        }

        return violations;
    }

    public static List<GovernanceViolation> EvaluateJson(SourceFile file, string text)
    {
        var violations = new List<GovernanceViolation>();
        var fileName = Path.GetFileName(file.AbsolutePath);
        var isPackageJson = string.Equals(fileName, "package.json", StringComparison.OrdinalIgnoreCase);

        if (isPackageJson)
        {
            EvaluatePackageJson(file, text, violations);
        }

        foreach (Match match in JsonPairRegex.Matches(text))
        {
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value;
            var (line, column) = LineColumn(text, match.Index);

            if (isPackageJson && PackageJsonMetadataKeys.Contains(key))
            {
                continue;
            }

            foreach (var (ruleId, patterns) in PatternLists.IdentifierPatterns)
            {
                if (IdentifierTokens.MatchesAnyPattern(IdentifierTokens.Tokenize(key), patterns))
                {
                    violations.Add(DetectionHelpers.Violation(
                        ruleId, file, line, column,
                        $"{key}: {value}",
                        $"Configuration key '{key}' matches a prohibited concept for {ruleId}."));
                }
            }

            if (EndpointClassifier.TryParse(value, out var endpoint) && endpoint is not null)
            {
                foreach (var vendor in PatternLists.CloudLlmVendorHosts)
                {
                    if (EndpointClassifier.HostMatches(endpoint.Host, vendor))
                    {
                        violations.Add(DetectionHelpers.Violation(
                            RuleCatalog.NrE7, file, line, column,
                            value,
                            $"Cloud LLM/embedding/vector vendor endpoint '{vendor}' is prohibited."));
                    }
                }
            }

            if (DetectionHelpers.ContainsAny(value, PatternLists.VendorHeaderLiterals))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr02, file, line, column,
                    value,
                    "Identity-bearing vendor header is prohibited."));
            }

            if (DetectionHelpers.ContainsAny(key, PatternLists.MachineIdentityLiterals)
                || DetectionHelpers.ContainsAny(value, PatternLists.MachineIdentityLiterals))
            {
                violations.Add(DetectionHelpers.Violation(
                    RuleCatalog.Nr01, file, line, column,
                    $"{key}: {value}",
                    "Configuration references an OS identity store or hardware identifier."));
            }
        }

        return violations
            .GroupBy(v => (v.RuleId, v.Line, v.Column))
            .Select(g => g.First())
            .ToList();
    }

    private static void EvaluatePackageJson(SourceFile file, string text, List<GovernanceViolation> violations)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var section in DependencySections)
            {
                if (!document.RootElement.TryGetProperty(section, out var dependencies)
                    || dependencies.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var dependency in dependencies.EnumerateObject())
                {
                    foreach (var (name, ruleId, isPrefix) in PatternLists.BannedNpmPackages)
                    {
                        var matches = isPrefix
                            ? dependency.Name.StartsWith(name, StringComparison.Ordinal)
                            : string.Equals(dependency.Name, name, StringComparison.Ordinal);
                        if (!matches)
                        {
                            continue;
                        }

                        var index = text.IndexOf($"\"{dependency.Name}\"", StringComparison.Ordinal);
                        var (line, column) = index < 0 ? (1, 1) : LineColumn(text, index);
                        violations.Add(DetectionHelpers.Violation(
                            ruleId, file, line, column,
                            $"{section}: {dependency.Name}",
                            $"npm package '{dependency.Name}' is a prohibited SDK surface for {ruleId}."));
                    }
                }
            }
        }
    }

    private static (int Line, int Column) LineColumn(string text, int index)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }
}
