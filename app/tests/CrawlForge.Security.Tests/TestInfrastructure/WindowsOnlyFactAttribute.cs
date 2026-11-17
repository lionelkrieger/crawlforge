using Xunit;

namespace CrawlForge.Security.Tests.TestInfrastructure;

/// <summary>
/// Fact that runs only on Windows: native minidump capture uses the Windows
/// dbghelp API (spec §13: Windows primary; other platforms return null).
/// </summary>
internal sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Native minidump capture requires Windows (dbghelp).";
        }
    }
}