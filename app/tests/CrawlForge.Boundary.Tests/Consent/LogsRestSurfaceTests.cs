using System.Net;
using System.Text;
using System.Text.Json;
using CrawlForge.Boundary.Tests.Harness;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Boundary.Tests.Consent;

/// <summary>
/// STRICT blocker-tracking test (WP-07-02 → orchestrator-routed DI fix):
/// the logs REST surface must activate and honor the DNS consent gate.
/// Currently RED: LogsController cannot be activated because
/// ILogCorrelationReadModel has no DI forwarding registration
/// (EngineServicesModule.cs:265 registers only ILogCorrelationEngine).
/// This test flips GREEN when the composition fix lands — do not weaken it.
/// </summary>
public sealed class LogsRestSurfaceTests
{
    [Fact]
    public async Task LogsRest_VerifyDns_WithoutConsent_RefusesWithoutSending()
    {
        await using var fixture = await BackendFixture.StartAsync();

        var projects = fixture.Backend.App.Services.GetRequiredService<IWriteRepository<ProjectDto>>();
        var project = await projects.CreateAsync(new ProjectDto(
            0, "LogsRest", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
        var imports = fixture.Backend.App.Services.GetRequiredService<IWriteRepository<LogImportDto>>();
        var import = await imports.CreateAsync(new LogImportDto(
            0, project.Id, LogFileFormat.ApacheNginxCombined,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            "{}", BotVerificationMode.CidrOnly, LogImportStatus.Completed,
            DateTimeOffset.UtcNow, null));

        using var response = await fixture.Http.PostAsync(
            $"/api/logs/{import.Id}/verify-dns",
            new StringContent("""{"consent":false}""", Encoding.UTF8, "application/json"));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"BLOCKER (routed to composition owner): /api/logs/*/verify-dns returned {(int)response.StatusCode}. "
            + "LogsController requires ILogCorrelationReadModel; EngineServicesModule registers only ILogCorrelationEngine. "
            + "Fix: register LogCorrelationEngine once and forward BOTH interfaces.");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = body.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("started").GetBoolean());
        Assert.Contains("consent", data.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
