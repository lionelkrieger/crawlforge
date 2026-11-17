using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Per-host AIMD state explorer and manual rate override (FS-CRAWL-002 §3.7).
/// Reads the live politeness controller's checkpoint state; adjustment history
/// comes from the persisted per-audit throttle events. The locked override
/// surface pins rate only (concurrency is accepted but not separately pinned).
/// </summary>
[Route("api/audits")]
public sealed class HostsController : ApiControllerBase
{
    private readonly IAimdPolitenessController _aimd;
    private readonly IReadRepository<HostThrottleEventDto> _events;
    private readonly IReadRepository<AuditDto> _audits;

    /// <summary>Creates the controller.</summary>
    /// <param name="aimd">Live AIMD politeness controller.</param>
    /// <param name="events">Throttle event read repository.</param>
    /// <param name="audits">Audit read repository.</param>
    public HostsController(
        IAimdPolitenessController aimd,
        IReadRepository<HostThrottleEventDto> events,
        IReadRepository<AuditDto> audits)
    {
        _aimd = aimd;
        _events = events;
        _audits = audits;
    }

    /// <summary>Lists per-host AIMD state, optionally with adjustment history.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="includeHistory">Whether to include adjustment history.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged host-state rows.</returns>
    [HttpGet("{id:long}/hosts")]
    public async Task<IActionResult> ListHosts(
        long id,
        [FromQuery] bool includeHistory = false,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        var events = includeHistory
            ? await _events.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = id.ToString() }), cancellationToken)
            : null;
        var eventsByHost = events?.Items
            .GroupBy(e => e.Host, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Timestamp).ToList(), StringComparer.OrdinalIgnoreCase);

        var hosts = _aimd.CreateCheckpointState().Hosts
            .OrderBy(h => h.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var page = hosts.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        var items = page.Select(h => ToHostState(h, eventsByHost)).ToList();
        return OkEnvelope(items, new PaginationMetadata(Math.Max(0, offset), Math.Clamp(limit, 1, MaxPageSize), hosts.Count));
    }

    /// <summary>Pins or clears a manual host rate override.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="host">Target host.</param>
    /// <param name="request">Override request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated host state.</returns>
    [HttpPost("{id:long}/hosts/{host}/override")]
    public async Task<IActionResult> OverrideHost(long id, string host, [FromBody] HostOverrideRequest request, CancellationToken cancellationToken)
    {
        if (await _audits.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Audit '{id}' was not found.");
        }

        if (request.Rate is < 0)
        {
            return ValidationError("Rate must be non-negative.", [new ApiFieldError("rate", "Provide a non-negative rate or null to clear.")]);
        }

        _aimd.SetManualOverride(host, request.Rate);
        var state = _aimd.GetState(host);
        return OkEnvelope(ToHostState(state, null));
    }

    private static HostStateWire ToHostState(HostThrottleState state, IReadOnlyDictionary<string, List<HostThrottleEventDto>>? eventsByHost)
    {
        List<HostThrottleEventDto>? history = null;
        var throttled = false;
        if (eventsByHost is not null && eventsByHost.TryGetValue(state.Host, out var hostEvents))
        {
            history = hostEvents;
            var last = hostEvents[^1];
            throttled = last.NewRate < last.PreviousRate;
        }

        return new HostStateWire(
            state.Host,
            state.CurrentRate,
            state.CurrentConcurrency,
            state.EwmaTtfbMilliseconds,
            state.CurrentRate,
            throttled,
            state.ManualOverrideRate is { } pinned ? new HostRateOverrideWire(pinned, null) : null,
            history?.Select(e => new HostAdjustmentWire(e.Timestamp, e.Reason, e.PreviousRate, e.NewRate, 0, 0, null)).ToList());
    }
}
