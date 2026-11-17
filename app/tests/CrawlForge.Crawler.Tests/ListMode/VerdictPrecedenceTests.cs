using System.Text.Json;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// FS-CRAWL-002 §10.3 verdict-precedence evidence: fixture-driven scripted hop
/// chains (no network) proving the exact five-tier order — chain-exceeded →
/// final-non-200 → target-mismatch → canonical-mismatch → pass — with
/// precedence collisions resolved deterministically (first failure wins) and
/// full evidence retained regardless of verdict.
/// </summary>
public class VerdictPrecedenceTests
{
    private const string Source = "https://old.example.com/a";
    private const string Expected = "https://new.example.com/a";

    private static RedirectMappingDto Map(long auditId, int ordinal = 0, string? source = null, string? expected = null) =>
        new(ordinal + 1, auditId, source ?? Source, expected ?? Expected, ordinal);

    private static (RedirectMatrixValidator Validator, RecordingHub Hub) CreateValidator(
        DatabaseFixture fixture,
        ScriptedHttpFetcher fetcher,
        ListModeOptions? options = null)
    {
        var hub = new RecordingHub();
        var validator = new RedirectMatrixValidator(fetcher, fixture.Provider, new TestCanonicalizer(), hub, options);
        return (validator, hub);
    }

    /// <summary>Tier 5: a clean redirect to the expected target passes with the hop recorded.</summary>
    [Fact]
    public async Task Clean_Redirect_Passes()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200, body: "<html><body>ok</body></html>"));
        var (validator, _) = CreateValidator(fixture, fetcher);

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        var row = Assert.Single(results);
        Assert.Equal(RedirectMatrixVerdict.Pass, row.Verdict);
        Assert.Equal(Expected, row.ActualFinalUrl);
        Assert.Equal(1, row.HopCount);
        var hop = Assert.Single(row.Chain);
        Assert.Equal(0, hop.Ordinal);
        Assert.Equal(Source, hop.FromUrl);
        Assert.Equal(Expected, hop.ToUrl);
        Assert.Equal(301, hop.StatusCode);
    }

    /// <summary>Tier 1 beats everything: an over-long chain verdicts chain-exceeded
    /// even when the final status is non-200 and the target is wrong.</summary>
    [Fact]
    public async Task Chain_Exceeded_Beats_Non200_And_Mismatch()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: "https://old.example.com/b"))
            .Script("https://old.example.com/b", ScriptedHttpFetcher.Response("https://old.example.com/b", 302, location: "https://old.example.com/c"))
            .Script("https://old.example.com/c", ScriptedHttpFetcher.Response("https://old.example.com/c", 307, location: "https://wrong.example.com/x"))
            .Script("https://wrong.example.com/x", ScriptedHttpFetcher.Response("https://wrong.example.com/x", 404));
        var (validator, _) = CreateValidator(fixture, fetcher, new ListModeOptions(RedirectHopLimit: 2));

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        var row = Assert.Single(results);
        Assert.Equal(RedirectMatrixVerdict.ChainExceeded, row.Verdict);
        Assert.Equal(3, row.HopCount);
        Assert.DoesNotContain("https://wrong.example.com/x", fetcher.RequestedUrls);
    }

    /// <summary>Tier 1: a redirect loop is chain-exceeded.</summary>
    [Fact]
    public async Task Redirect_Loop_Is_Chain_Exceeded()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 302, location: "https://old.example.com/b"))
            .Script("https://old.example.com/b", ScriptedHttpFetcher.Response("https://old.example.com/b", 302, location: Source));
        var (validator, _) = CreateValidator(fixture, fetcher);

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        var row = Assert.Single(results);
        Assert.Equal(RedirectMatrixVerdict.ChainExceeded, row.Verdict);
        Assert.Equal(2, row.HopCount);
    }

    /// <summary>Hop ceiling boundary: exactly the limit passes, one more exceeds.</summary>
    [Theory]
    [InlineData(2, RedirectMatrixVerdict.Pass)]
    [InlineData(1, RedirectMatrixVerdict.ChainExceeded)]
    public async Task Hop_Ceiling_Boundary_Is_Exact(int hopLimit, RedirectMatrixVerdict expectedVerdict)
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: "https://old.example.com/b"))
            .Script("https://old.example.com/b", ScriptedHttpFetcher.Response("https://old.example.com/b", 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200));
        var (validator, _) = CreateValidator(fixture, fetcher, new ListModeOptions(RedirectHopLimit: hopLimit));

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        Assert.Equal(expectedVerdict, Assert.Single(results).Verdict);
    }

    /// <summary>Tier 2 beats tier 3: a non-200 final status verdicts final-non-200
    /// even though the final target is also wrong.</summary>
    [Fact]
    public async Task Final_Non200_Beats_Target_Mismatch()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        const string wrong = "https://wrong.example.com/a";
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: wrong))
            .Script(wrong, ScriptedHttpFetcher.Response(wrong, 404));
        var (validator, _) = CreateValidator(fixture, fetcher);

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        var row = Assert.Single(results);
        Assert.Equal(RedirectMatrixVerdict.FinalNon200, row.Verdict);
        Assert.Equal(wrong, row.ActualFinalUrl);
    }

    /// <summary>Tier 3 beats tier 4: a wrong final destination verdicts
    /// target-mismatch even when the canonical also disagrees.</summary>
    [Fact]
    public async Task Target_Mismatch_Beats_Canonical_Mismatch()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        const string wrong = "https://wrong.example.com/a";
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: wrong))
            .Script(wrong, ScriptedHttpFetcher.Response(wrong, 200,
                body: "<html><head><link rel=\"canonical\" href=\"https://other.example.com/z\"></head></html>"));
        var (validator, _) = CreateValidator(fixture, fetcher);

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        Assert.Equal(RedirectMatrixVerdict.TargetMismatch, Assert.Single(results).Verdict);
    }

    /// <summary>Tier 4: correct destination but a disagreeing declared canonical
    /// verdicts canonical-mismatch (Warning).</summary>
    [Fact]
    public async Task Disagreeing_Canonical_Is_Canonical_Mismatch()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200,
                body: "<html><head><link rel=\"canonical\" href=\"https://other.example.com/z\"></head></html>"));
        var (validator, _) = CreateValidator(fixture, fetcher);

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        var row = Assert.Single(results);
        Assert.Equal(RedirectMatrixVerdict.CanonicalMismatch, row.Verdict);
        Assert.Equal("https://other.example.com/z", row.CanonicalUrl);
    }

    /// <summary>A matching declared canonical keeps the row at pass.</summary>
    [Fact]
    public async Task Matching_Canonical_Passes()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200,
                body: $"<html><head><link href=\"{Expected}\" rel=\"canonical\"></head></html>"));
        var (validator, _) = CreateValidator(fixture, fetcher);

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        Assert.Equal(RedirectMatrixVerdict.Pass, Assert.Single(results).Verdict);
    }

    /// <summary>The full multi-hop chain is recorded in order (§5.4).</summary>
    [Fact]
    public async Task Full_Hop_Chain_Is_Recorded()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: "https://old.example.com/b"))
            .Script("https://old.example.com/b", ScriptedHttpFetcher.Response("https://old.example.com/b", 302, location: "https://old.example.com/c"))
            .Script("https://old.example.com/c", ScriptedHttpFetcher.Response("https://old.example.com/c", 308, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200));
        var (validator, _) = CreateValidator(fixture, fetcher);

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        var row = Assert.Single(results);
        Assert.Equal(RedirectMatrixVerdict.Pass, row.Verdict);
        Assert.Equal(3, row.HopCount);
        Assert.Equal(new[] { 0, 1, 2 }, row.Chain.Select(h => h.Ordinal));
        Assert.Equal(new[] { 301, 302, 308 }, row.Chain.Select(h => h.StatusCode));
        Assert.Equal(Expected, row.Chain[2].ToUrl);
    }

    /// <summary>Transport failures verdict final-non-200 with the error retained as evidence.</summary>
    [Fact]
    public async Task Transport_Failure_Is_Final_Non200()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher().Fail(Source, new HttpRequestException("connection reset"));
        var (validator, _) = CreateValidator(fixture, fetcher);

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        var row = Assert.Single(results);
        Assert.Equal(RedirectMatrixVerdict.FinalNon200, row.Verdict);
        Assert.Null(row.ActualFinalUrl);
        Assert.Equal(0, row.HopCount);

        var findings = await RedirectMatrixResults.ReadFindingsAsync(fixture.Provider, auditId);
        var finding = Assert.Single(findings);
        var evidence = JsonSerializer.Deserialize<MatrixRowEvidence>(finding.EvidenceJson,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal("connection reset", evidence!.TransportError);
    }

    /// <summary>Relative Location headers resolve against the current URL.</summary>
    [Fact]
    public async Task Relative_Location_Resolves()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        const string legacySource = "https://old.example.com/legacy";
        var fetcher = new ScriptedHttpFetcher()
            .Script(legacySource, ScriptedHttpFetcher.Response(legacySource, 302, location: "/a"))
            .Script("https://old.example.com/a", ScriptedHttpFetcher.Response("https://old.example.com/a", 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200));
        var (validator, _) = CreateValidator(fixture, fetcher);

        var results = await validator.ValidateAsync(auditId, [Map(auditId, source: legacySource)]);

        var row = Assert.Single(results);
        Assert.Equal(RedirectMatrixVerdict.Pass, row.Verdict);
        Assert.Equal("https://old.example.com/a", row.Chain[0].ToUrl);
    }

    /// <summary>Any 2xx final status satisfies the status tier (204 included).</summary>
    [Fact]
    public async Task Non200_2xx_Status_Satisfies_Status_Tier()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 204));
        var (validator, _) = CreateValidator(fixture, fetcher);

        var results = await validator.ValidateAsync(auditId, [Map(auditId)]);

        Assert.Equal(RedirectMatrixVerdict.Pass, Assert.Single(results).Verdict);
    }

    /// <summary>Rows validate in ordinal order regardless of input order.</summary>
    [Fact]
    public async Task Rows_Validate_In_Ordinal_Order()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        var fetcher = new ScriptedHttpFetcher
        {
            DefaultResponse = ScriptedHttpFetcher.Response(Expected, 200),
        };
        fetcher.Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: Expected));
        fetcher.Script("https://old.example.com/second", ScriptedHttpFetcher.Response("https://old.example.com/second", 301, location: Expected));
        var (validator, _) = CreateValidator(fixture, fetcher);

        var mappings = new[]
        {
            Map(auditId, ordinal: 1, source: "https://old.example.com/second"),
            Map(auditId, ordinal: 0),
        };

        var results = await validator.ValidateAsync(auditId, mappings);

        Assert.Equal(0, results[0].Mapping.Ordinal);
        Assert.Equal(1, results[1].Mapping.Ordinal);
    }

    /// <summary>Every verdict persists its locked rule key and §10.3 severity.</summary>
    [Fact]
    public async Task Findings_Carry_Rule_Keys_And_Severities()
    {
        using var fixture = await DatabaseFixture.CreateAsync();
        var auditId = await fixture.SeedAuditAsync();
        const string wrong = "https://wrong.example.com/a";
        var fetcher = new ScriptedHttpFetcher()
            .Script(Source, ScriptedHttpFetcher.Response(Source, 301, location: Expected))
            .Script(Expected, ScriptedHttpFetcher.Response(Expected, 200))
            .Script("https://old.example.com/b", ScriptedHttpFetcher.Response("https://old.example.com/b", 301, location: wrong))
            .Script(wrong, ScriptedHttpFetcher.Response(wrong, 500));
        var (validator, _) = CreateValidator(fixture, fetcher);

        await validator.ValidateAsync(auditId, [
            Map(auditId, ordinal: 0),
            Map(auditId, ordinal: 1, source: "https://old.example.com/b"),
        ]);

        var findings = await RedirectMatrixResults.ReadFindingsAsync(fixture.Provider, auditId);
        Assert.Equal(2, findings.Count);
        Assert.Equal(FindingRuleKeys.RedirectMatrixPass, findings[0].RuleKey);
        Assert.Equal(FindingSeverity.Pass, findings[0].Severity);
        Assert.Equal(FindingRuleKeys.RedirectMatrixFinalNon200, findings[1].RuleKey);
        Assert.Equal(FindingSeverity.Error, findings[1].Severity);
    }
}
