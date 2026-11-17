namespace Fixture.E6;

/// <summary>The plugin system is free, local, and ungated.</summary>
public sealed class FreePluginCatalog
{
    public IReadOnlyList<string> ListInstalled() => [];

    public bool IsCompatible(string manifestVersion) => manifestVersion == "1";
}
