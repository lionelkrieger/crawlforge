using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Finding suppressions (FS-CRAWL-001 §7.5): project-scoped, reversible,
/// excluded from counts and scores. Delegates to the locked scoring service;
/// the REST layer only validates input and projects the wire state.
/// </summary>
[Route("api/projects/{id:long}/suppressions")]
public sealed class SuppressionsController : ApiControllerBase
{
    private readonly IReadRepository<ProjectDto> _projects;
    private readonly IFindingSuppressionService _suppressions;

    /// <summary>Creates the controller.</summary>
    /// <param name="projects">Project read repository (parent existence checks).</param>
    /// <param name="suppressions">Locked finding-suppression service.</param>
    public SuppressionsController(IReadRepository<ProjectDto> projects, IFindingSuppressionService suppressions)
    {
        _projects = projects;
        _suppressions = suppressions;
    }

    /// <summary>Suppresses a rule key and/or finding scope within the project.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="request">Suppression target.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted suppression state.</returns>
    [HttpPost]
    public async Task<ObjectResult> CreateSuppressionAsync(long id, [FromBody] SuppressionCreateRequest request, CancellationToken cancellationToken)
    {
        var project = await _projects.GetByIdAsync(id, cancellationToken);
        if (project is null || project.IsDeleted)
        {
            return NotFoundError($"Project {id} does not exist.");
        }

        long? urlId = null;
        if (request.Scope?.UrlId is not null)
        {
            if (!long.TryParse(request.Scope.UrlId, out var parsedUrlId))
            {
                return ValidationError("The suppression scope URL identifier is invalid.", [new ApiFieldError("scope.urlId", "Must be a numeric identifier.")]);
            }

            urlId = parsedUrlId;
        }

        if (string.IsNullOrWhiteSpace(request.RuleKey) && urlId is null && string.IsNullOrWhiteSpace(request.Scope?.UrlPattern))
        {
            return ValidationError("A suppression requires a rule key and/or a scope.", [new ApiFieldError("ruleKey", "Provide a rule key or a scope.")]);
        }

        var suppression = await _suppressions.SuppressAsync(
            id,
            new FindingSuppressionRequest(request.RuleKey, urlId, request.Scope?.UrlPattern),
            cancellationToken);
        return CreatedEnvelope(ToWire(suppression));
    }

    /// <summary>Reverses a suppression by its stable key.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="key">Suppression key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The inactive suppression state.</returns>
    [HttpDelete("{key}")]
    public async Task<ObjectResult> DeleteSuppressionAsync(long id, string key, CancellationToken cancellationToken)
    {
        var project = await _projects.GetByIdAsync(id, cancellationToken);
        if (project is null || project.IsDeleted)
        {
            return NotFoundError($"Project {id} does not exist.");
        }

        var removed = await _suppressions.UnsuppressAsync(id, key, cancellationToken);
        if (!removed)
        {
            return NotFoundError($"Suppression '{key}' does not exist or is already inactive.");
        }

        var rows = await _suppressions.ListAsync(id, cancellationToken);
        var row = rows.FirstOrDefault(s => string.Equals(s.SuppressionKey, key, StringComparison.Ordinal));
        return OkEnvelope(row is not null
            ? ToWire(row)
            : new SuppressionStateWire(key, id.ToString(), null, null, false, DateTimeOffset.UtcNow));
    }

    private static SuppressionStateWire ToWire(FindingSuppressionDto suppression)
    {
        var scope = suppression.UrlId is null && suppression.FindingScope is null
            ? null
            : new SuppressionScopeWire(suppression.UrlId?.ToString(), suppression.FindingScope);
        return new SuppressionStateWire(
            suppression.SuppressionKey,
            suppression.ProjectId.ToString(),
            suppression.RuleKey,
            scope,
            suppression.IsSuppressed,
            suppression.CreatedAt);
    }
}
