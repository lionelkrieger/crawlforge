namespace CrawlForge.Scale.Benchmarks.Support;

/// <summary>
/// Deterministic artifact locations for the scale workpiece. Everything lives
/// under the owned Scale.Benchmarks project directory so the published report,
/// result fragments, and the seeded database are durable, inspectable artifacts.
/// The browser benchmark (tests/frontend/grid/bench) reads the seed manifest to
/// reuse the same seeded data directory.
/// </summary>
public static class ScalePaths
{
    /// <summary>The Scale.Benchmarks project root (resolved from the build output).</summary>
    public static string ProjectRoot { get; } = ResolveProjectRoot();

    /// <summary>Published artifacts root (report + result fragments).</summary>
    public static string ArtifactsRoot => Path.Combine(ProjectRoot, "artifacts");

    /// <summary>Data directory holding the seeded 500k-row database.</summary>
    public static string SeededDataDirectory => Path.Combine(ArtifactsRoot, "seeded-data");

    /// <summary>Per-measurement JSON result fragments.</summary>
    public static string ResultsDirectory => Path.Combine(ArtifactsRoot, "results");

    /// <summary>Seed manifest consumed by the browser benchmark.</summary>
    public static string SeedManifestPath => Path.Combine(ArtifactsRoot, "seed-manifest.json");

    /// <summary>Published benchmark report (markdown).</summary>
    public static string ReportPath => Path.Combine(ArtifactsRoot, "benchmark-report.md");

    /// <summary>Ensures the artifact directories exist.</summary>
    public static void EnsureArtifacts()
    {
        Directory.CreateDirectory(ArtifactsRoot);
        Directory.CreateDirectory(ResultsDirectory);
    }

    private static string ResolveProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrawlForge.Scale.Benchmarks.csproj")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the Scale.Benchmarks project root from " + AppContext.BaseDirectory);
    }
}
