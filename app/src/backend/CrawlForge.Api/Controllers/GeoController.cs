using System.Text.Json.Nodes;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// GEO / AI-bot governance explorer (FS-CRAWL-002 §13.5): per-host llms.txt
/// validation and the bot-governance matrix. All analysis is local; no remote
/// registry or publishing exists (exclusion E9).
/// </summary>
[Route("api/audits")]
public sealed class GeoController : ApiControllerBase
{
    private readonly IGeoReadinessAnalyzer _geo;
    private readonly IAiBotGovernanceMatrix _governance;
    private readonly IReadRepository<AuditDto> _audits;

    /// <summary>Creates the controller.</summary>
    /// <param name="geo">GEO readiness analyzer.</param>
    /// <param name="governance">AI-bot governance matrix.</param>
    /// <param name="audits">Audit read repository.</param>
    public GeoController(IGeoReadinessAnalyzer geo, IAiBotGovernanceMatrix governance, IReadRepository<AuditDto> audits)
    {
        _geo = geo;
        _governance = governance;
        _audits = audits;
    }

    /// <summary>Lists per-host llms.txt validation results.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="host">Host filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged validation rows.</returns>
    [HttpGet("{id:long}/geo/llms-txt")]
    public async Task<IActionResult> LlmsTxt(
        long id,
        [FromQuery] string? host = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var result = await _geo.AnalyzeAsync(id, cancellationToken);
        var checkedAt = DateTimeOffset.UtcNow;
        var rows = result.HostResults
            .Where(h => host is null || string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase))
            .Select(h => new LlmsTxtValidationWire(
                h.Host,
                $"https://{h.Host}/llms.txt",
                h.IsPresent,
                h.IsWellFormed,
                h.Issues.Concat(h.BrokenLinks.Select(l => $"Broken link: {l}")).ToList(),
                checkedAt))
            .OrderBy(r => r.Host, StringComparer.Ordinal)
            .ToList();

        var page = rows.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        return OkEnvelope(page, new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), rows.Count));
    }

    /// <summary>Lists the bot-governance matrix rows.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="botName">Bot filter.</param>
    /// <param name="host">Host filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged governance rows.</returns>
    [HttpGet("{id:long}/geo/governance")]
    public async Task<IActionResult> Governance(
        long id,
        [FromQuery] string? botName = null,
        [FromQuery] string? host = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var rows = await _governance.EvaluateAsync(id, cancellationToken);
        var projected = rows
            .Select(ToGovernanceRow)
            .Where(r => botName is null || string.Equals(r.BotName, botName, StringComparison.OrdinalIgnoreCase))
            .Where(r => host is null || string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.BotName, StringComparer.Ordinal)
            .ThenBy(r => r.Host, StringComparer.Ordinal)
            .ToList();

        var page = projected.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        return OkEnvelope(page, new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), projected.Count));
    }

    private static BotGovernanceRowWire ToGovernanceRow(GovernanceMatrixRowDto row)
    {
        var evidence = ProjectWireMapper.ParseObject(row.EvidenceJson);
        var host = evidence["host"]?.GetValue<string>();
        var matchedRule = evidence["matchedRule"] as JsonObject;
        bool? robotsAllowed = matchedRule?["allow"]?.GetValue<bool>();
        var directives = evidence["directives"] as JsonArray;
        var notes = directives is { Count: > 0 }
            ? string.Join(", ", directives.Select(d => d?.GetValue<string>() ?? string.Empty))
            : null;
        return new BotGovernanceRowWire(
            row.AgentName,
            host,
            row.Verdict == GovernanceVerdict.Allow,
            robotsAllowed,
            null,
            notes);
    }
}
