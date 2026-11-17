using CrawlForge.Intelligence.LogAnalyzer;
using CrawlForge.Intelligence.Tests.LogAnalyzer.Support;
using Xunit;

namespace CrawlForge.Intelligence.Tests.LogAnalyzer;

/// <summary>
/// Bot classification and verification (FS-CRAWL-002 §11.3, criterion #44):
/// family precedence, offline CIDR matching, claimed/cidr-verified labeling,
/// human/unknown buckets, and forward-confirmed rDNS via a scripted resolver
/// (fully offline suites).
/// </summary>
public sealed class BotVerifierTests
{
    private readonly BotCatalogue _catalogue = BotCatalogue.Load(TestAssets.BotCataloguePath());

    private BotVerifier CreateVerifier(FakeDnsResolver? resolver = null) => new(_catalogue, resolver);

    [Theory]
    [InlineData(LogSamples.GooglebotDesktopUa, "Googlebot Desktop")]
    [InlineData(LogSamples.GooglebotSmartphoneUa, "Googlebot Smartphone")]
    [InlineData(LogSamples.BingbotUa, "Bingbot")]
    [InlineData(LogSamples.GptBotUa, "GPTBot")]
    [InlineData("Mozilla/5.0 (compatible; ClaudeBot/1.0; +claudebot@anthropic.com)", "ClaudeBot")]
    [InlineData("Mozilla/5.0 (compatible; PerplexityBot/1.0; +https://perplexity.ai/perplexitybot)", "PerplexityBot")]
    [InlineData("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko); compatible; OAI-SearchBot/1.0; +https://openai.com/searchbot", "OAI-SearchBot")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (Applebot/0.1)", "Applebot")]
    public void KnownFamiliesClassifyWithPurpose(string userAgent, string expectedFamily)
    {
        var verifier = CreateVerifier();

        var classification = verifier.Classify("203.0.113.1", userAgent);

        Assert.True(classification.IsBot);
        Assert.Equal(expectedFamily, classification.Family);
        var family = _catalogue.Families.Single(f => f.Name == expectedFamily);
        Assert.Contains(family.Purpose, new[] { BotPurposes.Search, BotPurposes.AiSearch, BotPurposes.AiTraining });
    }

    [Fact]
    public void SmartphoneTakesPrecedenceOverDesktop()
    {
        var verifier = CreateVerifier();

        var classification = verifier.Classify(null, LogSamples.GooglebotSmartphoneUa);

        Assert.Equal("Googlebot Smartphone", classification.Family);
    }

    [Fact]
    public void GenericBotTokensFallIntoOtherBot()
    {
        var verifier = CreateVerifier();

        var classification = verifier.Classify(null, "Mozilla/5.0 (compatible; SemrushBot/7~bl; +http://www.semrush.com/bot.html)");

        Assert.True(classification.IsBot);
        Assert.Equal("Other Bot", classification.Family);
    }

    [Fact]
    public void BrowserTrafficIsHumanAndMissingAgentIsUnknown()
    {
        var verifier = CreateVerifier();

        var human = verifier.Classify("203.0.113.1", LogSamples.HumanUa);
        var unknown = verifier.Classify("203.0.113.1", null);
        var empty = verifier.Classify("203.0.113.1", "   ");

        Assert.False(human.IsBot);
        Assert.Equal("human", human.Family);
        Assert.False(unknown.IsBot);
        Assert.Equal("unknown", unknown.Family);
        Assert.Equal("unknown", empty.Family);
    }

    [Fact]
    public void CidrMatchUpgradesTierToCidrVerified()
    {
        var verifier = CreateVerifier();

        var inRange = verifier.Classify("66.249.66.1", LogSamples.GooglebotDesktopUa);
        var outOfRange = verifier.Classify("203.0.113.1", LogSamples.GooglebotDesktopUa);

        Assert.True(inRange.CidrMatched);
        Assert.Equal(BotVerificationTiers.CidrVerified, BotVerificationTiers.FromClassification(inRange));
        Assert.False(outOfRange.CidrMatched);
        Assert.Equal(BotVerificationTiers.Claimed, BotVerificationTiers.FromClassification(outOfRange));
    }

    [Fact]
    public void UnverifiedBotsAreLabeledNeverSilentlyTrusted()
    {
        var verifier = CreateVerifier();

        // Spoofed UA from a non-publisher IP stays claimed (unverified).
        var spoofed = verifier.Classify("198.51.100.7", LogSamples.GooglebotDesktopUa);

        Assert.True(spoofed.IsBot);
        Assert.False(spoofed.CidrMatched);
        Assert.Equal(BotVerificationTiers.Claimed, BotVerificationTiers.FromClassification(spoofed));
    }

    [Theory]
    [InlineData("66.249.64.0", true)]
    [InlineData("66.249.95.255", true)]
    [InlineData("66.249.96.0", false)]
    [InlineData("66.249.63.255", false)]
    public void CidrBoundaryAddressesMatchExactly(string ip, bool expected)
    {
        var verifier = CreateVerifier();

        var classification = verifier.Classify(ip, LogSamples.GooglebotDesktopUa);

        Assert.Equal(expected, classification.CidrMatched);
    }

    [Fact]
    public void Ipv6CidrRangesMatch()
    {
        var verifier = CreateVerifier();

        var inside = verifier.Classify("2001:4860:4801::5", LogSamples.GooglebotDesktopUa);
        var outside = verifier.Classify("2001:4860:4802::5", LogSamples.GooglebotDesktopUa);

        Assert.True(inside.CidrMatched);
        Assert.False(outside.CidrMatched);
    }

    [Fact]
    public void InvalidIpNeverMatchesCidr()
    {
        var verifier = CreateVerifier();

        var classification = verifier.Classify("not-an-ip", LogSamples.GooglebotDesktopUa);

        Assert.True(classification.IsBot);
        Assert.False(classification.CidrMatched);
    }

    [Fact]
    public void UserCidrAdditionsMergeWithoutMutatingTheAsset()
    {
        var additions = new[] { new UserCidrAddition("perplexitybot", "198.51.100.0/24") };
        var catalogue = BotCatalogue.Load(TestAssets.BotCataloguePath(), additions);
        var verifier = new BotVerifier(catalogue);

        var classification = verifier.Classify("198.51.100.9", "Mozilla/5.0 (compatible; PerplexityBot/1.0)");

        Assert.True(classification.CidrMatched);
        // The bundled asset itself is unchanged on disk.
        var reloaded = BotCatalogue.Load(TestAssets.BotCataloguePath());
        Assert.Empty(reloaded.Families.Single(f => f.Key == "perplexitybot").Cidrs);
    }

    [Fact]
    public async Task DnsVerificationConfirmsForwardReverseAndSuffix()
    {
        var resolver = new FakeDnsResolver();
        resolver.Reverse["66.249.66.1"] = "crawl-66-249-66-1.googlebot.com";
        resolver.Forward["crawl-66-249-66-1.googlebot.com"] = ["66.249.66.1"];
        var verifier = CreateVerifier(resolver);

        var result = await verifier.VerifyDnsAsync("66.249.66.1");

        Assert.True(result.Verified);
        Assert.Equal("crawl-66-249-66-1.googlebot.com", result.Hostname);
        Assert.Equal(1, resolver.ReverseCalls);
        Assert.Equal(1, resolver.ForwardCalls);
    }

    [Fact]
    public async Task DnsVerificationFailsWhenForwardDoesNotConfirm()
    {
        var resolver = new FakeDnsResolver();
        resolver.Reverse["66.249.66.1"] = "crawl.googlebot.com";
        resolver.Forward["crawl.googlebot.com"] = ["66.249.66.99"];
        var verifier = CreateVerifier(resolver);

        var result = await verifier.VerifyDnsAsync("66.249.66.1");

        Assert.False(result.Verified);
    }

    [Fact]
    public async Task DnsVerificationFailsForForeignSuffix()
    {
        var resolver = new FakeDnsResolver();
        resolver.Reverse["203.0.113.9"] = "spoofed.example.com";
        resolver.Forward["spoofed.example.com"] = ["203.0.113.9"];
        var verifier = CreateVerifier(resolver);

        var result = await verifier.VerifyDnsAsync("203.0.113.9");

        Assert.False(result.Verified);
    }

    [Fact]
    public async Task DnsVerificationIsTolerantOfMissingResolution()
    {
        var verifier = CreateVerifier(new FakeDnsResolver());

        var result = await verifier.VerifyDnsAsync("203.0.113.9");
        var invalid = await verifier.VerifyDnsAsync("not-an-ip");

        Assert.False(result.Verified);
        Assert.Null(result.Hostname);
        Assert.False(invalid.Verified);
    }

    [Fact]
    public void CatalogueCarriesVersionMetadata()
    {
        Assert.False(string.IsNullOrWhiteSpace(_catalogue.Version));
        Assert.False(string.IsNullOrWhiteSpace(_catalogue.Revision));
        Assert.True(_catalogue.Families.Count >= 9);
    }

    [Fact]
    public void SearchPurposeFamiliesDriveSearchBotHits()
    {
        var search = _catalogue.Families.Where(f => f.IsSearchBot).Select(f => f.Name).ToList();

        Assert.Contains("Googlebot Desktop", search);
        Assert.Contains("Googlebot Smartphone", search);
        Assert.Contains("Bingbot", search);
        Assert.Contains("Applebot", search);
        Assert.DoesNotContain("GPTBot", search);
        Assert.DoesNotContain("OAI-SearchBot", search);
    }
}
