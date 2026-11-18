namespace CrawlForge.ContractConformance.Tests.Harness;

/// <summary>
/// Locates the repository and the read-only contract sources the harness
/// asserts against. Discovery walks up from the test assembly output directory
/// (agent-scoped bin paths vary with CF_AGENT_ID) until the solution file is
/// found, so the harness works under any agent's build isolation.
/// </summary>
public static class RepoPaths
{
    /// <summary>Repository root (the directory containing app/CrawlForge.sln's parent).</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Application root (E:\Crawlforge\app).</summary>
    public static string AppRoot => Path.Combine(RepoRoot, "app");

    /// <summary>Backend Host project directory (launch-crawlforge.ps1 step-2 target).</summary>
    public static string HostProjectDir => Path.Combine(AppRoot, "src", "backend", "CrawlForge.Host");

    /// <summary>This test project's source directory (owned path; captures land here).</summary>
    public static string ProjectDir => Path.Combine(AppRoot, "tests", "CrawlForge.ContractConformance.Tests");

    /// <summary>Captured wire payloads directory (committed evidence).</summary>
    public static string CapturedDir => Path.Combine(ProjectDir, "captured");

    /// <summary>Committed payload inventory file.</summary>
    public static string InventoryFile => Path.Combine(ProjectDir, "inventory", "payload-inventory.json");

    /// <summary>Frozen frontend REST route catalog (read-only reference).</summary>
    public static string FrontendRoutesTs => Path.Combine(AppRoot, "src", "frontend", "src", "contracts", "api", "routes.ts");

    /// <summary>Frozen frontend hub event contract (read-only reference).</summary>
    public static string FrontendHubEventsTs => Path.Combine(AppRoot, "src", "frontend", "src", "contracts", "hub", "events.ts");

    /// <summary>Frontend hub payload guards (read-only reference).</summary>
    public static string FrontendHubGuardsTs => Path.Combine(AppRoot, "src", "frontend", "src", "contracts", "hub", "guards.ts");

    /// <summary>Electron preload bridge contract (read-only reference).</summary>
    public static string ElectronBridgeTs => Path.Combine(AppRoot, "src", "electron", "src", "preload", "bridge.ts");

    /// <summary>Electron sandboxed preload (read-only reference).</summary>
    public static string ElectronPreloadTs => Path.Combine(AppRoot, "src", "electron", "src", "preload.ts");

    /// <summary>Electron main process (wireIpc; read-only reference).</summary>
    public static string ElectronMainTs => Path.Combine(AppRoot, "src", "electron", "src", "main.ts");

    /// <summary>Fixture site server script (read-only; started on port 47312).</summary>
    public static string FixtureServerMjs => Path.Combine(AppRoot, "tests", "fixture-site", "server.mjs");

    /// <summary>
    /// The agent-scoped (or canonical) Host DLL produced by the launch script's
    /// step-2 build command. CF_AGENT_ID unset = canonical bin\Debug (wave gates).
    /// </summary>
    public static string HostDll
    {
        get
        {
            var agent = Environment.GetEnvironmentVariable("CF_AGENT_ID");
            var binRoot = string.IsNullOrEmpty(agent)
                ? Path.Combine(HostProjectDir, "bin")
                : Path.Combine(HostProjectDir, "bin", agent);
            return Path.Combine(binRoot, "Debug", "net10.0", "CrawlForge.Host.dll");
        }
    }

    /// <summary>Scratch root OUTSIDE the repository (data safety; kept for lead review).</summary>
    public static string ScratchRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrawlForge-conformance-wp0803");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "app", "CrawlForge.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (app/CrawlForge.sln) above '{AppContext.BaseDirectory}'.");
    }
}
