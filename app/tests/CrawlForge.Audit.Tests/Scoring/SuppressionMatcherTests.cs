using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoring;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Scoring;

/// <summary>
/// Suppression matcher suite: asserts EXACT parity with the accepted pipeline
/// keystone (<see cref="FindingEmitter.IsSuppressed"/>) across the full matrix
/// of suppression shapes and finding identities, plus deterministic key
/// construction. This is the WP-03-08 consistency guarantee: scoring and the
/// emitter can never disagree about coverage.
/// </summary>
public class SuppressionMatcherTests
{
    private static FindingSuppressionDto Suppression(
        string? ruleKey, long? urlId, string? scope, bool active = true) =>
        new(1, 1, "key", ruleKey, urlId, scope, active, DateTimeOffset.UnixEpoch);

    private static FindingDraft Draft(string ruleKey, long? urlId) =>
        new(ruleKey, AuditCategory.Content, FindingSeverity.Warning, urlId, "{}", null);

    [Fact]
    public void Covers_MatchesKeystone_AcrossFullShapeMatrix()
    {
        string?[] ruleKeys = [null, "rule.a", "rule.b"];
        long?[] urlIds = [null, 1L, 2L];
        string?[] scopes = [null, "rule.a", "other"];
        bool[] states = [true, false];
        (string RuleKey, long? UrlId)[] findings =
        [
            ("rule.a", null), ("rule.a", 1L), ("rule.a", 2L),
            ("rule.b", null), ("rule.b", 1L), ("rule.c", 3L),
        ];

        var checkedCount = 0;
        foreach (var ruleKey in ruleKeys)
        foreach (var urlId in urlIds)
        foreach (var scope in scopes)
        foreach (var active in states)
        {
            var suppression = Suppression(ruleKey, urlId, scope, active);
            foreach (var (findingRule, findingUrl) in findings)
            {
                var keystone = FindingEmitter.IsSuppressed(Draft(findingRule, findingUrl), [suppression]);
                var mine = SuppressionMatcher.Covers(suppression, findingRule, findingUrl);
                Assert.True(
                    keystone == mine,
                    $"Parity violation: suppression(rule={ruleKey ?? "*"}, url={urlId?.ToString() ?? "*"}, scope={scope ?? "*"}, active={active}) " +
                    $"finding(rule={findingRule}, url={findingUrl?.ToString() ?? "*"}): keystone={keystone}, matcher={mine}");
                checkedCount++;
            }
        }

        Assert.Equal(3 * 3 * 3 * 2 * 6, checkedCount);
    }

    [Fact]
    public void IsSuppressed_SetLevel_MatchesKeystone()
    {
        var suppressions = new[]
        {
            Suppression("rule.a", null, null),
            Suppression(null, 2L, null, active: false),
            Suppression(null, null, "rule.b"),
            Suppression(null, null, null),
        };
        (string RuleKey, long? UrlId)[] findings =
        [
            ("rule.a", 1L), ("rule.a", 2L), ("rule.b", null), ("rule.b", 2L), ("rule.c", 3L),
        ];

        foreach (var (ruleKey, urlId) in findings)
        {
            var keystone = FindingEmitter.IsSuppressed(Draft(ruleKey, urlId), suppressions);
            var mine = SuppressionMatcher.IsSuppressed(ruleKey, urlId, suppressions);
            Assert.True(keystone == mine, $"Set parity violation for ({ruleKey}, {urlId}).");
        }
    }

    [Fact]
    public void NoDimensionRow_SuppressesNothing()
    {
        var blanket = Suppression(null, null, null);

        Assert.False(SuppressionMatcher.Covers(blanket, "any.rule", 1));
        Assert.False(SuppressionMatcher.IsSuppressed("any.rule", null, [blanket]));
    }

    [Fact]
    public void InactiveRow_SuppressesNothing()
    {
        var inactive = Suppression("rule.a", null, null, active: false);

        Assert.False(SuppressionMatcher.Covers(inactive, "rule.a", null));
    }

    [Fact]
    public void ScopeDimension_MatchesAgainstRuleKey_NotUrl()
    {
        var scope = Suppression(null, null, "rule.a");

        Assert.True(SuppressionMatcher.Covers(scope, "rule.a", 99));
        Assert.False(SuppressionMatcher.Covers(scope, "rule.b", 99));
    }

    [Fact]
    public void Matching_IsOrdinalCaseSensitive()
    {
        var suppression = Suppression("Rule.A", null, null);

        Assert.False(SuppressionMatcher.Covers(suppression, "rule.a", null));
        Assert.True(SuppressionMatcher.Covers(suppression, "Rule.A", null));
    }

    [Fact]
    public void BuildSuppressionKey_IsDeterministic_WithNullWildcards()
    {
        var request = new FindingSuppressionRequest("rule.a", null, null);

        var key = FindingSuppressionService.BuildSuppressionKey(request);

        Assert.Equal("rule=rule.a;url=*;scope=*", key);
        Assert.Equal(key, FindingSuppressionService.BuildSuppressionKey(request));
        Assert.NotEqual(key, FindingSuppressionService.BuildSuppressionKey(new FindingSuppressionRequest("rule.a", 1, null)));
    }

    [Fact]
    public void BuildSuppressionKey_LongDimensions_FallBackToSha256()
    {
        var request = new FindingSuppressionRequest(null, null, new string('x', 300));

        var key = FindingSuppressionService.BuildSuppressionKey(request);

        Assert.StartsWith("sha256:", key, StringComparison.Ordinal);
        Assert.True(key.Length <= 191);
        Assert.Equal(key, FindingSuppressionService.BuildSuppressionKey(request));
    }
}
