using System.Text.Json;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Project CRUD (FS-CRAWL-001 §10.2): list with search and pagination, create,
/// read, subset update, and soft delete. Additive extension settings ride the
/// locked additive columns (FS-CRAWL-002 Appendix B). Local data only.
/// </summary>
[Route("api/projects")]
public sealed class ProjectsController : ApiControllerBase
{
    private static readonly string[] SortFields = ["id", "name", "createdAt", "updatedAt"];

    private readonly IReadRepository<ProjectDto> _reads;
    private readonly IWriteRepository<ProjectDto> _writes;
    private readonly IReadRepository<ContentScopeProfileDto> _profiles;

    /// <summary>Creates the controller.</summary>
    /// <param name="reads">Project read repository.</param>
    /// <param name="writes">Project write repository.</param>
    /// <param name="profiles">Content-scope profile read repository.</param>
    public ProjectsController(
        IReadRepository<ProjectDto> reads,
        IWriteRepository<ProjectDto> writes,
        IReadRepository<ContentScopeProfileDto> profiles)
    {
        _reads = reads;
        _writes = writes;
        _profiles = profiles;
    }

    /// <summary>Lists projects with optional search text and pagination.</summary>
    /// <param name="search">Optional case-insensitive name/description search text.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="sort">Optional sort field.</param>
    /// <param name="order">Sort direction: asc or desc.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged project summaries.</returns>
    [HttpGet]
    public async Task<ObjectResult> ListProjectsAsync(
        [FromQuery] string? search,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryBindSort(sort, order, SortFields, out var sortBy, out var descending, out var sortError))
        {
            return ValidationError(sortError!);
        }

        var query = BindListQuery(offset, limit, sortBy, descending);
        if (string.IsNullOrWhiteSpace(search))
        {
            var page = await _reads.ListAsync(query, cancellationToken);
            var summaries = page.Items.Select(ProjectWireMapper.ToSummary).ToList();
            return OkEnvelope(summaries, PaginationOf(page));
        }

        // Search is a local contains-match over name/description; the repository
        // surface is equality-filtered, so the match runs in the API layer.
        var all = await _reads.ListAsync(new ListQuery(0, 10_000, null, sortBy, descending), cancellationToken);
        var matched = all.Items
            .Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (p.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        var items = matched.Skip(query.Offset).Take(query.Limit).Select(ProjectWireMapper.ToSummary).ToList();
        return OkEnvelope(items, new PaginationMetadata(query.Offset, query.Limit, matched.Count));
    }

    /// <summary>Creates a project.</summary>
    /// <param name="request">Creation payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created project.</returns>
    [HttpPost]
    public async Task<ObjectResult> CreateProjectAsync([FromBody] ProjectCreateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationError("The project name is required.", [new ApiFieldError("name", "Must not be empty.")]);
        }

        var now = DateTimeOffset.UtcNow;
        var project = await _writes.CreateAsync(new ProjectDto(
            0,
            request.Name.Trim(),
            request.Description,
            request.Tags is null ? null : JsonSerializer.Serialize(request.Tags),
            now,
            now,
            request.ScopeSummary,
            false), cancellationToken);

        return CreatedEnvelope(ProjectWireMapper.ToProject(project, await LoadProfilesAsync(project.Id, cancellationToken)));
    }

    /// <summary>Reads one project including extension settings.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The project record.</returns>
    [HttpGet("{id:long}")]
    public async Task<ObjectResult> GetProjectAsync(long id, CancellationToken cancellationToken)
    {
        var project = await _reads.GetByIdAsync(id, cancellationToken);
        if (project is null || project.IsDeleted)
        {
            return NotFoundError($"Project {id} does not exist.");
        }

        return OkEnvelope(ProjectWireMapper.ToProject(project, await LoadProfilesAsync(id, cancellationToken)));
    }

    /// <summary>Updates any subset of project fields and extension settings.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="request">Subset payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated project.</returns>
    [HttpPut("{id:long}")]
    public async Task<ObjectResult> UpdateProjectAsync(long id, [FromBody] ProjectUpdateRequest request, CancellationToken cancellationToken)
    {
        var project = await _reads.GetByIdAsync(id, cancellationToken);
        if (project is null || project.IsDeleted)
        {
            return NotFoundError($"Project {id} does not exist.");
        }

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ValidationError("The project name must not be empty.", [new ApiFieldError("name", "Must not be empty.")]);
            }

            project = project with { Name = request.Name.Trim() };
        }

        if (request.Description is not null)
        {
            project = project with { Description = request.Description.Length == 0 ? null : request.Description };
        }

        if (request.Tags is not null)
        {
            project = project with { TagsJson = JsonSerializer.Serialize(request.Tags) };
        }

        if (request.ScopeSummary is not null)
        {
            project = project with { ScopeSummary = request.ScopeSummary.Length == 0 ? null : request.ScopeSummary };
        }

        project = ProjectWireMapper.ApplyExtensions(project, request.ContentScope, request.Rewrite, request.Consent, request.ZoneWeights);
        project = project with { UpdatedAt = DateTimeOffset.UtcNow };

        var updated = await _writes.UpdateAsync(project, cancellationToken);
        return OkEnvelope(ProjectWireMapper.ToProject(updated, await LoadProfilesAsync(id, cancellationToken)));
    }

    /// <summary>Soft-deletes a project; history rows are retained.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deletion confirmation.</returns>
    [HttpDelete("{id:long}")]
    public async Task<ObjectResult> DeleteProjectAsync(long id, CancellationToken cancellationToken)
    {
        var project = await _reads.GetByIdAsync(id, cancellationToken);
        if (project is null || project.IsDeleted)
        {
            return NotFoundError($"Project {id} does not exist.");
        }

        await _writes.UpdateAsync(project with { IsDeleted = true, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
        return OkEnvelope(new DeletionConfirmationWire(id.ToString(), true));
    }

    private async Task<IReadOnlyList<ContentScopeProfileWire>?> LoadProfilesAsync(long projectId, CancellationToken cancellationToken)
    {
        var page = await _profiles.ListAsync(new ListQuery(0, MaxPageSize, new Dictionary<string, string> { ["projectId"] = projectId.ToString() }), cancellationToken);
        if (page.Items.Count == 0)
        {
            return null;
        }

        return page.Items
            .Select(p => new ContentScopeProfileWire(
                p.Id.ToString(),
                p.Name,
                ProjectWireMapper.ParseStringList(p.IncludeSelectorsJson),
                ProjectWireMapper.ParseStringList(p.ExcludeSelectorsJson)))
            .ToList();
    }
}
