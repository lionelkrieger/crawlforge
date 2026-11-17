using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Saved query-builder definitions (FS-CRAWL-001 §8.7). Storage uses the
/// locked saved_views table with the API-owned surface key "query-builder";
/// the rule tree rides FilterJson unchanged. PUT/DELETE carry the identifier
/// in the body/query string per the frozen route catalog (no {id} segment).
/// Timestamps live in the API-surface metadata sidecar.
/// </summary>
[Route("api/queries")]
public sealed class QueriesController : ApiControllerBase
{
    private const string Surface = "query-builder";
    private const int MaxTreeDepth = 16;

    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "eq", "neq", "gt", "gte", "lt", "lte", "contains", "startsWith", "endsWith", "in", "matches",
    };

    private readonly IReadRepository<SavedViewDto> _reads;
    private readonly IWriteRepository<SavedViewDto> _writes;
    private readonly ApiSurfaceMetadataStore _metadata;

    /// <summary>Creates the controller.</summary>
    /// <param name="reads">Saved-view read repository.</param>
    /// <param name="writes">Saved-view write repository.</param>
    /// <param name="settingsReads">Settings read repository (metadata sidecar).</param>
    /// <param name="settingsWrites">Settings write repository (metadata sidecar).</param>
    public QueriesController(
        IReadRepository<SavedViewDto> reads,
        IWriteRepository<SavedViewDto> writes,
        IReadRepository<ApplicationSettingsDto> settingsReads,
        IWriteRepository<ApplicationSettingsDto> settingsWrites)
    {
        _reads = reads;
        _writes = writes;
        _metadata = new ApiSurfaceMetadataStore(settingsReads, settingsWrites);
    }

    /// <summary>Lists saved queries with name search and pagination.</summary>
    /// <param name="search">Optional name search.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="sort">Optional sort field.</param>
    /// <param name="order">Sort direction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged saved queries.</returns>
    [HttpGet]
    public async Task<ObjectResult> ListAsync(
        [FromQuery] string? search,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryBindSort(sort, order, ["id", "name"], out var sortBy, out var descending, out var sortError))
        {
            return ValidationError(sortError!);
        }

        var filters = new Dictionary<string, string>(StringComparer.Ordinal) { ["targetSurface"] = Surface };
        var root = await _metadata.LoadAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(search))
        {
            var page = await _reads.ListAsync(BindListQuery(offset, limit, sortBy, descending, filters), cancellationToken);
            return OkEnvelope(page.Items.Select(v => ToWire(v, root)).ToList(), PaginationOf(page));
        }

        var all = await _reads.ListAsync(new ListQuery(0, 10_000, filters, sortBy, descending), cancellationToken);
        var matched = all.Items.Where(v => v.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        var items = matched.Skip(offset).Take(Math.Clamp(limit, 1, MaxPageSize)).Select(v => ToWire(v, root)).ToList();
        return OkEnvelope(items, new PaginationMetadata(offset, limit, matched.Count));
    }

    /// <summary>Creates a saved query.</summary>
    /// <param name="request">Name and rule tree.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Created query.</returns>
    [HttpPost]
    public async Task<ObjectResult> CreateAsync([FromBody] SavedQueryCreateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationError("A query name is required.", [new ApiFieldError("name", "Provide a name.")]);
        }

        if (!ValidateRuleTree(request.RuleTree, 0, out var treeError))
        {
            return ValidationError(treeError!, [new ApiFieldError("ruleTree", "Provide a valid rule tree.")]);
        }

        var created = new SavedViewDto(0, request.Name.Trim(), Surface, request.RuleTree.GetRawText());
        var id = (await _writes.CreateAsync(created, cancellationToken)).Id;

        var now = DateTimeOffset.UtcNow;
        var root = await _metadata.LoadAsync(cancellationToken);
        ApiSurfaceMetadataStore.WriteEntry(root, "queries", id.ToString(), new ApiSurfaceMetadataStore.Entry(null, now, now));
        await _metadata.SaveAsync(root, cancellationToken);

        return CreatedEnvelope(ToWire(created with { Id = id }, root));
    }

    /// <summary>Updates a saved query (identifier in body).</summary>
    /// <param name="request">Identifier and fields to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated query.</returns>
    [HttpPut]
    public async Task<ObjectResult> UpdateAsync([FromBody] SavedQueryUpdateRequest request, CancellationToken cancellationToken)
    {
        if (!long.TryParse(request.Id, out var id))
        {
            return ValidationError("A query identifier is required.", [new ApiFieldError("id", "Provide the query identifier.")]);
        }

        var existing = await FindAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFoundError($"Query {request.Id} does not exist.");
        }

        var filterJson = existing.FilterJson;
        if (request.RuleTree is { } tree)
        {
            if (!ValidateRuleTree(tree, 0, out var treeError))
            {
                return ValidationError(treeError!, [new ApiFieldError("ruleTree", "Provide a valid rule tree.")]);
            }

            filterJson = tree.GetRawText();
        }

        var updated = existing with
        {
            Name = request.Name is null ? existing.Name : request.Name.Trim(),
            FilterJson = filterJson,
        };
        if (string.IsNullOrWhiteSpace(updated.Name))
        {
            return ValidationError("A query name is required.", [new ApiFieldError("name", "Provide a name.")]);
        }

        await _writes.UpdateAsync(updated, cancellationToken);

        var root = await _metadata.LoadAsync(cancellationToken);
        var entry = ApiSurfaceMetadataStore.ReadEntry(root, "queries", id.ToString());
        var now = DateTimeOffset.UtcNow;
        ApiSurfaceMetadataStore.WriteEntry(root, "queries", id.ToString(), new ApiSurfaceMetadataStore.Entry(null, entry?.CreatedAt ?? now, now));
        await _metadata.SaveAsync(root, cancellationToken);

        return OkEnvelope(ToWire(updated, root));
    }

    /// <summary>Deletes a saved query (identifier in query string).</summary>
    /// <param name="id">Query identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deletion confirmation.</returns>
    [HttpDelete]
    public async Task<ObjectResult> DeleteAsync([FromQuery] string? id, CancellationToken cancellationToken)
    {
        if (!long.TryParse(id, out var parsedId))
        {
            return ValidationError("A query identifier is required.", [new ApiFieldError("id", "Provide the query identifier.")]);
        }

        if (await FindAsync(parsedId, cancellationToken) is null)
        {
            return NotFoundError($"Query {id} does not exist.");
        }

        await _writes.DeleteAsync(parsedId, cancellationToken);
        var root = await _metadata.LoadAsync(cancellationToken);
        ApiSurfaceMetadataStore.RemoveEntry(root, "queries", parsedId.ToString());
        await _metadata.SaveAsync(root, cancellationToken);
        return OkEnvelope(new DeletionConfirmationWire(parsedId.ToString(), true));
    }

    private async Task<SavedViewDto?> FindAsync(long id, CancellationToken cancellationToken)
    {
        var view = await _reads.GetByIdAsync(id, cancellationToken);
        return view is { TargetSurface: Surface } ? view : null;
    }

    private static SavedQueryWire ToWire(SavedViewDto view, JsonObject root)
    {
        var entry = ApiSurfaceMetadataStore.ReadEntry(root, "queries", view.Id.ToString());
        var fallback = DateTimeOffset.UnixEpoch;
        using var document = JsonDocument.Parse(view.FilterJson);
        return new SavedQueryWire(
            view.Id.ToString(),
            view.Name,
            document.RootElement.Clone(),
            entry?.CreatedAt ?? fallback,
            entry?.UpdatedAt ?? fallback);
    }

    private static bool ValidateRuleTree(JsonElement node, int depth, out string? error)
    {
        error = null;
        if (depth > MaxTreeDepth)
        {
            error = "The rule tree is nested too deeply.";
            return false;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            error = "A rule node must be an object.";
            return false;
        }

        var kind = node.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
        if (kind == "condition")
        {
            if (!node.TryGetProperty("field", out var field) || string.IsNullOrWhiteSpace(field.GetString()))
            {
                error = "A condition requires a field.";
                return false;
            }

            var op = node.TryGetProperty("operator", out var opElement) ? opElement.GetString() : null;
            if (op is null || !Operators.Contains(op))
            {
                error = $"Unknown comparison operator '{op}'.";
                return false;
            }

            if (!node.TryGetProperty("value", out _))
            {
                error = "A condition requires a value.";
                return false;
            }

            return true;
        }

        if (kind == "group")
        {
            var groupOperator = node.TryGetProperty("operator", out var groupOp) ? groupOp.GetString() : null;
            if (groupOperator is not ("and" or "or"))
            {
                error = "A group operator must be 'and' or 'or'.";
                return false;
            }

            if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            {
                error = "A group requires a children array.";
                return false;
            }

            foreach (var child in children.EnumerateArray())
            {
                if (!ValidateRuleTree(child, depth + 1, out error))
                {
                    return false;
                }
            }

            return true;
        }

        error = $"Unknown rule node kind '{kind}'.";
        return false;
    }
}
