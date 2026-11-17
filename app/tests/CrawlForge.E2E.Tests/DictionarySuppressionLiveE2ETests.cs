using System.Net;
using System.Net.Http.Json;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.E2E.Tests.Fixtures;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// D13 live wiring proof (criterion #13 suppression facet): user-dictionary
/// entries persisted through the REAL REST surface must suppress spelling
/// findings in the host's live analyzer — immediately after mutation and
/// across a backend restart (startup participant). The unit proof passed via
/// ctor injection; this exercises the composed pipeline end to end.
/// </summary>
public sealed class DictionarySuppressionLiveE2ETests
{
    private static readonly string[] Misspellings = ["Ths", "qck", "brwn", "fx"];

    [Fact]
    public async Task RestDictionary_SuppressesSpellingFindings_Live_AndRestoresOnRemoval()
    {
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
            routes.Map("/", OriginResponse.Html(FixturePages.Page(
                "Dictionary fixture",
                "<p>Ths qck brwn fx jumps over the lazy dog while the rest of the sentence stays perfectly ordinary.</p>"))));
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);

        // Baseline audit: the misspellings produce content.spelling with token evidence.
        var baselineAudit = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(baselineAudit, AuditStatus.Completed, TimeSpan.FromSeconds(60));
        var baseline = await backend.ListByAuditAsync<FindingDto>(baselineAudit);
        var spelling = Assert.Single(baseline, f => f.RuleKey == "content.spelling");
        Assert.Contains("Ths", spelling.EvidenceJson);
        Assert.Equal(FindingSeverity.Warning, spelling.Severity);

        // Accept every misspelling through the real REST dictionary surface.
        foreach (var word in Misspellings)
        {
            var added = await backend.Http.PostAsJsonAsync("/api/dictionary", new { word, language = "en" });
            Assert.True(added.StatusCode == HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        }

        // A fresh live audit must now suppress the spelling finding entirely.
        var suppressedAudit = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(suppressedAudit, AuditStatus.Completed, TimeSpan.FromSeconds(60));
        var suppressed = await backend.ListByAuditAsync<FindingDto>(suppressedAudit);
        Assert.DoesNotContain(suppressed, f => f.RuleKey == "content.spelling");

        // Removal restores the finding (live snapshot refresh on delete).
        foreach (var word in Misspellings)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/dictionary")
            {
                Content = JsonContent.Create(new { word, language = "en" })
            };
            var removed = await backend.Http.SendAsync(request);
            Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
        }

        var restoredAudit = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(restoredAudit, AuditStatus.Completed, TimeSpan.FromSeconds(60));
        var restored = await backend.ListByAuditAsync<FindingDto>(restoredAudit);
        Assert.Contains(restored, f => f.RuleKey == "content.spelling");
    }

    [Fact]
    public async Task PersistedDictionary_SurvivesRestart_SuppressesOnFirstAudit()
    {
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
            routes.Map("/", OriginResponse.Html(FixturePages.Page(
                "Dictionary restart fixture",
                "<p>Ths qck brwn fx jumps over the lazy dog while the rest of the sentence stays perfectly ordinary.</p>"))));

        var dataDirectory = Path.Combine(Path.GetTempPath(), "crawlforge-e2e", Guid.NewGuid().ToString("N"));
        long projectId;
        long configurationId;
        await using (var first = await E2EBackend.StartAsync(dataDirectory: dataDirectory))
        {
            (projectId, configurationId) = await first.SeedProjectAsync(origin.BaseUrl);
            foreach (var word in Misspellings)
            {
                var added = await first.Http.PostAsJsonAsync("/api/dictionary", new { word, language = "en" });
                Assert.True(added.StatusCode == HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
            }
            first.PreserveDataDirectory = true;
        }

        // Restart on the same data directory: the startup participant must load
        // the persisted dictionary before any audit runs.
        await using var restarted = await E2EBackend.StartAsync(dataDirectory: dataDirectory);
        var auditId = await restarted.StartAuditAsync(projectId, configurationId);
        await restarted.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));
        var findings = await restarted.ListByAuditAsync<FindingDto>(auditId);
        Assert.DoesNotContain(findings, f => f.RuleKey == "content.spelling");
    }
}
