using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Browser provisioning conformance (§5.6.3, D-003/CR-007) on FAKE bundle
/// layouts: pinned bundled Chrome for Testing → user path → local Chrome →
/// local Edge → pre-crawl validation failure. The pinned version is recorded
/// for UI-21. No runtime download exists anywhere in the path.
/// </summary>
public sealed class BrowserProvisioningConformanceTests
{
    [Fact]
    public void BundledManifestLayout_ResolvesFirst_WithPinnedVersion()
    {
        using var bundle = FakeBundleLayout.Create(pinnedVersion: "131.0.6778.85");
        var resolver = new LocalBrowserResolver(bundle.ResolutionOptions);

        var resolved = resolver.ResolveDetailed();
        Assert.Equal(BrowserSource.Bundled, resolved.Source);
        Assert.Equal(Path.GetFullPath(bundle.ExecutablePath), resolved.ExecutablePath);
        Assert.Equal("Chrome for Testing", resolved.Product);
        Assert.Equal("131.0.6778.85", resolved.Version);
        Assert.Equal("131.0.6778.85", resolved.PinnedVersion);
        // Compatibility surface agrees.
        Assert.Equal(resolved.ExecutablePath, resolver.Resolve());
    }

    [Fact]
    public void BundledLegacyLayout_WithoutManifest_StillResolves()
    {
        using var bundle = FakeBundleLayout.Create(pinnedVersion: null, legacyLayout: true);
        var resolver = new LocalBrowserResolver(bundle.ResolutionOptions);

        var resolved = resolver.ResolveDetailed();
        Assert.Equal(BrowserSource.Bundled, resolved.Source);
        Assert.Null(resolved.PinnedVersion);
        Assert.True(File.Exists(resolved.ExecutablePath));
    }

    [Fact]
    public void BundledWins_OverUserConfiguredPath()
    {
        using var bundle = FakeBundleLayout.Create();
        var userPath = Path.Combine(bundle.Root, "user-browser.exe");
        File.WriteAllText(userPath, "stub");

        var resolver = new LocalBrowserResolver(new BrowserResolutionOptions
        {
            PackagedAssetsRoot = bundle.Root,
            UserConfiguredExecutablePath = userPath,
            ProbeLocalInstalls = false,
        });

        Assert.Equal(BrowserSource.Bundled, resolver.ResolveDetailed().Source);
    }

    [Fact]
    public void ManifestWithoutBinary_FallsThroughToUserPath()
    {
        using var bundle = FakeBundleLayout.Create(withExecutable: false);
        var userPath = Path.Combine(bundle.Root, "user-browser.exe");
        File.WriteAllText(userPath, "stub");

        var resolver = new LocalBrowserResolver(new BrowserResolutionOptions
        {
            PackagedAssetsRoot = bundle.Root,
            UserConfiguredExecutablePath = userPath,
            ProbeLocalInstalls = false,
        });

        var resolved = resolver.ResolveDetailed();
        Assert.Equal(BrowserSource.UserConfigured, resolved.Source);
        Assert.Equal(Path.GetFullPath(userPath), resolved.ExecutablePath);
        // The pin stays readable for UI-21 even before O-006 ships the binary.
        Assert.Equal("131.0.6778.85", BrowserProvisioning.TryLoadPinManifest(new BrowserResolutionOptions
        {
            PackagedAssetsRoot = bundle.Root,
        })!.Version);
    }

    [Fact]
    public void UserPathWins_OverLocalInstalls()
    {
        using var bundle = FakeBundleLayout.Create(withExecutable: false, pinnedVersion: null);
        var userPath = Path.Combine(bundle.Root, "user-browser.exe");
        File.WriteAllText(userPath, "stub");

        var resolver = new LocalBrowserResolver(new BrowserResolutionOptions
        {
            PackagedAssetsRoot = bundle.Root,
            UserConfiguredExecutablePath = userPath,
            ProbeLocalInstalls = true,
        });

        Assert.Equal(BrowserSource.UserConfigured, resolver.ResolveDetailed().Source);
    }

    [Fact]
    public void MissingUserConfiguredPath_IsALocalConfigurationError()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cf-wp-10-01-missing-" + Guid.NewGuid().ToString("N") + ".exe");
        var resolver = new LocalBrowserResolver(new BrowserResolutionOptions
        {
            UserConfiguredExecutablePath = missing,
            ProbeLocalInstalls = false,
        });

        var failure = Assert.Throws<BrowserResolutionException>(() => resolver.ResolveDetailed());
        Assert.Contains("does not exist", failure.Message);
        Assert.Contains("download is prohibited", failure.Message);
    }

    [Fact]
    public void NoBrowserAnywhere_FailsLocally_WithOfflineDiagnostic()
    {
        using var bundle = FakeBundleLayout.Create(withExecutable: false, pinnedVersion: null);
        var resolver = new LocalBrowserResolver(new BrowserResolutionOptions
        {
            PackagedAssetsRoot = bundle.Root,
            ProbeLocalInstalls = false,
        });

        var failure = Assert.Throws<BrowserResolutionException>(() => resolver.ResolveDetailed());
        Assert.Contains("runtime download is prohibited", failure.Message);
        Assert.Contains("non-JS capture remains available", failure.Message);
        Assert.Null(BrowserProvisioning.TryResolve(new BrowserResolutionOptions
        {
            PackagedAssetsRoot = bundle.Root,
            ProbeLocalInstalls = false,
        }));
    }

    [Fact]
    public void LocalInstallFallback_FindsChromeOrEdgeOnThisMachine()
    {
        var resolver = new LocalBrowserResolver();
        var resolved = resolver.ResolveDetailed();

        Assert.True(File.Exists(resolved.ExecutablePath));
        Assert.True(resolved.Source is BrowserSource.LocalChrome or BrowserSource.LocalEdge or BrowserSource.Bundled);
        Assert.NotEqual("unknown", resolved.Version); // real binaries carry version metadata
    }

    [Fact]
    public void InvalidManifest_IsALocalConfigurationError()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-wp-10-01-badpin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, BrowserPinManifest.FileName), "{ not json");
            var resolver = new LocalBrowserResolver(new BrowserResolutionOptions
            {
                PackagedAssetsRoot = root,
                ProbeLocalInstalls = false,
            });
            Assert.Throws<BrowserResolutionException>(() => resolver.ResolveDetailed());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ManifestWithoutVersion_IsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-wp-10-01-nopin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, BrowserPinManifest.FileName), """
                {"product":"chrome-for-testing","platforms":{}}
                """);
            Assert.Throws<BrowserResolutionException>(() => BrowserPinManifest.TryLoad(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingManifest_ReturnsNull()
    {
        Assert.Null(BrowserPinManifest.TryLoad(null));
        Assert.Null(BrowserPinManifest.TryLoad(Path.Combine(Path.GetTempPath(), "cf-wp-10-01-absent-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void RealAssetRoot_CarriesThePin_ForUi21()
    {
        // The shipped assets/browser pin manifest is discoverable from the
        // application tree (the UI-21 browser section binds to this).
        var root = LocalBrowserResolver.DiscoverPackagedAssetsRoot();
        Assert.NotNull(root);
        var manifest = BrowserPinManifest.TryLoad(root);
        Assert.NotNull(manifest);
        Assert.Equal("chrome-for-testing", manifest!.Product);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Version));
        Assert.NotNull(manifest.ResolvePlatformExecutablePath());
    }

    [Fact]
    public void RenderingSource_ContainsNoDownloadOrAutomationLibrary()
    {
        // D-002/D-003 structural proof: the rendering path constructs no
        // outbound HTTP client, uses no download API, and carries no
        // PuppeteerSharp code. The only socket is the loopback CDP client.
        var renderingRoot = FindRepoRenderingRoot();
        var sources = Directory.GetFiles(renderingRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToList();
        Assert.NotEmpty(sources);

        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("PuppeteerSharp", text);
            Assert.DoesNotContain("new HttpClient", text);
            Assert.DoesNotContain("WebClient", text);
            Assert.DoesNotContain("DownloadFile", text);
            Assert.DoesNotContain("DownloadString", text);
            Assert.DoesNotContain("DownloadData", text);
            Assert.DoesNotContain("BrowserFetcher", text);
        }

        // The CDP client guards the loopback boundary before connecting.
        var connectionSource = File.ReadAllText(Path.Combine(renderingRoot, "CdpConnection.cs"));
        Assert.Contains("IPAddress.IsLoopback", connectionSource);
    }

    private static string FindRepoRenderingRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "app", "src", "backend", "CrawlForge.Crawler", "Rendering");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(directory.FullName, "src", "backend", "CrawlForge.Crawler", "Rendering");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Rendering source folder from the test bin layout.");
    }
}
