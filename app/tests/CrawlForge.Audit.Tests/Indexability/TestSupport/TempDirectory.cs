using System.Runtime.CompilerServices;

namespace CrawlForge.Audit.Tests.Indexability.TestSupport;

/// <summary>
/// Unique per-test local directory, removed on disposal. Keeps suites hermetic.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory([CallerMemberName] string? prefix = null)
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "cf-wp-03-02-" + (prefix ?? "test") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a transient test directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a transient test directory.
        }
    }
}