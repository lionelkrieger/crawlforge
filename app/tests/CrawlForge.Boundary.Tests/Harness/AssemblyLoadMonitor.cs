using System.Collections.Concurrent;
using System.Reflection;

namespace CrawlForge.Boundary.Tests.Harness;

/// <summary>
/// Process-wide assembly-load monitor (criterion #2 harness-level evidence):
/// records every assembly loaded into the backend process across the full
/// lifecycle. OS identity stores are reachable only through a small, fixed set
/// of SDK assemblies (registry, WMI, directory services); if none of them is
/// ever loaded, no identity-store read can have occurred through managed code.
/// Combined with the NR-01 static scan (no identity APIs in product sources,
/// no P/Invoke identity imports), this forms the layered no-identity proof.
/// </summary>
public sealed class AssemblyLoadMonitor : IDisposable
{
    /// <summary>Assemblies whose sole purpose is OS identity-store access.</summary>
    public static readonly string[] IdentityStoreAssemblies =
    [
        "System.Management",
        "Microsoft.Win32.Registry",
        "System.DirectoryServices",
        "System.DirectoryServices.AccountManagement",
        "System.DirectoryServices.Protocols",
        "System.Management.Automation",
    ];

    private readonly ConcurrentDictionary<string, byte> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _watchedLoadStacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly AssemblyLoadEventHandler _handler;

    /// <summary>Starts monitoring, snapshotting already-loaded assemblies.</summary>
    public AssemblyLoadMonitor()
    {
        var snapshot = AppDomain.CurrentDomain.GetAssemblies();
        ProductAssembliesAtStart = snapshot
            .Select(a => a.GetName().Name ?? "?")
            .Where(n => n.StartsWith("CrawlForge.", StringComparison.Ordinal)
                && !n.StartsWith("CrawlForge.Boundary.Tests", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var assembly in snapshot)
        {
            var name = assembly.GetName().Name ?? "?";
            _loaded.TryAdd(name, 0);
            if (IdentityStoreAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                _watchedLoadStacks.TryAdd(name, "(already loaded at monitor start)");
            }
        }

        _handler = (_, args) =>
        {
            var name = args.LoadedAssembly.GetName().Name ?? "?";
            _loaded.TryAdd(name, 0);
            if (IdentityStoreAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                // Capture WHO triggered the load: framework-internal loads (for
                // example the BCL timezone subsystem) are attributable evidence,
                // product-code loads would be a boundary defect.
                _watchedLoadStacks.TryAdd(name, Environment.StackTrace);
            }
        };
        AppDomain.CurrentDomain.AssemblyLoad += _handler;
    }

    /// <summary>Every assembly observed loaded in this process.</summary>
    public IReadOnlyList<string> LoadedAssemblies => _loaded.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>Identity-store assemblies observed loaded.</summary>
    public IReadOnlyList<string> IdentityStoreAssembliesLoaded =>
        _loaded.Keys.Where(k => IdentityStoreAssemblies.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Captured load stacks for watched assemblies (attribution evidence).</summary>
    public IReadOnlyDictionary<string, string> WatchedLoadStacks => _watchedLoadStacks;

    /// <summary>
    /// CrawlForge product assemblies already loaded when monitoring began. Zero
    /// means any pre-loaded identity assembly predates all product code.
    /// </summary>
    public IReadOnlyList<string> ProductAssembliesAtStart { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        AppDomain.CurrentDomain.AssemblyLoad -= _handler;
    }
}
