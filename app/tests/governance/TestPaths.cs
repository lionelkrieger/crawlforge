namespace CrawlForge.Tests.Governance;

/// <summary>
/// Resolves fixture and application roots relative to the test assembly, independent of
/// the CF_AGENT_ID-scoped output path (bin\&lt;agent&gt;\Debug\net10.0 vs bin\Debug\net10.0).
/// </summary>
internal static class TestPaths
{
    public static string ProjectDirectory { get; } = FindAncestorContaining(AppContext.BaseDirectory, "Fixtures");

    public static string FixturesRoot => Path.Combine(ProjectDirectory, "Fixtures");

    public static string AppRoot => Directory.GetParent(ProjectDirectory)!.Parent!.FullName;

    public static string SrcRoot => Path.Combine(AppRoot, "src");

    /// <summary>Scratch space for test-generated data; stays inside owned_paths.</summary>
    public static string CreateScratchDirectory()
    {
        var path = Path.Combine(ProjectDirectory, ".tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindAncestorContaining(string start, string childDirectoryName)
    {
        var directory = new DirectoryInfo(start);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, childDirectoryName)))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Cannot locate a '{childDirectoryName}' directory above '{start}'.");
    }
}
