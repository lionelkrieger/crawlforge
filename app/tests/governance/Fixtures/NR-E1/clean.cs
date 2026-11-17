using System.Reflection;

namespace Fixture.E1;

/// <summary>Local plugin directory scan only; plugins are user-authored local artifacts.</summary>
public sealed class LocalPluginScanner
{
    public IReadOnlyList<string> ScanLocalDirectory(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "plugin.json", SearchOption.AllDirectories)
            : [];

    public Assembly LoadLocalModule(string path) => Assembly.LoadFrom(path);
}
