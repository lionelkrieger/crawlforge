using System.Globalization;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.Scoping;

/// <summary>
/// Persistence for reusable project-level content scopes (FS-CRAWL-002 §4.4,
/// content_scope_profiles). Configuration data — not pruned by audit retention.
/// Selectors are validated at configuration time before any write; payloads
/// carry ordered entries of language + expression + order.
/// </summary>
public sealed class ContentScopeProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IReadRepository<ContentScopeProfileDto> _read;
    private readonly IWriteRepository<ContentScopeProfileDto> _write;

    /// <summary>Creates the store over the locked repositories.</summary>
    /// <param name="read">Read repository.</param>
    /// <param name="write">Write repository.</param>
    public ContentScopeProfileStore(
        IReadRepository<ContentScopeProfileDto> read,
        IWriteRepository<ContentScopeProfileDto> write)
    {
        _read = read;
        _write = write;
    }

    /// <summary>Creates a validated project scope profile.</summary>
    /// <param name="projectId">Owning project.</param>
    /// <param name="name">Profile name.</param>
    /// <param name="scope">Include/exclude scope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted profile.</returns>
    /// <exception cref="ContentScopeValidationException">Any selector is invalid.</exception>
    public async Task<ContentScopeProfileDto> CreateAsync(
        long projectId,
        string name,
        ContentScopeDefinition scope,
        CancellationToken cancellationToken = default)
    {
        ContentScopeValidator.Validate(scope);
        var now = DateTimeOffset.UtcNow;
        var dto = new ContentScopeProfileDto(
            Id: 0,
            ProjectId: projectId,
            Name: name,
            IncludeSelectorsJson: SerializeEntries(scope.IncludeSelectors),
            ExcludeSelectorsJson: SerializeEntries(scope.ExcludeSelectors),
            CreatedAt: now,
            UpdatedAt: now);
        return await _write.CreateAsync(dto, cancellationToken);
    }

    /// <summary>Updates a profile's name and/or scope after validation.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="name">New profile name.</param>
    /// <param name="scope">New include/exclude scope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated profile, or null when absent.</returns>
    /// <exception cref="ContentScopeValidationException">Any selector is invalid.</exception>
    public async Task<ContentScopeProfileDto?> UpdateAsync(
        long profileId,
        string name,
        ContentScopeDefinition scope,
        CancellationToken cancellationToken = default)
    {
        ContentScopeValidator.Validate(scope);
        var existing = await _read.GetByIdAsync(profileId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var updated = existing with
        {
            Name = name,
            IncludeSelectorsJson = SerializeEntries(scope.IncludeSelectors),
            ExcludeSelectorsJson = SerializeEntries(scope.ExcludeSelectors),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return await _write.UpdateAsync(updated, cancellationToken);
    }

    /// <summary>Reads one profile.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile, or null when absent.</returns>
    public Task<ContentScopeProfileDto?> GetAsync(long profileId, CancellationToken cancellationToken = default) =>
        _read.GetByIdAsync(profileId, cancellationToken);

    /// <summary>Lists a project's profiles.</summary>
    /// <param name="projectId">Owning project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The project's profiles.</returns>
    public async Task<IReadOnlyList<ContentScopeProfileDto>> ListByProjectAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        var page = await _read.ListAsync(
            new ListQuery(
                Limit: 1000,
                Filters: new Dictionary<string, string>
                {
                    ["ProjectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                },
                SortBy: nameof(ContentScopeProfileDto.Name)),
            cancellationToken);
        return page.Items;
    }

    /// <summary>Deletes a profile.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when deleted.</returns>
    public Task<bool> DeleteAsync(long profileId, CancellationToken cancellationToken = default) =>
        _write.DeleteAsync(profileId, cancellationToken);

    /// <summary>Resolves a profile reference to its scope definition.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scope, or null when the profile is absent.</returns>
    public async Task<ContentScopeDefinition?> ResolveDefinitionAsync(
        long profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _read.GetByIdAsync(profileId, cancellationToken);
        return profile is null ? null : DeserializeDefinition(profile);
    }

    /// <summary>Deserializes a profile row into a scope definition.</summary>
    /// <param name="profile">Profile row.</param>
    /// <returns>The scope definition in declared order.</returns>
    public static ContentScopeDefinition DeserializeDefinition(ContentScopeProfileDto profile) =>
        new(
            DeserializeEntries(profile.IncludeSelectorsJson),
            DeserializeEntries(profile.ExcludeSelectorsJson));

    private static string SerializeEntries(IReadOnlyList<string> selectors)
    {
        var entries = selectors
            .Select((expression, order) => new ScopeSelectorEntry(
                expression,
                SelectorLanguageDetector.Detect(expression),
                order))
            .ToArray();
        return JsonSerializer.Serialize(entries, JsonOptions);
    }

    private static IReadOnlyList<string> DeserializeEntries(string json)
    {
        var entries = JsonSerializer.Deserialize<ScopeSelectorEntry[]>(json, JsonOptions) ?? [];
        return entries
            .OrderBy(entry => entry.Order)
            .Select(entry => entry.Expression)
            .ToArray();
    }
}
