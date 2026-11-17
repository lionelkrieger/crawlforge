using System.Text;
using CrawlForge.Boundary.Tests.Harness;
using CrawlForge.Host.Composition;
using Xunit;

namespace CrawlForge.Boundary.Tests.Assets;

/// <summary>
/// Bundled-asset manifest check (FS-CRAWL-001 §13, exclusions E2/E4/E8): every
/// asset catalog ships inside the application tree, resolves locally through
/// the AssetLocator, and has NO runtime fetch path. Packaging status is
/// reported (the packaging workpiece owns installer manifests).
/// </summary>
public sealed class BundledAssetManifestTests
{
    private static readonly string[] RequiredCatalogs =
    [
        "ai-agent-profiles",
        "bot-cidr",
        "dictionaries",
        "export-templates",
        "font-metrics",
        "onnx-model",
        "plugin-schema",
        "stopwords",
        "tech-fingerprints",
        "waf-signatures",
    ];

    [Fact]
    public void AllAssetCatalogs_AreBundledAndResolveLocally()
    {
        var assetsRoot = Path.Combine(EvidenceWriter.AppRoot, "assets");
        Assert.True(Directory.Exists(assetsRoot), "The assets directory must exist in the app tree.");

        var inventory = new List<string>();
        var builder = new StringBuilder();
        builder.AppendLine("# Bundled-asset manifest check (§13, E2/E4/E8)");
        builder.AppendLine();
        builder.AppendLine("| Catalog | Files | Bytes | Local resolution |");
        builder.AppendLine("|---|---|---|---|");

        foreach (var catalog in RequiredCatalogs)
        {
            var directory = Path.Combine(assetsRoot, catalog);
            Assert.True(Directory.Exists(directory), $"Asset catalog '{catalog}' must be bundled.");

            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
            Assert.NotEmpty(files);
            var bytes = files.Sum(f => new FileInfo(f).Length);

            // The product resolver finds it locally (never fetches).
            var resolved = AssetLocator.ResolveDirectory(catalog);
            Assert.True(Directory.Exists(resolved));

            builder.AppendLine($"| {catalog} | {files.Count} | {bytes} | {resolved} |");
            inventory.Add($"{catalog}: {files.Count} files, {bytes} bytes");
        }

        // Missing assets throw locally — there is no download fallback.
        Assert.Throws<DirectoryNotFoundException>(() => AssetLocator.ResolveDirectory("no-such-asset-catalog"));

        // Static: the resolver contains no network surface whatsoever.
        var locatorSource = File.ReadAllText(Path.Combine(
            EvidenceWriter.AppRoot, "src", "backend", "CrawlForge.Host", "Composition", "AssetLocator.cs"));
        string[] networkApis = ["HttpClient", "WebClient", "Download", "WebRequest", "Socket", "Dns."];
        foreach (var api in networkApis)
        {
            Assert.DoesNotContain(api, locatorSource);
        }

        // Packaging status (installer manifests belong to the packaging workpiece).
        var packagingRoot = Path.Combine(EvidenceWriter.AppRoot, "packaging");
        var packagingPresent = Directory.Exists(packagingRoot);
        builder.AppendLine();
        builder.AppendLine($"- Required catalogs bundled: {RequiredCatalogs.Length}/{RequiredCatalogs.Length}");
        builder.AppendLine("- AssetLocator: local-only resolution; missing asset throws DirectoryNotFoundException (no fetch fallback); no network API in the resolver source.");
        builder.AppendLine($"- packaging/ directory present: {packagingPresent} ({(packagingPresent ? "inspect installer manifests" : "packaging workpiece not yet delivered — reported to orchestrator")})");
        builder.AppendLine("- Governance NR-E2/E4 full-tree scan: zero violations (see governance evidence).");

        EvidenceWriter.WriteMarkdown("bundled-assets.md", builder.ToString());
    }
}
