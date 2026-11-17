using System.Runtime.CompilerServices;

namespace CrawlForge.Security.Tests.TestInfrastructure;

/// <summary>
/// Unique per-test local directory under the system temp path, removed on
/// disposal. Keeps every suite hermetic: no test touches real user data.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory([CallerMemberName] string? prefix = null)
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "cf-wp-00-04-" + (prefix ?? "test") + "-" + Guid.NewGuid().ToString("N"));
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