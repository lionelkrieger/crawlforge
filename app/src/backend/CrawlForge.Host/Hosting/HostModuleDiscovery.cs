using System.Reflection;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Host.Hosting;

/// <summary>
/// Discovers <see cref="IHostModule"/> implementations in loaded assemblies and in
/// CrawlForge.* assemblies sitting next to the host. Discovery is local-only: no remote
/// code loading and no download paths of any kind (clean-room guardrails E1/E4).
/// </summary>
public static class HostModuleDiscovery
{
    public static IReadOnlyList<IHostModule> Discover(ILogger? logger = null)
    {
        var assemblies = new List<Assembly>(AppDomain.CurrentDomain.GetAssemblies());

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "CrawlForge.*.dll"))
        {
            try
            {
                var name = AssemblyName.GetAssemblyName(path);
                if (assemblies.All(a => !string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    assemblies.Add(Assembly.Load(name));
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
            {
                logger?.LogWarning(ex, "Skipping unreadable module assembly {Path}.", path);
            }
        }

        var modules = new List<IHostModule>();
        foreach (var assembly in assemblies)
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
                    && typeof(IHostModule).IsAssignableFrom(type)
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    modules.Add((IHostModule)Activator.CreateInstance(type)!);
                }
            }
        }

        return modules
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
