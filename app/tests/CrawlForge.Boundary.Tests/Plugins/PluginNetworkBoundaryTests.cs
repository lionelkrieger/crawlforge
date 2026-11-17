using CrawlForge.Boundary.Tests.Harness;
using Xunit;

namespace CrawlForge.Boundary.Tests.Plugins;

/// <summary>
/// Acceptance criterion #39 (plugin network boundary): zero plugin network
/// traffic by default; with an explicit user grant, requests reach ONLY user
/// destination rules. The enforcement machinery is the renderer's sandboxed
/// frame (opaque origin + strict CSP) and the host-side grant guard; this suite
/// pins the load-bearing source invariants, and the frontend vitest plugin
/// suites (networkGrantGuard / sandboxFrame / sandboxBridge / pluginRegistry)
/// provide the behavioral proof (run separately; output captured as evidence).
/// </summary>
public sealed class PluginNetworkBoundaryTests
{
    [Fact]
    public void SandboxFrame_DefaultsToZeroNetwork_AndOpaqueOrigin()
    {
        var source = ReadFrontendSource("plugins/sandboxFrame.ts");

        // Only allow-scripts: opaque origin, no same-origin, no forms/popups/navigation.
        Assert.Contains("export const SANDBOX_ATTRIBUTE_VALUE = 'allow-scripts';", source);
        Assert.Contains("frame.setAttribute('sandbox', SANDBOX_ATTRIBUTE_VALUE)", source);

        // Strict CSP: default-src 'none'; connect-src 'none' without granted origins.
        Assert.Contains("default-src 'none'", source);
        Assert.Contains("const connectSrc = networkOrigins.length > 0 ? networkOrigins.join(' ') : \"'none'\";", source);
        Assert.Contains("img-src 'none'", source);
        Assert.Contains("object-src 'none'", source);
        Assert.Contains("frame-src 'none'", source);
        Assert.Contains("form-action 'none'", source);
    }

    [Fact]
    public void GrantGuard_DeniesByDefault_AndNeverWidensBeyondUserDestinations()
    {
        var source = ReadFrontendSource("plugins/networkGrantGuard.ts");

        // Default grant: no network at all.
        Assert.Contains("export const NO_NETWORK_GRANT: NetworkGrant = { mode: 'none', destinationRefs: [], granted: false };", source);

        // Deny without explicit grant; deny without destination refs; http(s) only;
        // exact host+scheme+port match against user rules; deny by default at the end.
        Assert.Contains("if (grant.mode !== 'destinations' || !grant.granted)", source);
        Assert.Contains("if (grant.destinationRefs.length === 0)", source);
        Assert.Contains("if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:')", source);
        Assert.Contains("return { allowed: false, reason: 'URL does not match any granted user destination rule' };", source);

        // CSP origins: empty without a grant.
        Assert.Contains("if (grant.mode !== 'destinations' || !grant.granted) return [];", source);
    }

    [Fact]
    public void RegistryAndBridge_WireTheZeroNetworkDefault()
    {
        var registry = ReadFrontendSource("plugins/pluginRegistry.ts");
        Assert.Contains("networkGrant: NO_NETWORK_GRANT", registry);
        Assert.Contains("networkGrant: overrides.networkGrant ?? previous?.networkGrant ?? NO_NETWORK_GRANT", registry);

        var bridge = ReadFrontendSource("plugins/sandboxBridge.ts");
        Assert.Contains("isNetworkAllowed(payload.url, this.options.networkGrant, this.options.destinations)", bridge);

        var manager = ReadFrontendSource("plugins/pluginManager.ts");
        Assert.Contains("grantedConnectOrigins(entry.networkGrant, this.destinations)", manager);
    }

    private static string ReadFrontendSource(string relativePath)
    {
        var path = Path.Combine(EvidenceWriter.AppRoot, "src", "frontend", "src", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Frontend source missing: {path}");
        return File.ReadAllText(path);
    }
}
