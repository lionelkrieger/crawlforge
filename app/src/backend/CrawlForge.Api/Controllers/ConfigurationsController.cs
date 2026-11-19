using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Canonicalization;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Audit-configuration CRUD (FS-CRAWL-001 §10.2): list/create under a project,
/// item read/update/delete. Configuration fields compile the §5.1 surface
/// (seeds, limits, AIMD, rendering, robots) plus additive extension settings.
/// The enabledCategories convention (Scoring README) is written into the audit
/// snapshot at audit start, not here.
/// </summary>
[Route("api")]
public sealed class ConfigurationsController : ApiControllerBase
{
    private static readonly string[] SortFields = ["id", "name", "createdAt", "updatedAt"];

    private readonly IReadRepository<ProjectDto> _projects;
    private readonly IReadRepository<CrawlConfigurationDto> _reads;
    private readonly IWriteRepository<CrawlConfigurationDto> _writes;
    private readonly IReadRepository<ContentScopeProfileDto> _profiles;

    /// <summary>Creates the controller.</summary>
    /// <param name="projects">Project read repository (parent existence checks).</param>
    /// <param name="reads">Configuration read repository.</param>
    /// <param name="writes">Configuration write repository.</param>
    /// <param name="profiles">Content-scope profile read repository.</param>
    public ConfigurationsController(
        IReadRepository<ProjectDto> projects,
        IReadRepository<CrawlConfigurationDto> reads,
        IWriteRepository<CrawlConfigurationDto> writes,
        IReadRepository<ContentScopeProfileDto> profiles)
    {
        _projects = projects;
        _reads = reads;
        _writes = writes;
        _profiles = profiles;
    }

    /// <summary>Lists a project's configurations.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="sort">Optional sort field.</param>
    /// <param name="order">Sort direction: asc or desc.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged configuration summaries.</returns>
    [HttpGet("projects/{id:long}/configurations")]
    public async Task<ObjectResult> ListConfigurationsAsync(
        long id,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetByIdAsync(id, cancellationToken);
        if (project is null || project.IsDeleted)
        {
            return NotFoundError($"Project {id} does not exist.");
        }

        if (!TryBindSort(sort, order, SortFields, out var sortBy, out var descending, out var sortError))
        {
            return ValidationError(sortError!);
        }

        var query = BindListQuery(offset, limit, sortBy, descending, new Dictionary<string, string> { ["projectId"] = id.ToString() });
        var page = await _reads.ListAsync(query, cancellationToken);
        var summaries = page.Items.Select(ProjectWireMapper.ToConfigurationSummary).ToList();
        return OkEnvelope(summaries, PaginationOf(page));
    }

    /// <summary>Creates a configuration under a project.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="request">Creation payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created configuration.</returns>
    [HttpPost("projects/{id:long}/configurations")]
    public async Task<ObjectResult> CreateConfigurationAsync(long id, [FromBody] ConfigurationCreateRequest request, CancellationToken cancellationToken)
    {
        var project = await _projects.GetByIdAsync(id, cancellationToken);
        if (project is null || project.IsDeleted)
        {
            return NotFoundError($"Project {id} does not exist.");
        }

        var validation = ValidateCreate(request);
        if (validation is not null)
        {
            return validation;
        }

        var now = DateTimeOffset.UtcNow;
        var behavior = BuildBehavior(
            request.Description,
            request.RespectRobotsTxt ?? true,
            request.VirtualRobotsTxt,
            request.Limits?.MaxResponseBytes,
            request.Rendering);

        // WP-09-06 (WP-09-07 §3): additive editor document merged into ScopeRulesJson.
        MergeScopeRules(behavior, request.ScopeRules, request.RespectRobotsTxt);
        if (!TryNormalizeCategories(request.EnabledCategories, out var categories, out var categoryError))
        {
            return ValidationError(categoryError!, [new ApiFieldError("enabledCategories", categoryError!)]);
        }

        if (request.EnabledCategories is not null)
        {
            ApplyEnabledCategories(behavior, categories);
        }

        if (ValidateScopeDocument(behavior) is { } scopeError)
        {
            return scopeError;
        }
        var limits = request.Limits is null
            ? null
            : new CrawlLimitOverrides(request.Limits.MaxUrls, request.Limits.MaxDepth, request.Limits.MaxDurationSeconds);

        var configuration = new CrawlConfigurationDto(
            0,
            id,
            request.Name.Trim(),
            JsonSerializer.Serialize(request.StartUrls),
            behavior.ToJsonString(),
            request.Limits?.MaxDepth,
            limits,
            request.Rendering?.Enabled == true ? RenderMode.Auto : RenderMode.None,
            ProjectWireMapper.SerializeAimd(request.Aimd ?? ProjectWireMapper.DefaultAimd),
            null,
            null,
            null,
            null,
            now,
            now);
        configuration = ProjectWireMapper.ApplyExtensions(configuration, request.ContentScope, request.Rewrite, request.Consent, request.ZoneWeights);

        var created = await _writes.CreateAsync(configuration, cancellationToken);
        return CreatedEnvelope(ProjectWireMapper.ToConfiguration(created, await LoadProfilesAsync(id, cancellationToken)));
    }

    /// <summary>Reads one configuration.</summary>
    /// <param name="id">Configuration identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The configuration record.</returns>
    [HttpGet("configurations/{id:long}")]
    public async Task<ObjectResult> GetConfigurationAsync(long id, CancellationToken cancellationToken)
    {
        var configuration = await _reads.GetByIdAsync(id, cancellationToken);
        if (configuration is null)
        {
            return NotFoundError($"Configuration {id} does not exist.");
        }

        return OkEnvelope(ProjectWireMapper.ToConfiguration(configuration, await LoadProfilesAsync(configuration.ProjectId, cancellationToken)));
    }

    /// <summary>Updates any subset of configuration fields.</summary>
    /// <param name="id">Configuration identifier.</param>
    /// <param name="request">Subset payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated configuration.</returns>
    [HttpPut("configurations/{id:long}")]
    public async Task<ObjectResult> UpdateConfigurationAsync(long id, [FromBody] ConfigurationUpdateRequest request, CancellationToken cancellationToken)
    {
        var configuration = await _reads.GetByIdAsync(id, cancellationToken);
        if (configuration is null)
        {
            return NotFoundError($"Configuration {id} does not exist.");
        }

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ValidationError("The configuration name must not be empty.", [new ApiFieldError("name", "Must not be empty.")]);
            }

            configuration = configuration with { Name = request.Name.Trim() };
        }

        if (request.StartUrls is not null)
        {
            var urlError = ValidateStartUrls(request.StartUrls);
            if (urlError is not null)
            {
                return urlError;
            }

            configuration = configuration with { SeedsJson = JsonSerializer.Serialize(request.StartUrls) };
        }

        var behavior = ProjectWireMapper.ParseObject(configuration.ScopeRulesJson);

        // WP-09-06 (WP-09-07 §3): shallow top-level merge FIRST; the explicit
        // request-field deltas below win over any behavior keys in the document.
        MergeScopeRules(behavior, request.ScopeRules, request.RespectRobotsTxt);
        if (!TryNormalizeCategories(request.EnabledCategories, out var categories, out var categoryError))
        {
            return ValidationError(categoryError!, [new ApiFieldError("enabledCategories", categoryError!)]);
        }

        if (request.Description is not null)
        {
            if (request.Description.Length == 0)
            {
                behavior.Remove("description");
            }
            else
            {
                behavior["description"] = request.Description;
            }
        }

        if (request.RespectRobotsTxt is not null)
        {
            behavior["respectRobotsTxt"] = request.RespectRobotsTxt;
        }

        if (request.VirtualRobotsTxt is not null)
        {
            if (request.VirtualRobotsTxt.Length == 0)
            {
                behavior.Remove("virtualRobotsTxt");
            }
            else
            {
                behavior["virtualRobotsTxt"] = request.VirtualRobotsTxt;
            }
        }

        if (request.Limits is not null)
        {
            var limitsError = ValidateLimits(request.Limits);
            if (limitsError is not null)
            {
                return limitsError;
            }

            configuration = configuration with
            {
                LimitOverrides = new CrawlLimitOverrides(request.Limits.MaxUrls, request.Limits.MaxDepth, request.Limits.MaxDurationSeconds),
                Depth = request.Limits.MaxDepth,
            };
            if (request.Limits.MaxResponseBytes is null)
            {
                behavior.Remove("maxResponseBytes");
            }
            else
            {
                behavior["maxResponseBytes"] = request.Limits.MaxResponseBytes;
            }
        }

        if (request.Aimd is not null)
        {
            var aimdError = ValidateAimd(request.Aimd);
            if (aimdError is not null)
            {
                return aimdError;
            }

            configuration = configuration with { PolitenessJson = ProjectWireMapper.SerializeAimd(request.Aimd) };
        }

        if (request.Rendering is not null)
        {
            configuration = configuration with { RenderingMode = request.Rendering.Enabled ? RenderMode.Auto : RenderMode.None };
            behavior["rendering"] = new JsonObject
            {
                ["deviceProfile"] = request.Rendering.DeviceProfile,
                ["infiniteScroll"] = request.Rendering.InfiniteScroll,
                ["captureConsole"] = request.Rendering.CaptureConsole,
            };
        }

        if (request.EnabledCategories is not null)
        {
            // Non-null replaces the persisted set; empty array = all enabled.
            ApplyEnabledCategories(behavior, categories);
        }

        if (ValidateScopeDocument(behavior) is { } scopeError)
        {
            return scopeError;
        }

        configuration = configuration with { ScopeRulesJson = behavior.ToJsonString(), UpdatedAt = DateTimeOffset.UtcNow };
        configuration = ProjectWireMapper.ApplyExtensions(configuration, request.ContentScope, request.Rewrite, request.Consent, request.ZoneWeights);

        var updated = await _writes.UpdateAsync(configuration, cancellationToken);
        return OkEnvelope(ProjectWireMapper.ToConfiguration(updated, await LoadProfilesAsync(updated.ProjectId, cancellationToken)));
    }

    /// <summary>Deletes a configuration.</summary>
    /// <param name="id">Configuration identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deletion confirmation.</returns>
    [HttpDelete("configurations/{id:long}")]
    public async Task<ObjectResult> DeleteConfigurationAsync(long id, CancellationToken cancellationToken)
    {
        var configuration = await _reads.GetByIdAsync(id, cancellationToken);
        if (configuration is null)
        {
            return NotFoundError($"Configuration {id} does not exist.");
        }

        await _writes.DeleteAsync(id, cancellationToken);
        return OkEnvelope(new DeletionConfirmationWire(id.ToString(), true));
    }

    private ObjectResult? ValidateCreate(ConfigurationCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationError("The configuration name is required.", [new ApiFieldError("name", "Must not be empty.")]);
        }

        var urlError = ValidateStartUrls(request.StartUrls);
        if (urlError is not null)
        {
            return urlError;
        }

        if (request.Limits is not null)
        {
            var limitsError = ValidateLimits(request.Limits);
            if (limitsError is not null)
            {
                return limitsError;
            }
        }

        if (request.Aimd is not null)
        {
            var aimdError = ValidateAimd(request.Aimd);
            if (aimdError is not null)
            {
                return aimdError;
            }
        }

        return null;
    }

    private ObjectResult? ValidateStartUrls(IReadOnlyList<string>? startUrls)
    {
        if (startUrls is null || startUrls.Count == 0)
        {
            return ValidationError("At least one seed URL is required.", [new ApiFieldError("startUrls", "Must contain at least one URL.")]);
        }

        var fieldErrors = new List<ApiFieldError>();
        for (var index = 0; index < startUrls.Count; index++)
        {
            if (!Uri.TryCreate(startUrls[index], UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                fieldErrors.Add(new ApiFieldError($"startUrls[{index}]", "Must be an absolute http or https URL."));
            }
        }

        return fieldErrors.Count > 0
            ? ValidationError("One or more seed URLs are invalid.", fieldErrors)
            : null;
    }

    private ObjectResult? ValidateLimits(CrawlLimitsWire limits)
    {
        var fieldErrors = new List<ApiFieldError>();
        if (limits.MaxUrls is < 1)
        {
            fieldErrors.Add(new ApiFieldError("limits.maxUrls", "Must be positive or null."));
        }

        if (limits.MaxDepth is < 0)
        {
            fieldErrors.Add(new ApiFieldError("limits.maxDepth", "Must be non-negative or null."));
        }

        if (limits.MaxDurationSeconds is < 1)
        {
            fieldErrors.Add(new ApiFieldError("limits.maxDurationSeconds", "Must be positive or null."));
        }

        if (limits.MaxResponseBytes is < 1)
        {
            fieldErrors.Add(new ApiFieldError("limits.maxResponseBytes", "Must be positive or null."));
        }

        return fieldErrors.Count > 0
            ? ValidationError("One or more crawl limits are invalid.", fieldErrors)
            : null;
    }

    private ObjectResult? ValidateAimd(AimdSettingsWire aimd)
    {
        var fieldErrors = new List<ApiFieldError>();
        if (aimd.InitialRateR0 <= 0)
        {
            fieldErrors.Add(new ApiFieldError("aimd.initialRateR0", "Must be positive."));
        }

        if (aimd.Alpha <= 0)
        {
            fieldErrors.Add(new ApiFieldError("aimd.alpha", "Must be positive."));
        }

        if (aimd.Beta is <= 0 or >= 1)
        {
            fieldErrors.Add(new ApiFieldError("aimd.beta", "Must be in (0, 1)."));
        }

        if (aimd.Kappa < 1)
        {
            fieldErrors.Add(new ApiFieldError("aimd.kappa", "Must be at least 1."));
        }

        if (aimd.EwmaTtfbWeight is <= 0 or > 1)
        {
            fieldErrors.Add(new ApiFieldError("aimd.ewmaTtfbWeight", "Must be in (0, 1]."));
        }

        return fieldErrors.Count > 0
            ? ValidationError("One or more AIMD parameters are invalid.", fieldErrors)
            : null;
    }

    private static JsonObject BuildBehavior(string? description, bool respectRobotsTxt, string? virtualRobotsTxt, long? maxResponseBytes, RenderingSettingsWire? rendering)
    {
        var behavior = new JsonObject
        {
            ["respectRobotsTxt"] = respectRobotsTxt,
        };
        if (description is not null)
        {
            behavior["description"] = description;
        }

        if (virtualRobotsTxt is not null)
        {
            behavior["virtualRobotsTxt"] = virtualRobotsTxt;
        }

        if (maxResponseBytes is not null)
        {
            behavior["maxResponseBytes"] = maxResponseBytes;
        }

        if (rendering is not null)
        {
            behavior["rendering"] = new JsonObject
            {
                ["deviceProfile"] = rendering.DeviceProfile,
                ["infiniteScroll"] = rendering.InfiniteScroll,
                ["captureConsole"] = rendering.CaptureConsole,
            };
        }

        return behavior;
    }

    /// <summary>Behavior keys owned by the explicit request fields (WP-09-07 §3 preservation rule).</summary>
    private static readonly string[] BehaviorKeys = ["description", "respectRobotsTxt", "virtualRobotsTxt", "maxResponseBytes", "rendering"];

    /// <summary>
    /// Shallow top-level merge of the additive editor document into the behavior
    /// object. Editor sections (canonicalization/scope/directives/resources/cookieJar/
    /// userAgent/userAgentComparison/headers/cookies) are copied verbatim; behavior
    /// keys stay owned by the explicit request fields — except respectRobotsTxt,
    /// which falls back to the document when the request omitted the flag.
    /// </summary>
    private static void MergeScopeRules(JsonObject behavior, JsonElement? scopeRules, bool? respectRobotsTxtExplicit)
    {
        if (scopeRules is not { ValueKind: JsonValueKind.Object } document)
        {
            return;
        }

        foreach (var property in document.EnumerateObject())
        {
            if (BehaviorKeys.Contains(property.Name))
            {
                if (property.Name == "respectRobotsTxt" && respectRobotsTxtExplicit is null)
                {
                    behavior[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }

                continue;
            }

            behavior[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
    }

    /// <summary>
    /// Normalizes additive enabledCategories to canonical PascalCase AuditCategory
    /// names (case-insensitive parse, deduplicated). Null/empty = all enabled (null).
    /// Unknown names are validation errors at configuration time.
    /// </summary>
    private static bool TryNormalizeCategories(IReadOnlyList<string>? names, out IReadOnlyList<string>? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (names is null || names.Count == 0)
        {
            return true;
        }

        var list = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (!Enum.TryParse<AuditCategory>(name, ignoreCase: true, out var category))
            {
                error = $"Unknown analysis category '{name}'.";
                return false;
            }

            var canonical = category.ToString();
            if (!list.Contains(canonical))
            {
                list.Add(canonical);
            }
        }

        normalized = list;
        return true;
    }

    /// <summary>Writes (or clears, for null = all) the enabledCategories document member.</summary>
    private static void ApplyEnabledCategories(JsonObject behavior, IReadOnlyList<string>? normalized)
    {
        if (normalized is null)
        {
            behavior.Remove("enabledCategories");
            return;
        }

        behavior["enabledCategories"] = new JsonArray(normalized.Select(name => (JsonNode)name).ToArray());
    }

    /// <summary>
    /// Validates the merged crawl-scope document with the total/tolerant parser;
    /// hard errors (invalid regexes, out-of-range sample sizes) become a
    /// validation envelope under <c>scopeRules</c> (WP-09-07 §3).
    /// </summary>
    private ObjectResult? ValidateScopeDocument(JsonObject behavior)
    {
        var errors = CrawlScopeConfigParser.Parse(behavior.ToJsonString()).Errors;
        return errors.Count == 0
            ? null
            : ValidationError("Invalid crawl scope configuration.", [new ApiFieldError("scopeRules", string.Join(" ", errors))]);
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
