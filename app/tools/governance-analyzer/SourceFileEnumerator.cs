namespace CrawlForge.Tools.Governance;

/// <summary>One scannable source file.</summary>
internal sealed record SourceFile(string AbsolutePath, string RelativePath, SourceFileKind Kind);

internal enum SourceFileKind
{
    CSharp,
    TypeScript,
    JavaScript,
    Json,
    CsProj,
}

/// <summary>
/// Deterministic source enumeration under the scan root. Generated/build artifacts
/// (obj, bin, node_modules, dist, TestResults, ...) and dependency lockfiles are
/// excluded, as are the analyzer's own rule data and ALL verification code (tests/):
/// fixtures contain detection signatures and negative-assertion canaries, not product
/// behavior (WP-07-G1: generalizes the original tests/governance self-exclusion).
/// Rules remain fully enforced on every product surface (src/, assets/, packaging/,
/// tools/, root configs).
/// </summary>
internal static class SourceFileEnumerator
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", "node_modules", ".git", ".vs", ".idea", "TestResults", "artifacts",
        "dist", "build", "packages", "coverage", ".next", ".vite",
    };

    /// <summary>
    /// Dependency lockfiles are build metadata, not product code (WP-07-G1):
    /// npm package "license" fields are registry metadata, not product licensing.
    /// </summary>
    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "npm-shrinkwrap.json", "packages.lock.json", "yarn.lock", "pnpm-lock.yaml",
    };

    private static readonly string[] ExcludedFileSuffixes =
    [
        ".g.cs", ".g.i.cs", ".Designer.cs", ".AssemblyInfo.cs", ".AssemblyAttributes.cs",
        ".Reference.cs", ".min.js", ".generated.cs",
    ];

    public static IReadOnlyList<SourceFile> Enumerate(string root)
    {
        var rootFull = Path.GetFullPath(root);
        if (!Directory.Exists(rootFull))
        {
            throw new DirectoryNotFoundException($"Scan root does not exist: '{rootFull}'.");
        }

        var files = new List<SourceFile>();
        Walk(rootFull, rootFull, files);
        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return files;
    }

    private static void Walk(string root, string directory, List<SourceFile> files)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (ExcludedFileNames.Contains(name))
            {
                continue;
            }

            if (ExcludedFileSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var kind = Classify(file);
            if (kind is null)
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsVerificationOrToolingPath(relative))
            {
                continue;
            }

            files.Add(new SourceFile(file, relative, kind.Value));
        }

        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            var dirName = Path.GetFileName(sub);
            if (ExcludedDirectories.Contains(dirName))
            {
                continue;
            }

            Walk(root, sub, files);
        }
    }

    /// <summary>
    /// Verification code is never product surface (WP-07-G1): ALL test projects under
    /// tests/ (negative-assertion guardrails, loopback harnesses, planted canaries) and
    /// the analyzer's own sources (rule signature data) are excluded when a broader root
    /// is scanned. Segment-based so both app-root and repo-root scans behave identically.
    /// </summary>
    private static bool IsVerificationOrToolingPath(string relativePath)
    {
        var normalized = relativePath.ToLowerInvariant();
        return normalized.StartsWith("tests/", StringComparison.Ordinal)
            || normalized.Contains("/tests/", StringComparison.Ordinal)
            || normalized.StartsWith("tools/governance-analyzer/", StringComparison.Ordinal)
            || normalized.Contains("/tools/governance-analyzer/", StringComparison.Ordinal);
    }

    private static SourceFileKind? Classify(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFileKind.CSharp;
        }

        if (extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFileKind.TypeScript;
        }

        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFileKind.JavaScript;
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFileKind.Json;
        }

        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFileKind.CsProj;
        }

        return null;
    }
}
