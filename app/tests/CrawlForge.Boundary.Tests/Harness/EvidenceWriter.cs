using System.Text;

namespace CrawlForge.Boundary.Tests.Harness;

/// <summary>
/// Durable evidence writer (WP-07-02): writes human- and machine-readable
/// evidence artifacts under <c>app/tools/network-audit/evidence/</c> in formats
/// the governance scanner does not treat as source (.md / .csv). Machine-readable
/// governance scan JSONs live with the analyzer test suite (tests/governance),
/// the established location for verification reports.
/// </summary>
public static class EvidenceWriter
{
    private static readonly Lazy<string> AppRootLazy = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && directory is not null; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrawlForge.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Cannot locate the app root (CrawlForge.sln) for evidence output.");
    });

    /// <summary>Absolute path of the application root (contains CrawlForge.sln).</summary>
    public static string AppRoot => AppRootLazy.Value;

    private static readonly Lazy<string> EvidenceRoot = new(() =>
    {
        var root = Path.Combine(AppRoot, "tools", "network-audit", "evidence");
        Directory.CreateDirectory(root);
        return root;
    });

    /// <summary>Absolute path of the durable evidence directory.</summary>
    public static string Root => EvidenceRoot.Value;

    /// <summary>Writes a Markdown evidence artifact (overwrites).</summary>
    /// <param name="fileName">File name within the evidence directory.</param>
    /// <param name="content">Markdown content.</param>
    /// <returns>The absolute artifact path.</returns>
    public static string WriteMarkdown(string fileName, string content)
    {
        var path = Path.Combine(Root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Writes a CSV evidence artifact (overwrites).</summary>
    /// <param name="fileName">File name within the evidence directory.</param>
    /// <param name="header">CSV header line.</param>
    /// <param name="rows">CSV data rows.</param>
    /// <returns>The absolute artifact path.</returns>
    public static string WriteCsv(string fileName, string header, IEnumerable<string> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(header);
        foreach (var row in rows)
        {
            builder.AppendLine(row);
        }

        var path = Path.Combine(Root, fileName);
        File.WriteAllText(path, builder.ToString());
        return path;
    }
}
