using CrawlForge.Contracts.Enums;
using CrawlForge.Intelligence.Geo;
using CrawlForge.Intelligence.Tests.Geo.Support;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Geo;

/// <summary>
/// Bundled AI-agent profile asset integrity (§13.2): the nine mandated agents,
/// purpose classes, uniqueness, and loader validation. In-house curated asset;
/// no remote refresh (E2).
/// </summary>
public sealed class AgentProfileAssetTests
{
    [Fact]
    public void BundledAsset_Loads_WithVersionAndRevision()
    {
        var catalogue = AiAgentProfileCatalogue.Load(GeoRealStack.AgentProfileAssetPath);

        Assert.False(string.IsNullOrWhiteSpace(catalogue.Version));
        Assert.False(string.IsNullOrWhiteSpace(catalogue.Revision));
        Assert.True(catalogue.Profiles.Count >= 9);
    }

    [Theory]
    [InlineData("Googlebot", AiAgentPurpose.Search)]
    [InlineData("Bingbot", AiAgentPurpose.Search)]
    [InlineData("OAI-SearchBot", AiAgentPurpose.Rag)]
    [InlineData("PerplexityBot", AiAgentPurpose.Rag)]
    [InlineData("GPTBot", AiAgentPurpose.Training)]
    [InlineData("ClaudeBot", AiAgentPurpose.Training)]
    [InlineData("Google-Extended", AiAgentPurpose.Training)]
    [InlineData("CCBot", AiAgentPurpose.Training)]
    [InlineData("Applebot-Extended", AiAgentPurpose.Training)]
    public void MandatedAgents_HaveSpecPurposeClasses(string agentToken, AiAgentPurpose expected)
    {
        var catalogue = AiAgentProfileCatalogue.Load(GeoRealStack.AgentProfileAssetPath);
        var profile = catalogue.Profiles.Single(p => p.AgentToken == agentToken);

        Assert.Equal(expected, profile.Purpose);
        Assert.NotEmpty(profile.UserAgentPatterns);
        Assert.False(string.IsNullOrWhiteSpace(profile.Vendor));
        Assert.False(string.IsNullOrWhiteSpace(profile.PolicyNotes));
    }

    [Fact]
    public void AgentTokens_AreUnique()
    {
        var catalogue = AiAgentProfileCatalogue.Load(GeoRealStack.AgentProfileAssetPath);
        var tokens = catalogue.Profiles.Select(p => p.AgentToken).ToArray();

        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void DefaultAssetPath_ResolvesFromRepositoryRoot()
    {
        var path = AiAgentProfileCatalogue.ResolveDefaultAssetPath();
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DuplicateTokens_AreRejected()
    {
        var path = WriteTempAsset("""
            { "version": "t", "profiles": [
              { "agentToken": "X", "userAgentPatterns": ["X"], "purposeClass": "llm-training" },
              { "agentToken": "x", "userAgentPatterns": ["x"], "purposeClass": "llm-training" }
            ] }
            """);

        Assert.Throws<InvalidDataException>(() => AiAgentProfileCatalogue.Load(path));
    }

    [Fact]
    public void UnknownPurposeClass_IsRejected()
    {
        var path = WriteTempAsset("""
            { "version": "t", "profiles": [
              { "agentToken": "X", "userAgentPatterns": ["X"], "purposeClass": "unknown-class" }
            ] }
            """);

        Assert.Throws<InvalidDataException>(() => AiAgentProfileCatalogue.Load(path));
    }

    [Fact]
    public void EmptyPatterns_AreRejected()
    {
        var path = WriteTempAsset("""
            { "version": "t", "profiles": [
              { "agentToken": "X", "userAgentPatterns": [], "purposeClass": "llm-training" }
            ] }
            """);

        Assert.Throws<InvalidDataException>(() => AiAgentProfileCatalogue.Load(path));
    }

    [Fact]
    public void MissingAsset_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => AiAgentProfileCatalogue.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.json")));
    }

    private static string WriteTempAsset(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "cf-wp-04-03-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }
}
