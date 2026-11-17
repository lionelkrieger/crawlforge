using System.Text.Json.Nodes;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// API-surface display metadata sidecar. The locked storage schema carries no
/// name/timestamp columns for schedule_definitions, export_destinations, or
/// saved_views; the wire contracts require them. This store keeps that
/// display metadata in an API-owned key (<c>apiSurface</c>) inside the existing
/// <c>application_settings.ui_preferences_json</c> column — the same
/// behavior-document pattern approved for configuration and settings payloads.
/// Engine-owned payloads are never touched. Single-user desktop semantics:
/// read-modify-write without cross-process locking.
/// </summary>
internal sealed class ApiSurfaceMetadataStore
{
    private const string RootKey = "apiSurface";

    private readonly IReadRepository<ApplicationSettingsDto> _reads;
    private readonly IWriteRepository<ApplicationSettingsDto> _writes;

    /// <summary>Creates the store.</summary>
    /// <param name="reads">Settings read repository.</param>
    /// <param name="writes">Settings write repository.</param>
    public ApiSurfaceMetadataStore(IReadRepository<ApplicationSettingsDto> reads, IWriteRepository<ApplicationSettingsDto> writes)
    {
        _reads = reads;
        _writes = writes;
    }

    /// <summary>One metadata entry.</summary>
    /// <param name="Name">Optional display name.</param>
    /// <param name="CreatedAt">Creation timestamp.</param>
    /// <param name="UpdatedAt">Last-update timestamp.</param>
    public sealed record Entry(string? Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    /// <summary>Loads the apiSurface root object (empty when absent).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mutable root object.</returns>
    public async Task<JsonObject> LoadAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var preferences = ProjectWireMapper.ParseObject(settings.UiPreferencesJson);
        return preferences[RootKey]?.DeepClone() as JsonObject ?? [];
    }

    /// <summary>Persists the apiSurface root, preserving other UI preferences.</summary>
    /// <param name="root">Root object to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when persisted.</returns>
    public async Task SaveAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var preferences = ProjectWireMapper.ParseObject(settings.UiPreferencesJson);
        preferences[RootKey] = root.DeepClone();
        await _writes.UpdateAsync(settings with { UiPreferencesJson = preferences.ToJsonString(ApiJsonOptions.Default) }, cancellationToken);
    }

    /// <summary>Reads one entry.</summary>
    /// <param name="root">Loaded root.</param>
    /// <param name="section">Section key (schedules, destinations, queries).</param>
    /// <param name="id">Entity identifier.</param>
    /// <returns>The entry, or null when absent.</returns>
    public static Entry? ReadEntry(JsonObject root, string section, string id)
    {
        if (root[section]?[id] is not JsonObject node)
        {
            return null;
        }

        return new Entry(
            node["name"]?.GetValue<string>(),
            node["createdAt"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UnixEpoch,
            node["updatedAt"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UnixEpoch);
    }

    /// <summary>Writes one entry into the root.</summary>
    /// <param name="root">Loaded root.</param>
    /// <param name="section">Section key.</param>
    /// <param name="id">Entity identifier.</param>
    /// <param name="entry">Entry to store.</param>
    public static void WriteEntry(JsonObject root, string section, string id, Entry entry)
    {
        if (root[section] is not JsonObject sectionNode)
        {
            sectionNode = [];
            root[section] = sectionNode;
        }

        var node = new JsonObject
        {
            ["createdAt"] = entry.CreatedAt,
            ["updatedAt"] = entry.UpdatedAt,
        };
        if (entry.Name is not null)
        {
            node["name"] = entry.Name;
        }

        sectionNode[id] = node;
    }

    /// <summary>Removes one entry from the root.</summary>
    /// <param name="root">Loaded root.</param>
    /// <param name="section">Section key.</param>
    /// <param name="id">Entity identifier.</param>
    public static void RemoveEntry(JsonObject root, string section, string id)
    {
        if (root[section] is JsonObject sectionNode)
        {
            sectionNode.Remove(id);
        }
    }

    private async Task<ApplicationSettingsDto> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var page = await _reads.ListAsync(new ListQuery(0, 1), cancellationToken);
        if (page.Items.Count > 0)
        {
            return page.Items[0];
        }

        var created = new ApplicationSettingsDto(
            0, "{}", new CrawlLimitOverrides(null, null, null, null, null, null), "{}", "{}",
            DatabaseProviderKind.Sqlite, null, "dark", "en");
        return await _writes.CreateAsync(created, cancellationToken);
    }
}
