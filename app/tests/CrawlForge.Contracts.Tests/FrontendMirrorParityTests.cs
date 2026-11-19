using System.Reflection;
using System.Text.RegularExpressions;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Contracts.Tests;

/// <summary>
/// Frontend mirror parity (WP-10-A1, D-021): the frozen catalogs in
/// app/src/frontend/src/contracts/api/vocabulary.ts must match the locked
/// backend assembly byte for byte — every AuditCategory / WafChallengeClass /
/// CaptureMode / PatternCaptureMode / CaptureSource / DiscoverySource wire
/// value, every IndexabilityFlags bit and every FindingRuleKeys constant.
/// Real producer (the contracts assembly) → real consumer (the frontend
/// mirror the UI code imports); extending one side without the other fails
/// here before the phase gate.
/// </summary>
public class FrontendMirrorParityTests
{
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string ContractsDir =>
        Path.Combine(RepoRoot, "app", "src", "frontend", "src", "contracts", "api");

    private static string VocabularyTs => Path.Combine(ContractsDir, "vocabulary.ts");

    private static string ProjectsTs => Path.Combine(ContractsDir, "projects.ts");

    private static string AuditsTs => Path.Combine(ContractsDir, "audits.ts");

    [Fact]
    public void AuditCategoryMirrorMatchesTheBackendVocabulary()
    {
        string source = File.ReadAllText(VocabularyTs);
        string[] members = ExtractConstArray(source, "AUDIT_CATEGORY_MEMBERS");
        string[] wire = ExtractConstArray(source, "AUDIT_CATEGORY_WIRE_VALUES");

        Assert.Equal(Enum.GetNames<AuditCategory>(), members);
        Assert.Equal(Enum.GetValues<AuditCategory>().Select(EnumWireNames.ToWireValue), wire);

        // The §7.4 display labels of the three new categories are locked.
        Assert.Contains("images: 'Images'", source, StringComparison.Ordinal);
        Assert.Contains("securityandprotocol: 'Security & protocol'", source, StringComparison.Ordinal);
        Assert.Contains("internationalization: 'Internationalization'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WafChallengeClassMirrorMatchesTheBackendVocabulary()
    {
        string[] wire = ExtractConstArray(File.ReadAllText(VocabularyTs), "WAF_CHALLENGE_CLASS_WIRE_VALUES");
        Assert.Equal(Enum.GetValues<WafChallengeClass>().Select(EnumWireNames.ToWireValue), wire);
    }

    [Fact]
    public void CaptureModeMirrorMatchesTheBackendVocabulary()
    {
        string[] wire = ExtractConstArray(File.ReadAllText(VocabularyTs), "CAPTURE_MODE_WIRE_VALUES");
        Assert.Equal(Enum.GetValues<CaptureMode>().Select(EnumWireNames.ToWireValue), wire);
    }

    [Fact]
    public void PatternCaptureMirrorMatchesTheBackendVocabulary()
    {
        string[] wire = ExtractConstArray(File.ReadAllText(VocabularyTs), "PATTERN_CAPTURE_WIRE_VALUES");
        Assert.Equal(Enum.GetValues<PatternCaptureMode>().Select(EnumWireNames.ToWireValue), wire);
    }

    [Fact]
    public void CaptureSourceMirrorMatchesTheBackendVocabulary()
    {
        string[] wire = ExtractConstArray(File.ReadAllText(VocabularyTs), "CAPTURE_SOURCE_WIRE_VALUES");
        Assert.Equal(Enum.GetValues<CaptureSource>().Select(EnumWireNames.ToWireValue), wire);
    }

    [Fact]
    public void DiscoverySourceMirrorMatchesTheBackendVocabulary()
    {
        string[] wire = ExtractConstArray(File.ReadAllText(VocabularyTs), "DISCOVERY_SOURCE_WIRE_VALUES");
        Assert.Equal(Enum.GetValues<DiscoverySource>().Select(EnumWireNames.ToWireValue), wire);
    }

    [Fact]
    public void ContentArtifactKindMirrorMatchesTheBackendVocabulary()
    {
        string[] wire = ExtractConstArray(File.ReadAllText(VocabularyTs), "CONTENT_ARTIFACT_KIND_WIRE_VALUES");
        Assert.Equal(Enum.GetValues<ContentArtifactKind>().Select(EnumWireNames.ToWireValue), wire);
    }

    [Fact]
    public void IndexabilityFlagBitsMirrorMatchesTheBackendFlags()
    {
        string source = File.ReadAllText(VocabularyTs);
        int start = source.IndexOf("export const INDEXABILITY_FLAG_BITS = {", StringComparison.Ordinal);
        Assert.True(start >= 0, "INDEXABILITY_FLAG_BITS catalog is missing from vocabulary.ts.");
        int end = source.IndexOf("} as const;", start, StringComparison.Ordinal);
        Assert.True(end > start, "INDEXABILITY_FLAG_BITS catalog is not frozen with 'as const'.");
        string block = source[start..end];

        Dictionary<string, int> mirrored = Regex.Matches(block, @"(\w+):\s*(\d+)")
            .ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value), StringComparer.Ordinal);

        Dictionary<string, int> expected = Enum.GetValues<IndexabilityFlags>()
            .ToDictionary(f => char.ToLowerInvariant(f.ToString()[0]) + f.ToString()[1..], f => (int)f, StringComparer.Ordinal);

        Assert.Equal(expected, mirrored);
        Assert.Equal(16, mirrored["canonicalElsewhere"]);
    }

    [Fact]
    public void FindingRuleKeysMirrorMatchesTheLockedCatalog()
    {
        string source = File.ReadAllText(VocabularyTs);
        int start = source.IndexOf("export const FINDING_RULE_KEYS = {", StringComparison.Ordinal);
        Assert.True(start >= 0, "FINDING_RULE_KEYS catalog is missing from vocabulary.ts.");
        int end = source.IndexOf("} as const;", start, StringComparison.Ordinal);
        Assert.True(end > start, "FINDING_RULE_KEYS catalog is not frozen with 'as const'.");
        string block = source[start..end];

        Dictionary<string, string> mirrored = Regex.Matches(block, @"(\w+):\s*'([^']*)'")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

        Dictionary<string, string> expected = typeof(FindingRuleKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);

        Assert.Equal(expected, mirrored);
    }

    [Fact]
    public void ConfigurationWireShapesCarryTheAdditiveCaptureFields()
    {
        string projects = File.ReadAllText(ProjectsTs);
        Assert.Contains("readonly captureMode?: CaptureModeWire | null;", projects, StringComparison.Ordinal);
        Assert.Contains("readonly captureSettings?: CaptureSettings | null;", projects, StringComparison.Ordinal);
        Assert.Contains("readonly redirectChecks?: RedirectCheckSettings | null;", projects, StringComparison.Ordinal);

        // Read, create and update shapes all accept the additive fields.
        Assert.Contains("export interface AuditConfiguration extends ConfigurationSummary, ExtensionSettings, CaptureConfigurationFields", projects, StringComparison.Ordinal);
        Assert.Contains("export interface ConfigurationCreateRequest extends ExtensionSettings, CaptureConfigurationFields", projects, StringComparison.Ordinal);
        Assert.Contains("export interface ConfigurationUpdateRequest extends ExtensionSettings, CaptureConfigurationFields", projects, StringComparison.Ordinal);

        // The settings document mirrors every §5.6.1/§5.6.2 knob.
        Assert.Contains("readonly adaptiveSampleSizePerGroup?: number;", projects, StringComparison.Ordinal);
        Assert.Contains("readonly materialChangeWordCountThreshold?: number;", projects, StringComparison.Ordinal);
        Assert.Contains("readonly dualStorageWarningThresholdBytes?: number;", projects, StringComparison.Ordinal);
        Assert.Contains("readonly renderWaitQuietMs?: number;", projects, StringComparison.Ordinal);
        Assert.Contains("readonly renderWaitHardCapMs?: number;", projects, StringComparison.Ordinal);
        Assert.Contains("readonly renderingPoolSize?: number | null;", projects, StringComparison.Ordinal);
        Assert.Contains("readonly patternOverrides?: readonly CapturePatternOverride[] | null;", projects, StringComparison.Ordinal);

        // The four CR-029 toggles.
        Assert.Contains("readonly crossHost?: boolean;", projects, StringComparison.Ordinal);
        Assert.Contains("readonly temporaryStatus?: boolean;", projects, StringComparison.Ordinal);
        Assert.Contains("readonly nonIndexableTarget?: boolean;", projects, StringComparison.Ordinal);
        Assert.Contains("readonly httpsDowngrade?: boolean;", projects, StringComparison.Ordinal);
    }

    [Fact]
    public void UrlRecordWireShapeCarriesCaptureProvenance()
    {
        string audits = File.ReadAllText(AuditsTs);
        Assert.Contains("readonly captureSource?: CaptureSourceWire | null;", audits, StringComparison.Ordinal);
        Assert.Contains("readonly discoverySource?: DiscoverySourceWire | null;", audits, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTestWireShapeCarriesTheNetworkWaterfall()
    {
        string tools = File.ReadAllText(Path.Combine(ContractsDir, "tools.ts"));
        Assert.Contains("readonly networkWaterfall?: readonly RenderedNetworkEntry[] | null;", tools, StringComparison.Ordinal);
        Assert.Contains("export interface RenderedNetworkEntry", tools, StringComparison.Ordinal);
        // Every RenderedNetworkEntry field is mirrored (CR-031 reads transferredBytes).
        foreach (string field in new[]
        {
            "readonly url: string;",
            "readonly resourceType: string;",
            "readonly method: string;",
            "readonly statusCode: number;",
            "readonly mimeType?: string | null;",
            "readonly transferredBytes?: number;",
            "readonly encodedDataLength?: number;",
            "readonly timingJson?: string | null;",
            "readonly fromCache?: boolean;",
            "readonly fromServiceWorker?: boolean;",
        })
        {
            Assert.Contains(field, tools, StringComparison.Ordinal);
        }
    }

    /// <summary>Extracts the quoted strings of a frozen `export const NAME = [...] as const` array.</summary>
    private static string[] ExtractConstArray(string source, string constName)
    {
        int start = source.IndexOf($"export const {constName} = [", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{constName} catalog is missing from vocabulary.ts.");
        int end = source.IndexOf("] as const;", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{constName} catalog is not frozen with 'as const'.");
        string block = source[start..end];
        return Regex.Matches(block, "'([^']*)'").Select(m => m.Groups[1].Value).ToArray();
    }

    /// <summary>Walks up from the test output directory until the solution file is found.</summary>
    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
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