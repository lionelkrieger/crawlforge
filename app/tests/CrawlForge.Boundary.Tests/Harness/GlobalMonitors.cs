using System.Runtime.CompilerServices;

namespace CrawlForge.Boundary.Tests.Harness;

/// <summary>
/// Process-lifetime monitors started at module initialization — the earliest
/// point the test assembly controls, before any product assembly is loaded.
/// Identity-store attribution: anything already loaded at this point predates
/// all product code (the test platform loaded it, not CrawlForge).
/// </summary>
internal static class GlobalMonitors
{
    internal static AssemblyLoadMonitor AssemblyMonitor { get; private set; } = null!;

    [ModuleInitializer]
    internal static void Initialize()
    {
        AssemblyMonitor = new AssemblyLoadMonitor();
    }
}
